using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GK2.FreeBuildingRotation
{
    internal static class BakedRotation
    {
        private static readonly FieldInfo BoundsDataField = Field(typeof(WgoPartBakedData), "boundsData");
        private static readonly FieldInfo PointsListField = Field(typeof(WgoPartBakedData), "pointsList");
        private static readonly FieldInfo PlannerMeshesField = Field(typeof(WgoPartBakedData), "plannerMeshesData");
        private static readonly FieldInfo GraphBoxesField = Field(typeof(WgoPartBakedData), "graphUpdateSceneBoxesData");
        private static readonly FieldInfo GdPointsField = Field(typeof(WgoPartBakedData), "gdPointsBakedData");
        private static readonly FieldInfo NavMeshCutsField = Field(typeof(WgoPartBakedData), "customNavMeshCutPrefabsBakedData");
        private static readonly FieldInfo CollisionCacheField = Field(typeof(WgoPartBakedData), "variationCollisionBoundsRectDict");
        private static readonly FieldInfo DockPointsCacheField = Field(typeof(WgoPartBakedData), "variationDockPointsDict");
        private static readonly FieldInfo PlannerMeshCacheField = Field(typeof(WgoPartBakedData), "variationPlannerMeshDict");
        private static readonly FieldInfo GraphBoxCacheField = Field(typeof(WgoPartBakedData), "variationGraphUpdateSceneBoxDict");
        private static readonly FieldInfo DockPositionField = Field(typeof(DockPointData.Baked), "position");
        private static readonly FieldInfo DockDirectionField = Field(typeof(DockPointData.Baked), "direction");
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MemberwiseClone is unavailable.");

        internal static Action Prepare(WgoPartBakedData baked, string variationId, int sourceIndex, IReadOnlyDictionary<int, int> targetQuarterTurns, string? targetVariationId = null, IReadOnlyList<DockPointData.Baked>? sourceDockPoints = null)
        {
            if (baked == null || variationId == null || sourceIndex < -1 || sourceIndex > 3 || targetQuarterTurns == null)
                throw new InvalidOperationException("Invalid baked rotation arguments.");

            var bounds = ReadRequired<WgoPartBakedData.InternalBoundsData>(BoundsDataField, baked);
            var points = ReadRequired<WgoPartBakedData.WgoPartDockPointsBakedData>(PointsListField, baked);
            var meshes = ReadOptional<WgoPartBakedData.PlannerMeshData>(PlannerMeshesField, baked);
            var boxes = ReadOptional<WgoPartBakedData.GraphUpdateSceneBoxData>(GraphBoxesField, baked);
            var gdPoints = ReadOptional<WgoPartBakedData.GDPointsVariationBakedData>(GdPointsField, baked);
            var navMeshCuts = ReadOptional<WgoPartBakedData.CustomNavMeshCutPrefabsVariationBakedData>(NavMeshCutsField, baked);
            ValidateRecords(bounds, entry => entry.hash, "collision bounds");
            ValidateRecords(points, entry => entry.hash, "dock points");
            ValidateRecords(meshes, entry => entry.hash, "planner meshes");
            ValidateRecords(boxes, entry => entry.hash, "graph boxes");
            ValidateRecords(gdPoints, entry => entry.hash, "GD points");
            ValidateRecords(navMeshCuts, entry => entry.hash, "custom navmesh cuts");

            int sourceHash = WgoPartData.GetStateHash(variationId, sourceIndex);
            var sourceBounds = bounds.Find(entry => entry.hash == sourceHash);
            var sourcePoints = points.Find(entry => entry.hash == sourceHash);
            if (sourceBounds == null || sourcePoints == null || sourcePoints.dockPoints == null)
                throw new InvalidOperationException("The source state has no complete collision and dock-point records.");

            ValidateRect(sourceBounds.rect);
            IReadOnlyList<DockPointData.Baked> generatedSourcePoints = sourceDockPoints ?? sourcePoints.dockPoints;
            if (generatedSourcePoints.Count != sourcePoints.dockPoints.Count)
                throw new InvalidOperationException("Generated access data must retain the original point count.");
            ValidateDockPoints(generatedSourcePoints);
            var sourceMesh = meshes.Find(entry => entry.hash == sourceHash);
            var sourceBox = boxes.Find(entry => entry.hash == sourceHash);
            if (sourceMesh != null)
                ValidateMesh(sourceMesh);
            if (sourceBox != null)
                ValidateBox(sourceBox);

            var replacementBounds = new List<WgoPartBakedData.InternalBoundsData>(bounds);
            var replacementPoints = new List<WgoPartBakedData.WgoPartDockPointsBakedData>(points);
            var replacementMeshes = new List<WgoPartBakedData.PlannerMeshData>(meshes);
            var replacementBoxes = new List<WgoPartBakedData.GraphUpdateSceneBoxData>(boxes);
            var replacementGdPoints = new List<WgoPartBakedData.GDPointsVariationBakedData>(gdPoints);
            var replacementCuts = new List<WgoPartBakedData.CustomNavMeshCutPrefabsVariationBakedData>(navMeshCuts);
            var targetHashes = new HashSet<int>();
            foreach (var target in targetQuarterTurns)
            {
                if (target.Key < 0 || target.Key > 3 || target.Value < 0 || target.Value > 3)
                    throw new InvalidOperationException("Generated states require distinct indices and zero to three quarter turns.");

                int hash = WgoPartData.GetStateHash(targetVariationId ?? variationId, target.Key);
                if (hash == sourceHash || !targetHashes.Add(hash))
                    throw new InvalidOperationException("Generated state hashes overlap.");
                if (bounds.Exists(entry => entry.hash == hash) || points.Exists(entry => entry.hash == hash) ||
                    meshes.Exists(entry => entry.hash == hash) || boxes.Exists(entry => entry.hash == hash) ||
                    gdPoints.Exists(entry => entry.hash == hash) || navMeshCuts.Exists(entry => entry.hash == hash))
                    throw new InvalidOperationException("A generated state would overwrite existing baked data.");

                replacementBounds.RemoveAll(entry => entry.hash == hash);
                replacementBounds.Add(new WgoPartBakedData.InternalBoundsData
                {
                    hash = hash,
                    rect = Rotate(sourceBounds.rect, target.Value)
                });
                replacementPoints.RemoveAll(entry => entry.hash == hash);
                replacementPoints.Add(new WgoPartBakedData.WgoPartDockPointsBakedData
                {
                    hash = hash,
                    dockPoints = Rotate(generatedSourcePoints, target.Value)
                });
                var sourceGd = gdPoints.Find(entry => entry.hash == sourceHash);
                if (sourceGd != null)
                {
                    var rotated = new List<WgoPartBakedData.GDPointBakedData>();
                    foreach (var point in sourceGd.gdPoints)
                    {
                        var clone = (WgoPartBakedData.GDPointBakedData)CloneMethod.Invoke(point, null);
                        ValidateVector(point.localPosition);
                        clone.localPosition = Rotate(point.localPosition, target.Value);
                        clone.direction = RotateDirection(point.direction, target.Value);
                        clone.nextGdPointIds = new List<string>(point.nextGdPointIds);
                        rotated.Add(clone);
                    }
                    replacementGdPoints.Add(new WgoPartBakedData.GDPointsVariationBakedData { hash = hash, gdPoints = rotated });
                }
                var sourceCuts = navMeshCuts.Find(entry => entry.hash == sourceHash);
                if (sourceCuts != null)
                {
                    var rotated = new List<WgoPartBakedData.CustomNavMeshCutPrefabEntryBakedData>();
                    foreach (var cut in sourceCuts.entries)
                    {
                        var clone = (WgoPartBakedData.CustomNavMeshCutPrefabEntryBakedData)CloneMethod.Invoke(cut, null);
                        Matrix4x4 matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, target.Value * 90f, 0f), GridRotation.GetLocalScale(target.Value)) *
                            Matrix4x4.TRS(cut.localPosition, cut.localRotation, cut.localScale);
                        Vector3 x = matrix.GetColumn(0);
                        Vector3 y = matrix.GetColumn(1);
                        Vector3 z = matrix.GetColumn(2);
                        if (Vector3.Dot(Vector3.Cross(x, y), z) <= 0f ||
                            Mathf.Abs(Vector3.Dot(x.normalized, y.normalized)) > 0.0001f ||
                            Mathf.Abs(Vector3.Dot(x.normalized, z.normalized)) > 0.0001f ||
                            Mathf.Abs(Vector3.Dot(y.normalized, z.normalized)) > 0.0001f)
                            throw new InvalidOperationException("Navigation cut requires a sheared or mirrored transform.");
                        clone.localPosition = matrix.GetColumn(3);
                        clone.localRotation = Quaternion.LookRotation(z, y);
                        clone.localScale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
                        rotated.Add(clone);
                    }
                    replacementCuts.Add(new WgoPartBakedData.CustomNavMeshCutPrefabsVariationBakedData { hash = hash, entries = rotated });
                }
                replacementMeshes.RemoveAll(entry => entry.hash == hash);
                if (sourceMesh != null)
                {
                    var vertices = new List<Vector3>(sourceMesh.vertices.Count);
                    foreach (var vertex in sourceMesh.vertices)
                        vertices.Add(Rotate(vertex, target.Value));
                    replacementMeshes.Add(new WgoPartBakedData.PlannerMeshData
                    {
                        hash = hash,
                        vertices = vertices,
                        triangles = new List<int>(sourceMesh.triangles)
                    });
                }
                replacementBoxes.RemoveAll(entry => entry.hash == hash);
                if (sourceBox != null)
                {
                    replacementBoxes.Add(new WgoPartBakedData.GraphUpdateSceneBoxData
                    {
                        hash = hash,
                        localCenter = Rotate(sourceBox.localCenter, target.Value),
                        size = RotateSize(sourceBox.size, target.Value),
                        setWalkability = sourceBox.setWalkability,
                        updatePhysics = sourceBox.updatePhysics,
                        penaltyDelta = sourceBox.penaltyDelta
                    });
                }
            }

            bool hasChunkBounds = baked.HasChunkBounds;
            ChunkBoundsPair chunkBounds = baked.ChunkBounds;
            if (hasChunkBounds)
            {
                chunkBounds = new ChunkBoundsPair(
                    UnionRotations(chunkBounds.withShadows),
                    UnionRotations(chunkBounds.withoutShadows));
            }

            return () =>
            {
                BoundsDataField.SetValue(baked, replacementBounds);
                PointsListField.SetValue(baked, replacementPoints);
                PlannerMeshesField.SetValue(baked, replacementMeshes);
                GraphBoxesField.SetValue(baked, replacementBoxes);
                GdPointsField.SetValue(baked, replacementGdPoints);
                NavMeshCutsField.SetValue(baked, replacementCuts);
                Field(typeof(WgoPartBakedData), "variationGDPointsDict").SetValue(baked, null);
                Field(typeof(WgoPartBakedData), "variationCustomNavMeshCutPrefabsDict").SetValue(baked, null);
                CollisionCacheField.SetValue(baked, null);
                DockPointsCacheField.SetValue(baked, null);
                PlannerMeshCacheField.SetValue(baked, null);
                GraphBoxCacheField.SetValue(baked, null);
                if (hasChunkBounds)
                    baked.SetChunkBounds(chunkBounds);
            };
        }

        private static FieldInfo Field(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(type.FullName + "." + name + " is unavailable.");
        }

        private static List<T> ReadRequired<T>(FieldInfo field, WgoPartBakedData baked)
        {
            return field.GetValue(baked) as List<T>
                ?? throw new InvalidOperationException("Missing baked list: " + field.Name + ".");
        }

        private static List<T> ReadOptional<T>(FieldInfo field, WgoPartBakedData baked)
        {
            object? value = field.GetValue(baked);
            if (value == null)
                return new List<T>();
            return value as List<T>
                ?? throw new InvalidOperationException("Unexpected baked list type: " + field.Name + ".");
        }

        private static void ValidateRecords<T>(List<T> records, Func<T, int> getHash, string name) where T : class
        {
            var hashes = new HashSet<int>();
            foreach (var record in records)
            {
                if (record == null || !hashes.Add(getHash(record)))
                    throw new InvalidOperationException("Null or duplicate baked " + name + " record.");
            }
        }

        private static void ValidateDockPoints(IReadOnlyList<DockPointData.Baked> points)
        {
            foreach (var point in points)
            {
                if (point == null)
                    throw new InvalidOperationException("Null dock point.");
                ValidateVector(point.Position);
                if (point.Direction != Direction.Up && point.Direction != Direction.Right &&
                    point.Direction != Direction.Down && point.Direction != Direction.Left)
                    throw new InvalidOperationException("A dock point has an unsupported direction.");
            }
        }

        private static void ValidateMesh(WgoPartBakedData.PlannerMeshData mesh)
        {
            if (mesh.vertices == null || mesh.vertices.Count == 0 || mesh.triangles == null ||
                mesh.triangles.Count < 3 || mesh.triangles.Count % 3 != 0)
                throw new InvalidOperationException("Malformed planner mesh.");
            foreach (var vertex in mesh.vertices)
                ValidateVector(vertex);
            foreach (int index in mesh.triangles)
            {
                if (index < 0 || index >= mesh.vertices.Count)
                    throw new InvalidOperationException("A planner mesh triangle index is out of range.");
            }
        }

        private static void ValidateBox(WgoPartBakedData.GraphUpdateSceneBoxData box)
        {
            ValidateVector(box.localCenter);
            ValidateSize(box.size);
            if (box.size.x == 0f && box.size.y == 0f && box.size.z == 0f)
                throw new InvalidOperationException("A graph update box has no size.");
        }

        private static void ValidateRect(Rect rect)
        {
            if (!IsFinite(rect.xMin) || !IsFinite(rect.xMax) || !IsFinite(rect.yMin) || !IsFinite(rect.yMax) ||
                rect.width < 0f || rect.height < 0f)
                throw new InvalidOperationException("Malformed collision bounds.");
        }

        private static void ValidateVector(Vector3 vector)
        {
            if (!IsFinite(vector.x) || !IsFinite(vector.y) || !IsFinite(vector.z))
                throw new InvalidOperationException("Baked geometry contains a non-finite coordinate.");
        }

        private static void ValidateSize(Vector3 size)
        {
            ValidateVector(size);
            if (size.x < 0f || size.y < 0f || size.z < 0f)
                throw new InvalidOperationException("Baked geometry contains a negative size.");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Vector3 Rotate(Vector3 vector, int quarterTurns)
        {
            return GridRotation.Rotate(vector, quarterTurns);
        }

        internal static Direction RotateDirection(Direction direction, int quarterTurns)
        {
            for (int i = 0; i < quarterTurns; i++)
                if (direction != Direction.None)
                    direction = direction.ClockwiseDir();
            return direction;
        }

        private static Vector3 RotateSize(Vector3 size, int quarterTurns)
        {
            return GridRotation.RotateSize(size, quarterTurns);
        }

        private static Rect Rotate(Rect rect, int quarterTurns)
        {
            var corners = new[]
            {
                Rotate(new Vector3(rect.xMin, 0f, rect.yMin), quarterTurns),
                Rotate(new Vector3(rect.xMin, 0f, rect.yMax), quarterTurns),
                Rotate(new Vector3(rect.xMax, 0f, rect.yMin), quarterTurns),
                Rotate(new Vector3(rect.xMax, 0f, rect.yMax), quarterTurns)
            };
            float minX = corners[0].x;
            float maxX = corners[0].x;
            float minZ = corners[0].z;
            float maxZ = corners[0].z;
            foreach (var corner in corners)
            {
                minX = Math.Min(minX, corner.x);
                maxX = Math.Max(maxX, corner.x);
                minZ = Math.Min(minZ, corner.z);
                maxZ = Math.Max(maxZ, corner.z);
            }
            return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        internal static DockPointData.Baked WithPosition(DockPointData.Baked source, Vector3 position)
        {
            ValidateVector(position);
            var clone = (DockPointData.Baked)CloneMethod.Invoke(source, null);
            DockPositionField.SetValue(clone, position);
            return clone;
        }

        private static List<DockPointData.Baked> Rotate(IReadOnlyList<DockPointData.Baked> source, int quarterTurns)
        {
            var points = new List<DockPointData.Baked>(source.Count);
            foreach (var point in source)
            {
                var clone = CloneMethod.Invoke(point, null) as DockPointData.Baked
                    ?? throw new InvalidOperationException("A dock point could not be cloned.");
                Direction direction = point.Direction;
                for (int i = 0; i < quarterTurns; i++)
                    direction = direction.ClockwiseDir();
                DockPositionField.SetValue(clone, Rotate(point.Position, quarterTurns));
                DockDirectionField.SetValue(clone, direction);
                points.Add(clone);
            }
            return points;
        }

        private static Bounds UnionRotations(Bounds source)
        {
            ValidateVector(source.center);
            ValidateSize(source.size);
            var union = source;
            for (int quarterTurns = 1; quarterTurns <= 3; quarterTurns++)
                union.Encapsulate(new Bounds(Rotate(source.center, quarterTurns), RotateSize(source.size, quarterTurns)));
            ValidateVector(union.center);
            ValidateSize(union.size);
            return union;
        }
    }
}
