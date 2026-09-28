using HarmonyLib;
using System;
using UnityEngine;

namespace GK2.FreeBuildingRotation.Patches;

[HarmonyPatch(typeof(WgoPart), nameof(WgoPart.CanBeRotated))]
internal static class CanRotatePatch
{
    [HarmonyPostfix]
    private static void Postfix(WgoPart __instance, string variationId, ref bool __result)
    {
        if (!__result && Plugin.Service?.CanRotate(__instance, variationId) == true)
            __result = true;
    }
}

[HarmonyPatch(typeof(WgoPart), nameof(WgoPart.Rotate))]
internal static class EnterRotationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(WgoPart __instance, bool inReverse, ref bool __result)
    {
        if (Plugin.Service?.EnterRotationGroup(__instance, inReverse) != true)
            return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(WgoData), nameof(WgoData.ReCreateMainWgoPartData))]
internal static class PreserveConstructionRotationPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoData __instance, out (string? Variation, int Index) __state)
    {
        __state = (__instance.MainWgoPartData?.variationId, __instance.MainWgoPartData?.rotationIndex ?? -1);
    }

    [HarmonyPostfix]
    private static void Postfix(WgoData __instance, (string? Variation, int Index) __state)
    {
        Plugin.Service?.RestoreOrientation(__instance, __state.Variation, __state.Index);
    }
}

[HarmonyPatch(typeof(WorldData), nameof(WorldData.ReplaceWgoData))]
internal static class ReplaceBuildingPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoData wgoData, string newWgoId, out bool __state)
    {
        __state = Plugin.Service?.BeginReplacement(wgoData, newWgoId) == true;
    }

    [HarmonyFinalizer]
    private static void Finalizer(bool __state)
    {
        Plugin.Service?.EndReplacement(__state);
    }
}

[HarmonyPatch(typeof(WorldData), nameof(WorldData.AddWgoData), new[] { typeof(WgoData) })]
internal static class ReplacementOrientationPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoData data)
    {
        Plugin.Service?.PrepareReplacement(data);
    }
}

[HarmonyPatch(typeof(WgoData), nameof(WgoData.AddWgoPart))]
internal static class AdditionalPartOrientationPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoData __instance, string wgoPartId, ref string stateId, ref int rotationIndex)
    {
        Plugin.Service?.PrepareAddedPart(__instance, wgoPartId, ref stateId, ref rotationIndex);
    }
}

[HarmonyPatch(typeof(WgoPart), nameof(WgoPart.GetDockPointData))]
internal static class GeneratedLiveDockPatch
{
    [HarmonyPrefix]
    private static bool Prefix(WgoPart __instance, DockPoint dockPoint, ref DockPointData? __result)
    {
        if (Plugin.Service == null || !Plugin.Service.TryGetDockData(__instance, dockPoint, out DockPointData? data))
            return true;
        __result = data;
        return false;
    }
}

[HarmonyPatch(typeof(WgoBuildPointer), "TrySetCustomRotation")]
internal static class AttachedBuildingOrientationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(WgoBuildPointer __instance, Wgo builtWgo)
    {
        return Plugin.Service?.ApplyAreaRotation(__instance, builtWgo) != true;
    }
}

[HarmonyPatch(typeof(ConveyorCondition), "HasConnectedWgoToDirection")]
internal static class ConveyorDirectionConditionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ConveyorCondition __instance, ConditionalDrawerContext context, ref bool __result)
    {
        if (context?.ConveyorComponent == null || Plugin.Service == null ||
            !Plugin.Service.TryRotateDirection(context.WgoPart, (Direction)__instance.direction, out Direction direction))
            return true;
        ConveyorComponent component = context.ConveyorComponent;
        __result = component.occupiedConnectorsDirections.Contains(direction) &&
            (!__instance.requiresInOutFlag || (__instance.isIn ? component.HasParentsInDirection(direction) : component.HasChildsInDirection(direction)));
        return false;
    }
}

[HarmonyPatch(typeof(WgoData), nameof(WgoData.GetDockPointDataWorldPosition))]
internal static class GeneratedDockPositionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(WgoData __instance, DockPointData dockPointData, ref Vector3 __result)
    {
        if (__instance.MainWgoPartData?.variationId?.StartsWith(RotationPlan.VariantPrefix, StringComparison.Ordinal) != true)
            return true;
        __result = __instance.Position + (dockPointData?.BakedData?.Position ?? Vector3.zero);
        return false;
    }
}

[HarmonyPatch(typeof(WgoData), nameof(WgoData.PrepareForGame))]
internal static class PrepareDataPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoData __instance)
    {
        Plugin.Service?.PrepareData(__instance);
    }
}

[HarmonyPatch(typeof(WgoPart), nameof(WgoPart.InitVisuals))]
internal static class InitVisualsPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoPart __instance, WgoPartData wgoPartData, WGODef definition)
    {
        Plugin.Service?.PrepareVisuals(__instance, wgoPartData, definition);
    }
}

[HarmonyPatch(typeof(WgoPart), "ApplyWgoPartState", new[] { typeof(WgoPartState) })]
internal static class ApplyStatePatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoPart __instance, WgoPartState wgoPartState)
    {
        Plugin.Service?.ApplyState(__instance, wgoPartState);
    }

    [HarmonyPostfix]
    private static void Postfix(WgoPart __instance, WgoPartState wgoPartState)
    {
        Plugin.Service?.ApplyDockDirection(__instance, wgoPartState);
    }
}

[HarmonyPatch(typeof(WgoPart), nameof(WgoPart.DeInitForPool))]
internal static class ReturnToPoolPatch
{
    [HarmonyPrefix]
    private static void Prefix(WgoPart __instance)
    {
        Plugin.Service?.ResetVisuals(__instance);
    }
}

[HarmonyPatch(typeof(WgoBuildPointer), nameof(WgoBuildPointer.SetupSelectionCells))]
internal static class PlacementDiagnosticsPatch
{
    [HarmonyPostfix]
    private static void Postfix(WgoBuildPointer __instance)
    {
        Plugin.Service?.LogPlacement(__instance);
    }
}
