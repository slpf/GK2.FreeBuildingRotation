using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GK2.FreeBuildingRotation;

internal sealed class RotationPlan
{
    internal const string VariantPrefix = "__gk2_free_rotation_";
    private const float AccessPositionTolerance = 0.1f;
    private static readonly FieldInfo ApplyDockRotation = AccessTools.Field(typeof(DockPoint), "applyRotationToDirection")
        ?? throw new MissingFieldException(typeof(DockPoint).FullName, "applyRotationToDirection");

    internal sealed class Group
    {
        internal readonly string NativeId;
        internal readonly string Id;
        internal readonly int SourceIndex;
        internal readonly Dictionary<int, int> QuarterTurns = new() { [0] = 0, [1] = 1, [2] = 2, [3] = 3 };

        internal Group(string nativeId, int ordinal, int sourceIndex)
        {
            NativeId = nativeId;
            Id = VariantPrefix + ordinal;
            SourceIndex = sourceIndex;
        }
    }

    internal readonly List<Group> Groups;
    internal readonly WgoPartBakedData Baked;
    internal readonly string NativeDescription;
    internal readonly string OrientationSignature;

    private RotationPlan(List<Group> groups, WgoPartBakedData baked, string description, string signature)
    {
        Groups = groups;
        Baked = baked;
        NativeDescription = description;
        OrientationSignature = signature;
    }

    internal static RotationPlan Create(WgoPart prefab, WgoPartBakedData? original)
    {
        if (prefab == null || prefab.Variations == null)
            throw new InvalidOperationException("Missing native prefab.");
        if (prefab.Variations.Any(state => state == null))
            throw new InvalidOperationException("Null native orientation state.");
        bool implicitState = prefab.Variations.Count == 0;
        string description = implicitState ? "implicit index=-1" : string.Join("; ", prefab.Variations
            .GroupBy(state => state.variationId).Select(group => group.Key + "=[" +
                string.Join(",", group.Select(state => state.rotationIndex + (state.mirror ? "m" : ""))) + "]"));
        RequireIdentity(prefab.transform);
        if (prefab.GetComponentsInChildren<WgoPart>(true).Length != 1 || prefab.GetComponentsInChildren<Wgo>(true).Length != 0)
            throw new InvalidOperationException("Nested WGO parts require a separate rotation adapter.");
        ConveyorRotation.Validate(prefab);
        foreach (Animator animator in prefab.GetComponentsInChildren<Animator>(true))
        {
            if (animator.transform == prefab.transform || animator.applyRootMotion ||
                animator.GetComponentsInChildren<DockPoint>(true).Length != 0 ||
                animator.GetComponentsInChildren<BuildArea>(true).Length != 0 ||
                animator.GetComponentsInChildren<Collider>(true).Any(collider => collider.gameObject.layer == 19 || collider.gameObject.layer == 28))
                throw new InvalidOperationException("Animation can affect the part root, placement area or access points.");
        }

        List<WgoPartState> states = implicitState
            ? new List<WgoPartState> { new() { variationId = string.Empty, rotationIndex = -1,
                gameObject = prefab.gameObject, isDefault = true, customEvents = new List<UnityEngine.Events.UnityEvent>() } }
            : prefab.Variations;
        foreach (WgoPartState state in states)
        {
            if (state.variationId == null || state.variationId.StartsWith(VariantPrefix, StringComparison.Ordinal) ||
                (!implicitState && state.rotationIndex < 0) || state.rotationIndex > 3 || state.gameObject == null ||
                (!implicitState && (state.gameObject == prefab.gameObject || !state.gameObject.transform.IsChildOf(prefab.transform))))
                throw new InvalidOperationException("Unsupported native state structure.");
            if (state.customEvents == null || state.customEvents.Any(value => value != null && value.GetPersistentEventCount() > 0))
                throw new InvalidOperationException("Orientation contains custom events.");
        }

        var groups = new List<Group>();
        var signatures = new List<string>();
        var baked = NativeGeometry.Prepare(prefab, original, states);
        int ordinal = 0;
        foreach (var grouping in states.GroupBy(state => state.variationId).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            List<WgoPartState> native = grouping.OrderBy(state => state.rotationIndex).ToList();
            if (native.Select(state => state.rotationIndex).Distinct().Count() != native.Count)
                throw new InvalidOperationException("Duplicate native state index.");
            WgoPartState? source = native.FirstOrDefault(state => !state.mirror && state.isDefault) ?? native.FirstOrDefault(state => !state.mirror);
            if (source == null)
                throw new InvalidOperationException("No non-mirrored source state.");
            List<DockPointData.Baked> points = GetPoints(baked, source);
            var matches = MatchLivePoints(prefab, source, points);
            var generatedPoints = points.Select((point, index) => BakedRotation.WithPosition(point, matches[index].Position)).ToList();
            var group = new Group(grouping.Key, ordinal++, source.rotationIndex);
            BakedRotation.Prepare(baked, group.NativeId, group.SourceIndex, group.QuarterTurns, group.Id, generatedPoints)();
            groups.Add(group);
            signatures.Add(group.Id);
        }
        return new RotationPlan(groups, baked, description, string.Join(";", signatures));
    }

    internal static List<DockPointData.Baked> GetPoints(WgoPartBakedData baked, WgoPartState state)
    {
        int hash = WgoPartData.GetStateHash(state.variationId, state.rotationIndex);
        var records = baked.PointsList?.Where(record => record != null && record.hash == hash).ToList();
        if (records == null || records.Count != 1 || records[0].dockPoints == null)
            throw new InvalidOperationException("Missing or ambiguous baked access data.");
        List<DockPointData.Baked> points = records[0].dockPoints;
        if (points.Any(point => point == null))
            throw new InvalidOperationException("Null access point.");
        return points;
    }

    internal static List<(DockPoint Point, Vector3 Position)> MatchLivePoints(WgoPart part, WgoPartState state, IReadOnlyList<DockPointData.Baked> bakedPoints)
    {
        DockPoint[] active = part.GetComponentsInChildren<DockPoint>(true)
            .Where(point => IsActiveForState(point.transform, part, state)).ToArray();
        if (active.Length != bakedPoints.Count)
            throw new InvalidOperationException("Live and baked access point structures differ.");
        if (active.Any(point => part.DockPoints == null || !part.DockPoints.Contains(point)))
            throw new InvalidOperationException("Unregistered live access point.");
        var registered = part.DockPoints.ToList();
        var available = active.OrderBy(point => registered.IndexOf(point))
            .Select(point => (Point: point, Geometry: GetPointGeometry(part, state, point))).ToList();
        var matched = new List<(DockPoint Point, Vector3 Position)>();
        foreach (DockPointData.Baked baked in bakedPoints)
        {
            var candidates = available.Where(value => value.Geometry.Direction == baked.Direction &&
                value.Point.IsForZombie == baked.IsForZombie && value.Point.DockPointTag == baked.DockPointTag &&
                value.Point.HideInFighting == baked.HideInFighting &&
                value.Point.DisableTargetingForCaretaker == baked.DisableTargetingForCaretaker &&
                value.Point.DontUseForWorkerPlacement == baked.DontUseForWorkerPlacement).ToList();
            if (candidates.Count > 1)
                candidates = candidates.Where(value => (value.Geometry.Position - baked.Position).sqrMagnitude <=
                    AccessPositionTolerance * AccessPositionTolerance).ToList();
            if (candidates.Count > 1 && candidates.All(value =>
                    (value.Geometry.Position - candidates[0].Geometry.Position).sqrMagnitude < 0.000001f))
                candidates = candidates.Take(1).ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException($"Cannot uniquely match access point at {baked.Position}, facing {baked.Direction} to the prefab.");
            var selected = candidates[0];
            matched.Add((selected.Point, selected.Geometry.Position));
            available.Remove(selected);
        }
        return matched;
    }

    internal static Matrix4x4 GetLocalMatrix(WgoPart part, WgoPartState state, Transform child)
    {
        Matrix4x4 matrix = Matrix4x4.identity;
        for (Transform current = child; current != part.transform; current = current.parent)
        {
            Vector3 scale = current.localScale;
            if (current == state.gameObject.transform)
                scale.x = Mathf.Abs(scale.x);
            matrix = Matrix4x4.TRS(current.localPosition, current.localRotation, scale) * matrix;
        }
        return matrix;
    }

    internal static (Vector3 Position, Direction Direction) GetPointGeometry(WgoPart part, WgoPartState state, DockPoint point)
    {
        Matrix4x4 matrix = GetLocalMatrix(part, state, point.transform);
        Direction direction = point.direction;
        if (matrix.lossyScale.x < 0f && (direction == Direction.Left || direction == Direction.Right))
            direction = direction.OppositeDir();
        if ((bool)ApplyDockRotation.GetValue(point))
        {
            Vector3 vector = matrix.rotation * direction.ConvertToVector3();
            direction = Mathf.Abs(vector.x) > Mathf.Abs(vector.z)
                ? (vector.x > 0 ? Direction.Right : Direction.Left)
                : (vector.z > 0 ? Direction.Up : Direction.Down);
        }
        return (matrix.MultiplyPoint3x4(Vector3.zero), direction);
    }

    internal static bool IsActiveForState(Transform child, WgoPart part, WgoPartState state)
    {
        for (Transform current = child; current != part.transform; current = current.parent)
        {
            if (current == null)
                return false;
            if (current == state.gameObject.transform)
                continue;
            if (part.Variations.Any(candidate => candidate.gameObject != null && candidate.gameObject.transform == current))
                return false;
            if (!current.gameObject.activeSelf)
                return false;
        }
        return true;
    }

    internal static void RequireIdentity(Transform transform)
    {
        if (transform.localPosition.sqrMagnitude > 0.000001f ||
            Quaternion.Angle(transform.localRotation, Quaternion.identity) > 0.001f ||
            (transform.localScale - Vector3.one).sqrMagnitude > 0.000001f)
            throw new InvalidOperationException("Non-identity part transform.");
    }
}
