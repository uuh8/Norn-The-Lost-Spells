using System;
using UnityEngine;

namespace Game.Rendering
{
    public readonly struct FireParcelSurfaceGridSettings
    {
        public readonly Vector3Int Resolution;
        public readonly Vector3 Origin;
        public readonly Vector3 VoxelSize;
        public readonly Bounds Bounds;
        public FireParcelSurfaceGridSettings(Vector3Int resolution, Vector3 origin, Vector3 voxelSize, Bounds bounds)
        {
            Resolution = resolution;
            Origin = origin;
            VoxelSize = voxelSize;
            Bounds = bounds;
        }
    }

    public static class FireParcelSurfaceGridPlanner
    {
        public static FireParcelSurfaceGridSettings Plan(Vector3 center, float radius, float targetVoxelSize)
        {
            if (!IsFinite(center) || !IsPositive(radius) || !IsPositive(targetVoxelSize))
                throw new ArgumentOutOfRangeException();
            float size = radius * 2f;
            int resolution = Mathf.Clamp(Mathf.CeilToInt(size / targetVoxelSize) + 1, 2, 96);
            float voxel = size / (resolution - 1);
            // 与液体相同，Origin 量化到完整 Voxel，避免相机/角色移动改变 Density Lattice phase。
            Vector3 origin = new Vector3(
                Mathf.Floor((center.x - radius) / voxel) * voxel,
                Mathf.Floor((center.y - radius) / voxel) * voxel,
                Mathf.Floor((center.z - radius) / voxel) * voxel);
            var bounds = new Bounds(origin + Vector3.one * radius, Vector3.one * size);
            return new FireParcelSurfaceGridSettings(
                new Vector3Int(resolution, resolution, resolution), origin, Vector3.one * voxel, bounds);
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        private static bool IsPositive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
