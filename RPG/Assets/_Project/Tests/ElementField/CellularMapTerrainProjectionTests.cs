using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    public sealed class CellularMapTerrainProjectionTests
    {
        [Test]
        public void PartialSandCollidesAtItsVisibleHeightAndOnlyTouchingGrainsMerge()
        {
            var root = new GameObject("Grain projection");
            var geometry = new CellularSolidColliders(root.transform, .25f, 4, 0, 255);
            try
            {
                var a = Vector3Int.left;
                var b = a + Vector3Int.left;
                geometry.Set(a, MaterialId.Sand, 1);
                geometry.Set(b, MaterialId.Sand, 1);
                geometry.Commit();
                var boxes = root.GetComponentsInChildren<BoxCollider>();
                Assert.That(boxes.Length, Is.EqualTo(2));
                var size = boxes[0].size;
                Assert.That(size.x * size.y * size.z, Is.EqualTo(.25f * .25f * .25f / 255).Within(1e-8f));
                Assert.That(boxes[0].bounds.max.y, Is.EqualTo(size.y).Within(1e-5f));
                geometry.Set(a, MaterialId.Sand, 128);
                geometry.Set(b, MaterialId.Sand, 128);
                geometry.Commit();
                int enabled = 0;
                foreach (var box in root.GetComponentsInChildren<BoxCollider>())
                    if (box.enabled)
                    {
                        enabled++;
                        Assert.That(box.size.x, Is.EqualTo(.5f).Within(1e-5f));
                        Assert.That(box.bounds.max.y, Is.EqualTo(.25f * 128 / 255).Within(1e-5f));
                    }
                Assert.That(enabled, Is.EqualTo(1));
                geometry.Set(a, MaterialId.Empty, 0);
                geometry.Set(b, MaterialId.Empty, 0);
                geometry.Commit();
                Assert.That(root.GetComponentsInChildren<BoxCollider>(), Is.Empty);
            }
            finally
            {
                geometry.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FlatGroundSplitsOnlyTheHitBranchAndMergesItWhenRestored()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var root = new GameObject("Editable flat ground");
            var profile = ScriptableObject.CreateInstance<ElementCellWorldProfile>();
            CellularMapTerrain map = null;
            try
            {
                // 每边 16 个 tile，共 256 个。首个洞走 8 层二分，每层只产生一个额外 BoxCollider。
                ground.transform.position = new Vector3(0f, .5f, 0f);
                ground.transform.localScale = new Vector3(256f, 1f, 256f);
                Physics.SyncTransforms();
                var collider = ground.GetComponent<BoxCollider>();
                var renderer = ground.GetComponent<MeshRenderer>();
                var source = new CellularMapTerrain.Source { Collider = collider, Bounds = collider.bounds,
                    Material = MaterialId.Soil, Renderers = new Renderer[] {renderer}, RendererEnabled = new[] {true}, Surface = renderer.sharedMaterial };
                map = new CellularMapTerrain(new[] {source},root.transform,1f,profile);
                map.Show(true);
                var a = new Vector3Int(-124,0,-124); var b = new Vector3Int(123,0,123);
                Assert.That(map.TryEdit(a, MaterialId.Empty), Is.True);
                Assert.That(root.GetComponentsInChildren<BoxCollider>(true).Length, Is.EqualTo(9));
                Assert.That(FlatSurfaceAt(root, a), Is.Null);
                Assert.That(FlatSurfaceAt(root, b), Is.Not.Null);
                Assert.That(map.TryEdit(b, MaterialId.Empty), Is.True);
                Assert.That(root.GetComponentsInChildren<BoxCollider>(true).Length, Is.EqualTo(16));
                Assert.That(FlatSurfaceAt(root, b), Is.Null);
                var farNeighbor = b + Vector3Int.right;
                var farPatch = FlatSurfaceAt(root, farNeighbor);
                Assert.That(farPatch, Is.TypeOf<MeshCollider>());
                Assert.That(map.TryEdit(a, MaterialId.Soil), Is.True);
                Assert.That(root.GetComponentsInChildren<BoxCollider>(true).Length, Is.EqualTo(9));
                Assert.That(FlatSurfaceAt(root, a), Is.TypeOf<BoxCollider>());
                Assert.That(FlatSurfaceAt(root, farNeighbor), Is.SameAs(farPatch));
                Assert.That(map.TryEdit(b, MaterialId.Soil), Is.True);
                Assert.That(collider.enabled, Is.True);
                Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty);
            }
            finally
            {
                map?.Dispose();
                Object.DestroyImmediate(root); Object.DestroyImmediate(ground); Object.DestroyImmediate(profile);
            }
        }

        private static Collider FlatSurfaceAt(GameObject root, Vector3Int cell)
        {
            var ray = new Ray(new Vector3(cell.x + .5f, 2f, cell.z + .5f), Vector3.down);
            foreach (var collider in root.GetComponentsInChildren<Collider>())
                if (collider.Raycast(ray, out _, 3f)) return collider;
            return null;
        }

        [Test]
        public void DiggingDistantRockTileKeepsFirstColliderAndRestoringLastHoleRestoresAuthoredMesh()
        {
            var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var root = new GameObject("Editable rock projections");
            var profile = ScriptableObject.CreateInstance<ElementCellWorldProfile>();
            CellularMapTerrain map = null;
            try
            {
                Object.DestroyImmediate(rock.GetComponent<BoxCollider>());
                rock.transform.position = new Vector3(24f,2f,.5f);
                rock.transform.localScale = new Vector3(48f,4f,1f);
                var collider = rock.AddComponent<MeshCollider>();
                collider.sharedMesh = rock.GetComponent<MeshFilter>().sharedMesh;
                Physics.SyncTransforms();
                var renderer = rock.GetComponent<MeshRenderer>();
                var source = new CellularMapTerrain.Source { Collider = collider, Bounds = collider.bounds,
                    Material = MaterialId.Rock, Renderers = new Renderer[] { renderer }, RendererEnabled = new[] { true }, Surface = renderer.sharedMaterial };
                map = new CellularMapTerrain(new[] {source},root.transform,1f,profile);
                map.Show(true);
                var near = new Vector3Int(2,3,0); var far = new Vector3Int(40,3,0);
                Assert.That(map.TryEdit(near,MaterialId.Empty),Is.True);
                var first = SurfaceAt(root,near.x+.5f,out var nearY);
                Assert.That(first,Is.Not.Null);
                Assert.That(nearY,Is.EqualTo(3f).Within(.001f));
                Assert.That(collider.enabled,Is.False);
                Assert.That(map.TryEdit(far,MaterialId.Empty),Is.True);
                Assert.That(SurfaceAt(root,near.x+.5f,out nearY),Is.SameAs(first));
                Assert.That(SurfaceAt(root,far.x+.5f,out var farY),Is.Not.Null);
                Assert.That(farY,Is.EqualTo(3f).Within(.001f));
                Assert.That(map.TryEdit(near,MaterialId.Rock),Is.True);
                Assert.That(SurfaceAt(root,near.x+.5f,out nearY),Is.Not.Null);
                Assert.That(nearY,Is.EqualTo(4f).Within(.001f));
                Assert.That(collider.enabled,Is.False);
                Assert.That(map.TryEdit(far,MaterialId.Rock),Is.True);
                Assert.That(collider.enabled,Is.True);
                Assert.That(SurfaceAt(root,near.x+.5f,out _),Is.Null);
            }
            finally
            {
                map?.Dispose();
                Object.DestroyImmediate(root); Object.DestroyImmediate(rock); Object.DestroyImmediate(profile);
            }
        }

        private static MeshCollider SurfaceAt(GameObject root, float x, out float height)
        {
            MeshCollider found = null;
            height = float.NegativeInfinity;
            var ray = new Ray(new Vector3(x,8f,.5f),Vector3.down);
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>())
                if (collider.Raycast(ray,out var hit,16f) && hit.point.y > height)
                { found = collider; height = hit.point.y; }
            return found;
        }
    }
}
