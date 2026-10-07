using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using Game.ElementField;

namespace Game.Rendering.Tests
{
    public sealed class DormantLiquidRenderPolicyTests
    {
        private sealed class FlatTerrain : IElementWorldReadOnly
        {
            public bool HideGround;
            public bool IsInitialized => true;
            public Vector3 Origin => Vector3.zero;
            public float CellSize => .25f;
            public int ChunkSize => 8;
            public int MaximumResidentChunkCount => 16;
            public int CopyVisibleChunkKeys(ElementChunkKey[] keys) => 0;
            public uint GetChunkVersion(ElementChunkKey key) => 1;
            public bool IsSolid(Vector3Int cell) => !HideGround && cell.y < 0;
            public bool TryGetCell(Vector3Int cell, out ElementCell value) { value = default; return false; }
        }

        [Test]
        public void ArchivedSurface_UsesStaticGroundEvenAfterGameplayPagesDisappear()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                floor.transform.position = new Vector3(1, -.125f, 1);
                floor.transform.localScale = new Vector3(6, .25f, 6);
                var baker = floor.AddComponent<ElementFieldSolidBaker>();
                var serialized = new UnityEditor.SerializedObject(baker);
                serialized.FindProperty("_solidLayerMask").intValue = 1 << floor.layer;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Physics.SyncTransforms();
                var terrain = new FlatTerrain { HideGround = true };
                var snapshot = new DormantLiquidSurfaceSnapshot(terrain, 128, baker);
                Assert.That(terrain.IsSolid(new Vector3Int(7, -1, 2)), Is.False);
                Assert.That(snapshot.IsSolid(new Vector3Int(7, -1, 2)), Is.True);
                Assert.That(snapshot.IsSolid(new Vector3Int(8, -1, 2)), Is.True);
                Assert.That(snapshot.IsSolid(new Vector3Int(8, 0, 2)), Is.False);
                // 相邻 Chunk 的重叠 Halo 必须复算出完全相同的位置和法线。
                for (int x = 5; x <= 10; x++)
                for (int z = 1; z <= 4; z++) snapshot.Add(new Vector3Int(x, 0, z), 96);
                var settings = new WaterVolumeMeshingSettings(2, .25f, .08f, .3f, .48f, .2f, 1f / 255f);
                var workspace = new WaterVolumeMeshingWorkspace(8, 2);
                var vertices = new List<Vector3>(); var normals = new List<Vector3>();
                var uvs = new List<Vector2>(); var indices = new List<int>();
                WorldWaterVolumeMeshBuilder.BuildWorldChunk(snapshot, new ElementChunkKey(0, 0, 0),
                    in settings, workspace, vertices, normals, uvs, indices);
                Vector3[] left = vertices.ToArray(); Vector3[] leftNormals = normals.ToArray();
                WorldWaterVolumeMeshBuilder.BuildWorldChunk(snapshot, new ElementChunkKey(1, 0, 0),
                    in settings, workspace, vertices, normals, uvs, indices);
                int matches = 0;
                for (int i = 0; i < left.Length; i++)
                for (int j = 0; j < vertices.Count; j++)
                    if ((left[i] - vertices[j] - new Vector3(2, 0, 0)).sqrMagnitude < 1e-10f)
                    {
                        matches++;
                        Assert.That(Vector3.Dot(leftNormals[i], normals[j]), Is.GreaterThan(.9999f));
                    }
                Assert.That(matches, Is.GreaterThan(0));
            }
            finally { Object.DestroyImmediate(floor); }
        }

        [Test]
        public void DormantSurface_HasSupportedLiquidShaderPass()
        {
            var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader");
            var source = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Art/Elemental/Materials/M_ElementWater_PBF.mat");
            var material = DormantLiquidCellRenderer.CreateInstancedMaterial(shader, source);
            try
            {
                Assert.That(shader.isSupported, Is.True);
                Assert.That(material.FindPass("WaterForward"), Is.GreaterThanOrEqualTo(0));
                Assert.That(UnityEditor.ShaderUtil.ShaderHasError(shader), Is.False);
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void Snapshot_ReorderingIsStableButNeighborRemovalInvalidatesSeam()
        {
            var snapshot = new DormantLiquidSurfaceSnapshot(new FlatTerrain(), 8);
            var key = new ElementChunkKey(-1, 0, 0);
            snapshot.Add(new Vector3Int(-1, 0, 1), 128);
            snapshot.Add(new Vector3Int(0, 0, 1), 64);
            ulong original = snapshot.NeighborhoodHash(key);
            snapshot.Clear();
            snapshot.Add(new Vector3Int(0, 0, 1), 64);
            snapshot.Add(new Vector3Int(-1, 0, 1), 128);
            Assert.That(snapshot.NeighborhoodHash(key), Is.EqualTo(original));
            snapshot.Clear();
            snapshot.Add(new Vector3Int(-1, 0, 1), 128);
            Assert.That(snapshot.NeighborhoodHash(key), Is.Not.EqualTo(original));
            Assert.That(snapshot.TryGetCell(new Vector3Int(0, 0, 1), out _), Is.False);
        }

        [TestCase(8)]
        [TestCase(255)]
        public void AdjacentArchivedCells_ProduceOneConnectedSurface(int amount)
        {
            var snapshot = new DormantLiquidSurfaceSnapshot(new FlatTerrain(), 8);
            snapshot.Add(new Vector3Int(2, 0, 2), (byte)amount);
            snapshot.Add(new Vector3Int(3, 0, 2), (byte)amount);
            var settings = new WaterVolumeMeshingSettings(2, .25f, .08f, .3f, .48f, .2f, 1f / 255f);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            WorldWaterVolumeMeshBuilder.BuildWorldChunk(snapshot, new ElementChunkKey(0, 0, 0),
                in settings, new WaterVolumeMeshingWorkspace(8, 2), vertices, normals, uvs, indices);
            Assert.That(indices.Count, Is.GreaterThan(0), "低存量休眠液体也必须保留可见表面");
            // 从第一个三角形遍历共享顶点：逐 Cell Cube 会得到两个独立连通分量。
            var reached = new HashSet<int> { indices[0] };
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < indices.Count; i += 3)
                    if (reached.Contains(indices[i]) || reached.Contains(indices[i + 1]) || reached.Contains(indices[i + 2]))
                        for (int j = 0; j < 3; j++) changed |= reached.Add(indices[i + j]);
            } while (changed);
            foreach (int index in indices) Assert.That(reached.Contains(index), Is.True);
        }

        [Test]
        public void RuntimeMaterial_EnablesInstancingAfterCopyingDisabledSource()
        {
            var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader");
            Assert.That(shader, Is.Not.Null);
            var source = new Material(shader) { enableInstancing = false };
            Material runtime = null;
            try
            {
                source.SetColor("_ShallowColor", Color.cyan);
                runtime = DormantLiquidCellRenderer.CreateInstancedMaterial(shader, source);
                Assert.That(runtime.enableInstancing, Is.True);
                Assert.That(source.enableInstancing, Is.False);
                Assert.That(runtime.GetColor("_ShallowColor"), Is.EqualTo(Color.cyan));
            }
            finally
            {
                if (runtime != null) Object.DestroyImmediate(runtime);
                Object.DestroyImmediate(source);
            }
        }

        [TestCase(0, 1f, .08f, 0f)]
        [TestCase(1, 1f, .08f, .08f)]
        [TestCase(128, 1f, .08f, 128f / 255f)]
        [TestCase(255, .5f, .08f, .5f)]
        public void Height_PreservesEmptyAndClampsNonEmptyToVisibleThickness(
            byte amount, float cellSize, float minimumRatio, float expected)
        {
            Assert.That(DormantLiquidRenderPolicy.CalculateHeight(
                amount, cellSize, minimumRatio), Is.EqualTo(expected).Within(.0001f));
        }

        [Test]
        public void Matrix_PlacesFillOnCellBottomInsteadOfFloatingAtCenter()
        {
            Matrix4x4 matrix = DormantLiquidRenderPolicy.CreateCellMatrix(
                new Vector3Int(2, 3, 4), Vector3.one, .5f, 128, .08f);
            float height = .5f * 128f / 255f;
            Assert.That(matrix.GetColumn(3).y, Is.EqualTo(1f + 3f * .5f + height * .5f).Within(.0001f));
            Assert.That(matrix.lossyScale.y, Is.EqualTo(height).Within(.0001f));
        }
    }
}
