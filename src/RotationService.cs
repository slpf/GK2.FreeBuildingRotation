using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GK2.FreeBuildingRotation;

internal sealed class RotationService
{
    private static readonly FieldInfo Sequence = RequiredField(typeof(WgoPart), "customRotationSequence");
    private static readonly FieldInfo ApplyDockRotation = RequiredField(typeof(DockPoint), "applyRotationToDirection");
    private static readonly FieldInfo CellsGridMask = RequiredField(typeof(BuildPointerObject), "cellsGridMask");
    private static readonly FieldInfo GdDirection = RequiredField(typeof(GDPoint), "direction");
    private static readonly FieldInfo PartObject = RequiredField(typeof(WgoPart), "object3D");
    private readonly Stack<(string TargetId, string Variation, int Index, Vector3 Position, string World)> replacements = new();
    private readonly ManualLogSource log;
    private readonly ConfigEntry<bool> debug;
    private readonly HashSet<string> excluded;
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> skipReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WGODef> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RotationPlan?> plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> readyFamilies = new(StringComparer.Ordinal);
    private readonly HashSet<string> rejectedFamilies = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<WgoPart, PartState> instances = new();
    private bool catalogReady;

    internal RotationService(ManualLogSource log, ConfigEntry<bool> debug, string excludedIds)
    {
        this.log = log;
        this.debug = debug;
        excluded = new HashSet<string>(excludedIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()), StringComparer.Ordinal);
    }

    internal void PrepareData(WgoData data)
    {
        try
        {
            if (data == null || data.MainWgoPartData == null)
                return;
            EnsureCatalog();
            if (!families.ContainsKey(data.id))
                return;
            ValidateInstance(data);
            if (!EnsureFamily(data.id))
                return;
            foreach (WgoPartData part in data.AdditionalWgoPartsData)
                PrepareAdditionalPart(data, part);
            if (plans.TryGetValue(data.MainWgoPartData.id, out RotationPlan? plan) && plan != null && plan.Baked.HasChunkBounds)
            {
                ChunkBoundsPair bounds = plan.Baked.ChunkBounds;
                foreach (WgoPartData part in data.AdditionalWgoPartsData)
                    if (part.BakedData?.HasChunkBounds == true)
                    {
                        bounds.withShadows.Encapsulate(part.BakedData.ChunkBounds.withShadows);
                        bounds.withoutShadows.Encapsulate(part.BakedData.ChunkBounds.withoutShadows);
                    }
                data.SetSerializedBounds(bounds);
            }
        }
        catch (Exception error)
        {
            ReportSkip(data?.id ?? "unknown", error);
        }
    }

    internal void PrepareVisuals(WgoPart part, WgoPartData data, WGODef definition)
    {
        try
        {
            if (part == null || data == null || definition == null)
                return;
            ResetVisuals(part);
            EnsureCatalog();
            if (!families.ContainsKey(definition.id))
                return;
            if (part.Wgo != null)
            {
                ValidateInstance(part.Wgo.Data);
                if (!ReferenceEquals(part.Wgo.Data.MainWgoPartData, data))
                    PrepareAdditionalPart(part.Wgo.Data, data);
                else
                    foreach (WgoPartData additional in part.Wgo.Data.AdditionalWgoPartsData)
                        PrepareAdditionalPart(part.Wgo.Data, additional);
            }
            if (!EnsureFamily(definition.id) || !plans.TryGetValue(data.id, out RotationPlan? plan) || plan == null || plan.Groups.Count == 0)
                return;
            RotationPlan.RequireIdentity(part.transform);
            var state = new PartState(part, plan);
            try
            {
                state.Install();
                data.PrepareForGame();
                instances.Add(part, state);
            }
            catch
            {
                state.Uninstall();
                throw;
            }
        }
        catch (Exception error)
        {
            ReportSkip(data?.id ?? "unknown visual", error);
        }
    }

    internal void ApplyState(WgoPart part, WgoPartState state)
    {
        if (!instances.TryGetValue(part, out PartState? instance))
            return;
        try
        {
            instance.Apply(state);
        }
        catch (Exception error)
        {
            instance.Reset();
            log.LogError($"Could not apply orientation for {part.Id}: {error}");
        }
    }

    internal void ResetVisuals(WgoPart part)
    {
        if (instances.TryGetValue(part, out PartState? instance))
        {
            instance.Uninstall();
            instances.Remove(part);
        }
    }

    internal void ApplyDockDirection(WgoPart part, WgoPartState state)
    {
        if (!instances.TryGetValue(part, out PartState? instance))
            return;
        try
        {
            instance.ApplyDockDirection(state);
            if (part.Wgo != null && ReferenceEquals(part.Wgo.MainWgoPart, part) && state.variationId.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal))
            {
                foreach (WgoPartData additional in part.Wgo.Data.AdditionalWgoPartsData)
                    PrepareAdditionalPart(part.Wgo.Data, additional);
                foreach (WgoPart additional in part.Wgo.AdditionalWgoParts)
                    additional.ApplyWgoPartState();
            }
        }
        catch (Exception error)
        {
            log.LogError($"Could not apply access direction for {part.Id}: {error}");
        }
    }

    internal bool CanRotate(WgoPart part, string variationId)
    {
        return instances.TryGetValue(part, out PartState? instance) && instance.CanRotate(variationId);
    }

    internal bool EnterRotationGroup(WgoPart part, bool inReverse)
    {
        return instances.TryGetValue(part, out PartState? instance) && instance.EnterRotationGroup(inReverse);
    }

    internal void RestoreVariation(WgoData data, string? variationId)
    {
        if (variationId == null || !variationId.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal))
            return;
        try
        {
            PrepareData(data);
            WgoPartData part = data.MainWgoPartData;
            if (part != null && readyFamilies.Contains(data.id) && plans.TryGetValue(part.id, out RotationPlan? plan) &&
                plan != null && plan.Groups.Any(group => group.Id == variationId))
                part.variationId = variationId;
        }
        catch (Exception error)
        {
            log.LogError($"Could not preserve orientation for {data.id}: {error}");
        }
    }

    internal void RestoreOrientation(WgoData data, string? variationId, int index)
    {
        if (variationId?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) != true || index < 0 || index > 3)
            return;
        try
        {
            RestoreVariation(data, variationId);
            if (data.MainWgoPartData?.variationId == variationId)
            {
                data.MainWgoPartData.rotationIndex = index;
                data.MainWgoPartData.PrepareForGame();
                foreach (WgoPartData part in data.AdditionalWgoPartsData)
                    PrepareAdditionalPart(data, part);
            }
        }
        catch (Exception error)
        {
            ReportSkip(data.id, error);
        }
    }

    internal bool BeginReplacement(WgoData source, string targetId)
    {
        string? variation = source.MainWgoPartData?.variationId;
        if (variation?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) != true)
            return false;
        replacements.Push((targetId, variation, source.MainWgoPartData!.rotationIndex, source.Position, source.WorldId));
        return true;
    }

    internal void EndReplacement(bool active)
    {
        if (active && replacements.Count != 0)
            replacements.Pop();
    }

    internal void PrepareReplacement(WgoData data)
    {
        if (replacements.Count == 0)
            return;
        var replacement = replacements.Peek();
        if (data.id == replacement.TargetId && data.WorldId == replacement.World &&
            (data.Position - replacement.Position).sqrMagnitude < 0.000001f)
            RestoreOrientation(data, replacement.Variation, replacement.Index);
    }

    private void PrepareAdditionalPart(WgoData owner, WgoPartData data)
    {
        if (excluded.Contains(data.id))
            throw new InvalidOperationException("Additional part excluded by configuration: " + data.id);
        RotationPlan plan = LoadPlan(data.id);
        LazySingletonSerializedSO<WgoPartBakedDataCollection>.Instance.AddOrUpdate(plan.Baked);
        string? orientation = owner.MainWgoPartData?.variationId;
        if (orientation?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) == true)
        {
            RotationPlan.Group? group = plan.Groups.Find(value => value.NativeId == (data.variationId ?? string.Empty) || value.Id == data.variationId);
            if (group == null)
                throw new InvalidOperationException("Additional part has no compatible variation: " + data.id);
            data.variationId = group.Id;
            data.rotationIndex = owner.MainWgoPartData!.rotationIndex;
        }
        data.PrepareForGame();
    }

    internal void PrepareAddedPart(WgoData owner, string asset, ref string variation, ref int index)
    {
        if (owner.MainWgoPartData?.variationId?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) != true)
            return;
        try
        {
            var data = new WgoPartData(asset, variation, index);
            PrepareAdditionalPart(owner, data);
            variation = data.variationId;
            index = data.rotationIndex;
        }
        catch (Exception error)
        {
            ReportSkip(asset, error);
        }
    }

    internal bool TryGetDockData(WgoPart part, DockPoint point, out DockPointData? data)
    {
        data = null;
        return instances.TryGetValue(part, out PartState? instance) && instance.TryGetDockData(point, out data);
    }

    internal bool TryRotateDirection(WgoPart part, Direction direction, out Direction rotated)
    {
        rotated = direction;
        if (part == null || !instances.TryGetValue(part, out PartState? state) || !state.TryGetTurns(out int turns))
            return false;
        rotated = BakedRotation.RotateDirection(direction, turns);
        return true;
    }

    internal bool ApplyAreaRotation(WgoBuildPointer pointer, Wgo built)
    {
        try
        {
            return TryApplyAreaRotation(pointer, built);
        }
        catch (Exception error)
        {
            ReportSkip(built?.Id ?? "attachment placement", error);
            return false;
        }
    }

    private bool TryApplyAreaRotation(WgoBuildPointer pointer, Wgo built)
    {
        if (built?.MainWgoPart == null || !instances.TryGetValue(built.MainWgoPart, out PartState? target))
            return false;
        Bounds bounds = pointer.GetWorldRoundedBounds();
        Vector3 extents = bounds.extents / 4f;
        extents.y = 0.5f;
        foreach (Collider collider in Physics.OverlapBox(bounds.center, extents, Quaternion.identity, 1 << 19))
        {
            BuildArea area = collider.GetComponent<BuildArea>();
            if (area == null || !area.HasRotationRequirement)
                continue;
            WgoPart parent = area.GetComponentInParent<WgoPart>();
            if (parent == null || !instances.TryGetValue(parent, out PartState? source) || !source.TryGetTurns(out int turns))
                continue;
            if (target.TryApplyAreaRotation(area.RotationRequirement, turns))
                return true;
        }
        return false;
    }

    internal void LogPlacement(WgoBuildPointer pointer)
    {
        if (!debug.Value)
            return;
        try
        {
            Wgo target = pointer.Target;
            if (target == null || target.MainWgoPart == null)
                return;
            WgoPart part = target.MainWgoPart;
            WgoPartData data = part.WgoPartData;
            if (data == null)
                return;
            string orientations = string.Join(",", part.Variations.Where(state => state.variationId == data.variationId)
                .Select(state => state.rotationIndex.ToString()));
            string generated = string.Join(",", part.Variations.Where(state => state?.variationId?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) == true)
                .Select(state => state.variationId + ":" + state.rotationIndex));
            string status = instances.TryGetValue(part, out _) ? "ready" : "vanilla";
            string reason = string.Empty;
            if (status == "vanilla" && !skipReasons.TryGetValue(target.Id, out reason) && !skipReasons.TryGetValue(data.id, out reason))
                reason = families.ContainsKey(target.Id) ? "Native four-state object or unsupported instance." : "Not an eligible buildable definition.";
            Rect footprint = data.GetCollisionBoundsRect(Vector3.zero);
            string points = string.Join("; ", data.GetDockPoints().Select(point =>
                $"offset={point.BakedData.Position}, facing={point.Direction}"));
            string live = string.Join("; ", part.GetComponentsInChildren<DockPoint>()
                .Select(point => $"world={point.transform.position}, facing={point.Direction}"));
            log.LogInfo($"Placement id={target.Id}, asset={data.id}, class={pointer.GetType().Name}, variation='{data.variationId}', " +
                $"index={data.rotationIndex}, allowed=[{orientations}], generated=[{generated}], status={status}, reason='{reason}', yaw={part.transform.localEulerAngles.y}, " +
                $"collision={footprint}, gridBounds={pointer.GetWorldRoundedBounds()}, {DescribeCells(pointer)}, bakedAccess=[{points}], liveAccess=[{live}]");
        }
        catch (Exception error)
        {
            ReportSkip("placement diagnostics", error);
        }
    }

    private void EnsureCatalog()
    {
        if (catalogReady)
            return;
        GameBalance balance = GameBalance.Me;
        if (balance == null || balance.wgoDefs == null || balance.buildingDefs == null)
            throw new InvalidOperationException("Game balance is not ready.");
        foreach (WGODef definition in balance.wgoDefs)
            if (definition != null && !string.IsNullOrEmpty(definition.id))
                definitions[definition.id] = definition;
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nonTransfer = new HashSet<string>(StringComparer.Ordinal);
        foreach (WGODef definition in definitions.Values)
        {
            IEnumerable<LazyExpression> expressions = (definition.customInteraction?.execution ?? new List<LazyExpression>())
                .Concat(definition.customInteraction2?.execution ?? new List<LazyExpression>())
                .Concat(definition.executeOnReplace ?? new List<LazyExpression>());
            foreach (LazyExpression expression in expressions)
                foreach (Match match in Regex.Matches(expression?.GetRawExpressionString() ?? string.Empty, "ChangeWgo\\(\\s*\"([A-Za-z0-9_]+)\"\\s*\\)"))
                    if (definitions.ContainsKey(match.Groups[1].Value))
                    {
                        AddEdge(edges, definition.id, match.Groups[1].Value);
                    }
        }
        foreach (WGODef definition in definitions.Values)
        {
            string? replacement = definition.replaceToWgoOnDie?.GetRawExpressionString();
            if (string.IsNullOrEmpty(replacement) || replacement == "0")
                continue;
            if (!definitions.ContainsKey(replacement))
            {
                nonTransfer.Add(definition.id);
                continue;
            }
            AddEdge(edges, definition.id, replacement);
        }
        foreach (BuildingDef building in balance.buildingDefs)
        {
            if (building == null || building.buildingMode != BuildingDef.BuildingMode.Place ||
                string.IsNullOrEmpty(building.wgoId) || string.IsNullOrEmpty(building.customWgoPlacePreview) ||
                !definitions.ContainsKey(building.wgoId))
                continue;
            if (!definitions.ContainsKey(building.customWgoPlacePreview))
            {
                nonTransfer.Add(building.wgoId);
                continue;
            }
            AddEdge(edges, building.wgoId, building.customWgoPlacePreview);
            AddEdge(edges, building.customWgoPlacePreview, building.wgoId);
        }
        foreach (CraftDef craft in balance.craftDefs)
        {
            if (craft == null || string.IsNullOrEmpty(craft.replaceWgoId) || craft.replaceWgoId == "0" || craft.craftsIn == null)
                continue;
            foreach (string source in craft.craftsIn)
            {
                if (!definitions.ContainsKey(source) || !definitions.ContainsKey(craft.replaceWgoId))
                    continue;
                AddEdge(edges, source, craft.replaceWgoId);
            }
        }
        foreach (BuildingDef building in balance.buildingDefs)
        {
            if (building == null || building.buildingMode != BuildingDef.BuildingMode.Place ||
                string.IsNullOrEmpty(building.wgoId) || !definitions.ContainsKey(building.wgoId))
                continue;
            var family = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(building.wgoId);
            while (queue.Count != 0)
            {
                string id = queue.Dequeue();
                if (!family.Add(id) || !edges.TryGetValue(id, out HashSet<string>? linked))
                    continue;
                foreach (string next in linked)
                    queue.Enqueue(next);
            }
            string? reason = null;
            foreach (string id in family)
            {
                reason ??= DefinitionProblem(definitions[id]);
                if (nonTransfer.Contains(id))
                    reason = "Construction replacement is dynamic or does not preserve orientation.";
            }
            if (reason != null)
            {
                rejectedFamilies.Add(building.wgoId);
                skipReasons[building.wgoId] = reason;
                ReportSkip(building.wgoId, new InvalidOperationException(reason));
                continue;
            }
            foreach (string id in family)
                if (!families.ContainsKey(id))
                    families[id] = family;
            families[building.wgoId] = family;
        }
        catalogReady = true;
        log.LogInfo($"Free Building Rotation catalog: {families.Count} candidate WGO definitions. Prefab compatibility is checked once per asset.");
    }

    private static string DescribeCells(WgoBuildPointer pointer)
    {
        if (CellsGridMask.GetValue(pointer) is not byte[,] mask)
            return "footprintCells=unavailable";
        int minX = mask.GetLength(0);
        int minZ = mask.GetLength(1);
        int maxX = -1;
        int maxZ = -1;
        int occupied = 0;
        int buff = 0;
        for (int x = 0; x < mask.GetLength(0); x++)
        {
            for (int z = 0; z < mask.GetLength(1); z++)
            {
                if (mask[x, z] == 2)
                    buff++;
                if (mask[x, z] != 1)
                    continue;
                minX = Math.Min(minX, x);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxZ = Math.Max(maxZ, z);
                occupied++;
            }
        }
        int width = occupied == 0 ? 0 : maxX - minX + 1;
        int height = occupied == 0 ? 0 : maxZ - minZ + 1;
        return $"footprintCells={width}x{height}, occupiedCells={occupied}, buffCells={buff}";
    }

    private string? DefinitionProblem(WGODef definition)
    {
        if (excluded.Contains(definition.id))
            return "Excluded by configuration.";
        if (!string.IsNullOrEmpty(definition.attachedScript) && definition.attachedScript != "alchemy_recipe_book" &&
            definition.attachedScript != "sawmill_wood_crafter" && definition.attachedScript != "mine_ore_coal_crafter")
            return "Attached world script requires an individual adapter: " + definition.attachedScript;
        if (!ConveyorRotation.Supports(definition.conveyorType))
            return "Conveyor routing or output-slot layout requires a separate adapter: " + definition.conveyorType;
        string asset = ResolveStaticAsset(definition);
        if (excluded.Contains(asset))
            return "Asset excluded by configuration.";
        if (string.IsNullOrEmpty(asset))
            return "Asset expression depends on runtime state.";
        return null;
    }

    private static string ResolveStaticAsset(WGODef definition)
    {
        if (!definition.hasCustomAssetId)
            return definition.id;
        string asset = definition.customAssetId?.GetRawExpressionString() ?? string.Empty;
        return Regex.IsMatch(asset, "^[A-Za-z0-9_]+$") ? asset : string.Empty;
    }

    private bool EnsureFamily(string id)
    {
        if (rejectedFamilies.Contains(id))
            return false;
        if (readyFamilies.Contains(id))
            return true;
        if (!families.TryGetValue(id, out HashSet<string>? family))
            return false;
        try
        {
            var familyPlans = new List<RotationPlan>();
            foreach (string member in family)
            {
                string asset = ResolveStaticAsset(definitions[member]);
                RotationPlan plan = LoadPlan(asset);
                familyPlans.Add(plan);
            }
            if (familyPlans.Count > 1)
            {
                string signature = PlanSignature(familyPlans[0]);
                if (familyPlans.Any(plan => PlanSignature(plan) != signature))
                    throw new InvalidOperationException("Construction stages have incompatible rotation mappings.");
            }
            var collection = LazySingletonSerializedSO<WgoPartBakedDataCollection>.Instance;
            foreach (RotationPlan plan in familyPlans)
                if (plan.Groups.Count != 0)
                    collection.AddOrUpdate(plan.Baked);
            foreach (string member in family)
                readyFamilies.Add(member);
            if (familyPlans.Any(plan => plan.Groups.Count != 0))
                log.LogInfo($"Enabled four orientations for [{string.Join(",", family)}].");
            return true;
        }
        catch (Exception error)
        {
            rejectedFamilies.Add(id);
            ReportSkip(id, error);
            return false;
        }
    }

    private RotationPlan LoadPlan(string asset)
    {
        if (plans.TryGetValue(asset, out RotationPlan? known))
            return known ?? throw new InvalidOperationException("Asset previously rejected: " + asset);
        AsyncOperationHandle<GameObject> handle = default;
        try
        {
            handle = Addressables.LoadAssetAsync<GameObject>("Assets/AddressableAssets/WGOs/" + asset + ".prefab");
            GameObject prefab = handle.WaitForCompletion();
            if (handle.Status != AsyncOperationStatus.Succeeded || prefab == null)
                throw new InvalidOperationException("Could not load native prefab " + asset);
            WgoPart part = prefab.GetComponent<WgoPart>();
            var baked = LazySingletonSerializedSO<WgoPartBakedDataCollection>.Instance.Get(asset);
            RotationPlan plan = RotationPlan.Create(part, baked);
            plan.Baked.id = asset;
            plans.Add(asset, plan);
            if (debug.Value)
                log.LogInfo($"Asset {asset}: native {plan.NativeDescription}; generated groups={plan.Groups.Count}.");
            return plan;
        }
        catch
        {
            plans[asset] = null;
            throw;
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    private static string PlanSignature(RotationPlan plan)
    {
        return plan.OrientationSignature;
    }

    private static void ValidateInstance(WgoData data)
    {
        if ((data.Scale - Vector3.one).sqrMagnitude > 0.000001f)
            throw new InvalidOperationException("Instance has a non-unit world scale.");
    }

    private static void AddEdge(Dictionary<string, HashSet<string>> edges, string source, string target)
    {
        if (!edges.TryGetValue(source, out HashSet<string>? values))
            edges[source] = values = new HashSet<string>(StringComparer.Ordinal);
        values.Add(target);
    }

    private void ReportSkip(string id, Exception error)
    {
        skipReasons[id] = error.Message;
        if (debug.Value && reported.Add(id + ":" + error.Message))
            log.LogInfo($"Skipped {id}: {error.Message}");
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        return AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    }

    private sealed class PartState
    {
        private readonly WgoPart part;
        private readonly RotationPlan plan;
        private readonly Dictionary<WgoPartState, int> turns = new();
        private readonly Dictionary<WgoPartState, List<DockPoint>> livePoints = new();
        private readonly List<(DockPoint Point, Direction Direction, bool ApplyRotation)> docks;
        private readonly List<(GDPoint Point, Direction Direction)> gdPoints;
        private readonly ConveyorRotation conveyor;
        private readonly Quaternion baseRotation;
        private readonly Vector3 baseScale;
        private readonly List<int>? originalSequence;
        private GameObject? implicitRoot;

        internal PartState(WgoPart part, RotationPlan plan)
        {
            this.part = part;
            this.plan = plan;
            baseRotation = part.transform.localRotation;
            baseScale = part.transform.localScale;
            originalSequence = (List<int>?)Sequence.GetValue(part);
            docks = part.GetComponentsInChildren<DockPoint>(true).Select(point =>
                (point, point.direction, (bool)ApplyDockRotation.GetValue(point))).ToList();
            gdPoints = part.GetComponentsInChildren<GDPoint>(true).Select(point => (point, point.Direction)).ToList();
            conveyor = new ConveyorRotation(part);
        }

        internal void Install()
        {
            var additions = new List<WgoPartState>();
            if (plan.Groups.Any(group => group.SourceIndex == -1))
            {
                if (part.Variations.Count != 0)
                    throw new InvalidOperationException("Implicit part already has states from another mod.");
                Transform[] children = part.transform.Cast<Transform>().ToArray();
                implicitRoot = new GameObject("FreeRotationGeometry");
                implicitRoot.transform.SetParent(part.transform, false);
                foreach (Transform child in children)
                    child.SetParent(implicitRoot.transform, false);
            }
            foreach (RotationPlan.Group group in plan.Groups)
            {
                WgoPartState source = group.SourceIndex == -1
                    ? new WgoPartState { variationId = string.Empty, rotationIndex = -1, gameObject = implicitRoot }
                    : part.Variations.Single(state => state.variationId == group.NativeId && state.rotationIndex == group.SourceIndex);
                List<DockPoint> points = RotationPlan.MatchLivePoints(part, source, RotationPlan.GetPoints(plan.Baked, source))
                    .Select(match => match.Point).ToList();
                foreach (KeyValuePair<int, int> addition in group.QuarterTurns)
                {
                    if (part.Variations.Any(state => state.variationId == group.Id && state.rotationIndex == addition.Key))
                        throw new InvalidOperationException("Another mod already supplied this state.");
                    var state = new WgoPartState
                    {
                        variationId = group.Id,
                        rotationIndex = addition.Key,
                        gameObject = source.gameObject,
                        mirror = false,
                        isDefault = group.SourceIndex == -1 && addition.Key == 0,
                        customBubblePoint = source.customBubblePoint,
                        customEvents = new List<UnityEngine.Events.UnityEvent>()
                    };
                    additions.Add(state);
                    turns.Add(state, addition.Value);
                    livePoints.Add(state, points);
                }
            }
            part.Variations.AddRange(additions);
        }

        internal bool CanRotate(string variationId)
        {
            string? current = string.IsNullOrEmpty(variationId) ? part.WgoPartData?.variationId : variationId;
            return plan.Groups.Any(group => group.NativeId == (current ?? string.Empty) || group.Id == current);
        }

        internal bool TryGetTurns(out int quarterTurns)
        {
            WgoPartState? state = turns.Keys.FirstOrDefault(value => value.variationId == part.WgoPartData?.variationId &&
                value.rotationIndex == part.WgoPartData.rotationIndex);
            quarterTurns = state == null ? 0 : turns[state];
            return state != null;
        }

        internal bool TryGetDockData(DockPoint point, out DockPointData? data)
        {
            WgoPartState? state = livePoints.Keys.FirstOrDefault(value => value.variationId == part.WgoPartData?.variationId &&
                value.rotationIndex == part.WgoPartData.rotationIndex);
            data = null;
            if (state == null)
                return false;
            int index = livePoints[state].IndexOf(point);
            if (index >= 0)
                data = part.WgoPartData.GetDockPointByIndex(index);
            return true;
        }

        internal bool TryApplyAreaRotation(int nativeIndex, int quarterTurns)
        {
            RotationPlan.Group? group = plan.Groups.FirstOrDefault(value => value.SourceIndex == nativeIndex);
            if (group == null)
                return false;
            return part.WgoPartData.TryApplyState(part.Wgo.Data.UniqueId, group.Id, quarterTurns);
        }

        internal bool EnterRotationGroup(bool inReverse)
        {
            if (part.WgoPartData == null)
                return false;
            RotationPlan.Group? group = plan.Groups.Find(value => value.NativeId == (part.WgoPartData.variationId ?? string.Empty));
            if (group == null)
                return false;
            part.ApplyWgoPartState(group.Id, inReverse ? 3 : 1);
            return true;
        }

        internal void Uninstall()
        {
            Reset();
            part.Variations.RemoveAll(state => turns.ContainsKey(state));
            Sequence.SetValue(part, originalSequence);
            if (implicitRoot != null)
            {
                foreach (Transform child in implicitRoot.transform.Cast<Transform>().ToArray())
                    child.SetParent(part.transform, false);
                UnityEngine.Object.Destroy(implicitRoot);
                implicitRoot = null;
            }
        }

        internal void Reset()
        {
            if (part == null)
                return;
            part.transform.localRotation = baseRotation;
            part.transform.localScale = baseScale;
            Sequence.SetValue(part, originalSequence);
            conveyor.Apply(0);
            foreach (var dock in docks)
                if (dock.Point != null)
                {
                    dock.Point.direction = dock.Direction;
                    ApplyDockRotation.SetValue(dock.Point, dock.ApplyRotation);
                }
            foreach (var point in gdPoints)
                if (point.Point != null)
                    GdDirection.SetValue(point.Point, point.Direction);
        }

        internal void Apply(WgoPartState state)
        {
            Reset();
            if (!turns.TryGetValue(state, out int quarterTurns))
                return;
            Sequence.SetValue(part, null);
            part.transform.localScale = Vector3.Scale(baseScale, GridRotation.GetLocalScale(quarterTurns));
            part.transform.localRotation = Quaternion.Euler(0f, quarterTurns * 90f, 0f) * baseRotation;
            conveyor.Apply(quarterTurns);
            foreach (var point in gdPoints)
                if (point.Point != null)
                    GdDirection.SetValue(point.Point, BakedRotation.RotateDirection(point.Direction, quarterTurns));
        }

        internal void ApplyDockDirection(WgoPartState state)
        {
            if (!turns.ContainsKey(state))
                return;
            Object3D rootObject = part.GetComponent<Object3D>();
            if (rootObject != null)
                PartObject.SetValue(part, rootObject);
            List<DockPoint> points = livePoints[state];
            int hash = WgoPartData.GetStateHash(state.variationId, state.rotationIndex);
            for (int index = 0; index < points.Count; index++)
            {
                DockPoint point = points[index];
                Direction direction = plan.Baked.VariationDockPointsDict[hash][index].Direction;
                if (point.transform.lossyScale.x < 0f && (direction == Direction.Left || direction == Direction.Right))
                    direction = direction.OppositeDir();
                point.direction = direction;
                ApplyDockRotation.SetValue(point, false);
            }
        }
    }
}
