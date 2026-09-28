using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GK2.FreeBuildingRotation;

internal static class NativeGeometry
{
    private static readonly FieldInfo BoundsField = AccessTools.Field(typeof(WgoPartBakedData), "boundsData");
    private static readonly FieldInfo PointsField = AccessTools.Field(typeof(WgoPartBakedData), "pointsList");
    private static readonly FieldInfo PositionField = AccessTools.Field(typeof(DockPointData.Baked), "position");
    private static readonly FieldInfo DirectionField = AccessTools.Field(typeof(DockPointData.Baked), "direction");
    private static readonly MethodInfo CloneMethod = AccessTools.Method(typeof(object), "MemberwiseClone");

    internal static WgoPartBakedData Prepare(WgoPart prefab, WgoPartBakedData? original, List<WgoPartState> states)
    {
        var baked = original == null || string.IsNullOrEmpty(original.id) ? new WgoPartBakedData(prefab.name)
            : (WgoPartBakedData)CloneMethod.Invoke(original, Array.Empty<object>());
        var bounds = new List<WgoPartBakedData.InternalBoundsData>(
            (List<WgoPartBakedData.InternalBoundsData>?)BoundsField.GetValue(baked) ?? new());
        var points = new List<WgoPartBakedData.WgoPartDockPointsBakedData>(baked.PointsList ?? Array.Empty<WgoPartBakedData.WgoPartDockPointsBakedData>());
        Bounds? chunk = null;
        foreach (WgoPartState state in states)
        {
            int hash = WgoPartData.GetStateHash(state.variationId, state.rotationIndex);
            if (!points.Any(record => record != null && record.hash == hash))
            {
                var docks = new List<DockPointData.Baked>();
                foreach (DockPoint point in prefab.DockPoints ?? Array.Empty<DockPoint>())
                {
                    if (point == null || !RotationPlan.IsActiveForState(point.transform, prefab, state))
                        continue;
                    var geometry = RotationPlan.GetPointGeometry(prefab, state, point);
                    var dock = new DockPointData.Baked(point);
                    Vector3 position = geometry.Position;
                    Direction direction = geometry.Direction;
                    if (state.mirror)
                    {
                        position.x = -position.x;
                        if (direction == Direction.Left || direction == Direction.Right)
                            direction = direction.OppositeDir();
                    }
                    PositionField.SetValue(dock, position);
                    DirectionField.SetValue(dock, direction);
                    docks.Add(dock);
                }
                points.Add(new WgoPartBakedData.WgoPartDockPointsBakedData { hash = hash, dockPoints = docks });
            }
            Bounds? geometryBounds = GetBounds(prefab, state, -1);
            if (geometryBounds.HasValue)
                chunk = Union(chunk, geometryBounds.Value);
            if (!bounds.Any(record => record != null && record.hash == hash))
            {
                Bounds? collision = GetBounds(prefab, state, 8) ?? GetBounds(prefab, state, 19);
                if (!collision.HasValue && !prefab.GetComponentsInChildren<Collider>(true).Any(collider =>
                        collider.enabled && (collider.gameObject.layer == 8 || collider.gameObject.layer == 19) &&
                        RotationPlan.IsActiveForState(collider.transform, prefab, state)))
                    collision = new Bounds(Vector3.zero, Vector3.zero);
                if (!collision.HasValue)
                    throw new InvalidOperationException("Missing collision data and no supported prefab box colliders.");
                Bounds box = collision.Value;
                bounds.Add(new WgoPartBakedData.InternalBoundsData
                {
                    hash = hash,
                    rect = Rect.MinMaxRect(box.min.x, box.min.z, box.max.x, box.max.z)
                });
            }
        }
        BoundsField.SetValue(baked, bounds);
        PointsField.SetValue(baked, points);
        AccessTools.Field(typeof(WgoPartBakedData), "variationCollisionBoundsRectDict").SetValue(baked, null);
        AccessTools.Field(typeof(WgoPartBakedData), "variationDockPointsDict").SetValue(baked, null);
        if (!baked.HasChunkBounds && chunk.HasValue)
            baked.SetChunkBounds(new ChunkBoundsPair(chunk.Value, chunk.Value));
        return baked;
    }

    private static Bounds Union(Bounds? existing, Bounds addition)
    {
        if (!existing.HasValue)
            return addition;
        Bounds union = existing.Value;
        union.Encapsulate(addition);
        return union;
    }

    private static Bounds? GetBounds(WgoPart prefab, WgoPartState state, int layer)
    {
        Bounds? bounds = null;
        if (layer == -1)
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                if (RotationPlan.IsActiveForState(renderer.transform, prefab, state))
                    bounds = TransformBounds(bounds, renderer.localBounds, RotationPlan.GetLocalMatrix(prefab, state, renderer.transform), state.mirror);
        foreach (BoxCollider collider in prefab.GetComponentsInChildren<BoxCollider>(true))
        {
            if (!collider.enabled || !RotationPlan.IsActiveForState(collider.transform, prefab, state) ||
                (layer != -1 && collider.gameObject.layer != layer))
                continue;
            Matrix4x4 matrix = RotationPlan.GetLocalMatrix(prefab, state, collider.transform);
            bounds = TransformBounds(bounds, new Bounds(collider.center, collider.size), matrix, state.mirror);
        }
        return bounds;
    }

    private static Bounds? TransformBounds(Bounds? bounds, Bounds local, Matrix4x4 matrix, bool mirror)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 sign = new((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
            Vector3 point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign));
            if (mirror)
                point.x = -point.x;
            bounds = Union(bounds, new Bounds(point, Vector3.zero));
        }
        return bounds;
    }
}
