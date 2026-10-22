using System;
using Game.ElementField;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    [CustomEditor(typeof(StableLiquidRegionAuthoring))]
    internal sealed class StableLiquidRegionAuthoringEditor : Editor
    {
        private string _validation;
        private MessageType _validationType;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool colliderBounds = serializedObject.FindProperty("_boundsCollider").objectReferenceValue != null;
            SerializedProperty property = serializedObject.GetIterator();
            bool enter = true;
            while (property.NextVisible(enter))
            {
                enter = false;
                // 有 Collider 时隐藏未使用的旧 World Bounds，避免修改了错误的体积数据。
                if (colliderBounds && property.name == "_worldBounds") continue;
                using (new EditorGUI.DisabledScope(property.name == "m_Script" || property.name == "_regionId"))
                    EditorGUILayout.PropertyField(property, true);
            }
            serializedObject.ApplyModifiedProperties();
            var authoring = (StableLiquidRegionAuthoring)target;
            if (authoring.Liquids == null)
            { EditorGUILayout.HelpBox("先连接 Grid Liquid World。可用 Tools > Element World > Authoring 配置。", MessageType.Error); return; }
            var world = authoring.Liquids.GetComponent<ElementWorldRuntime>();
            var worldProfile = world != null ? new SerializedObject(world).FindProperty("_profile").objectReferenceValue as ElementWorldProfile : null;
            var liquidProfile = new SerializedObject(authoring.Liquids).FindProperty("_profile").objectReferenceValue as LiquidWorldProfile;
            if (worldProfile == null || liquidProfile == null) return;
            LiquidVolumeSettings volume;
            try { volume = liquidProfile.CreateVolumeSettings(worldProfile.CellSize); }
            catch (ArgumentOutOfRangeException)
            {
                EditorGUILayout.HelpBox("液体分辨率或单位体积配置无效，请先修正 Liquid World Profile。",MessageType.Error);
                return;
            }
            // Editor 预检必须和 Runtime 使用同一粗格；细格预算会错误拒绝可用湖盆。
            float cellSize = volume.CellSize;
            Vector3 origin = world.transform.position;
            Bounds bounds = authoring.AuthoringBounds;
            var minimum = ElementWorldCoordinates.WorldToGlobalCell(bounds.min, origin, cellSize);
            var maximum = ElementWorldCoordinates.WorldToGlobalCell(bounds.max - Vector3.one * cellSize * .0001f, origin, cellSize) + Vector3Int.one;
            Vector3Int size = maximum - minimum;
            int chunk = worldProfile.ChunkSize;
            int sectorsX = Mathf.FloorToInt((maximum.x - 1) / (float)chunk) - Mathf.FloorToInt(minimum.x / (float)chunk) + 1;
            int sectorsZ = Mathf.FloorToInt((maximum.z - 1) / (float)chunk) - Mathf.FloorToInt(minimum.z / (float)chunk) + 1;
            int sectors = sectorsX * sectorsZ;
            long worstSector = (long)Mathf.Min(size.x, chunk) * Mathf.Min(size.z, chunk) * size.y;
            int verticalPages = Mathf.FloorToInt((maximum.y - 1) / (float)chunk) - Mathf.FloorToInt(minimum.y / (float)chunk) + 1;
            bool budget = worstSector <= liquidProfile.MaximumImmediateExpansionCells
                && verticalPages <= liquidProfile.MaximumImmediateExpansionSurfaceChunks
                && sectors <= liquidProfile.MaximumStableRegions;
            EditorGUILayout.HelpBox($"Cell {cellSize:0.###} m；搜索格 {size.x} × {size.y} × {size.z}；最多 {sectors} 个稳定分片\n"
                + $"每格容量 {volume.CapacityGmu} GMU；单位 GMU 体积 {volume.CubicMetersPerGmu:0.########} m³\n"
                + $"单片最多 {worstSector} 格，预算 {liquidProfile.MaximumImmediateExpansionCells}；竖向页 {verticalPages}\n"
                + "Initial Fill 是液量/容量比例。所有湖共享 Region/Patch 预算。Bounds 最低面应覆盖最深湖底，最高面需低于连续岸顶。",
                budget ? MessageType.Info : MessageType.Error);
            if (!Application.isPlaying && GUILayout.Button("检查湖盆地形与封闭岸线"))
            {
                _validation = Validate(authoring, world, minimum, maximum, worldProfile, liquidProfile,cellSize);
                _validationType = _validation.StartsWith("通过", StringComparison.Ordinal) ? MessageType.Info : MessageType.Error;
            }
            if (!string.IsNullOrEmpty(_validation)) EditorGUILayout.HelpBox(_validation, _validationType);
        }

        private static string Validate(StableLiquidRegionAuthoring a, ElementWorldRuntime world,
            Vector3Int min, Vector3Int max, ElementWorldProfile wp, LiquidWorldProfile lp,float cellSize)
        {
            var collider = new SerializedObject(a).FindProperty("_boundsCollider").objectReferenceValue as BoxCollider;
            if (collider != null && (!collider.enabled || !collider.isTrigger || !StableLiquidRegionAuthoring.IsAxisAligned(collider.transform)))
                return "Bounds Collider 需要启用、勾选 Is Trigger，并与世界坐标轴对齐。";
            var baker = world.GetComponent<ElementFieldSolidBaker>();
            if (baker == null || new SerializedObject(baker).FindProperty("_solidLayerMask").intValue == 0)
                return "Solid Baker 或 Solid Layer Mask 无效。";
            int width = max.x - min.x, depth = max.z - min.z, height = max.y - min.y;
            if (width < 1 || depth < 1 || height < 1 || (long)width * depth * height > (long)lp.MaximumStableRegionCells * lp.MaximumStableRegions)
                return "搜索体积为空或超过稳定容量预算。";
            long sectorCells = (long)Mathf.Min(width, wp.ChunkSize) * Mathf.Min(depth, wp.ChunkSize) * height;
            if (sectorCells > lp.MaximumImmediateExpansionCells)
                return "单片搜索体积超过局部展开预算；先降低 Bounds 深度，或同步配置展开/动态容量。";
            Physics.SyncTransforms();
            var floors = new int[checked(width * depth)];
            int included = 0;
            for (int z = min.z; z < max.z; z++) for (int x = min.x; x < max.x; x++)
            {
                int floor = int.MaxValue; bool emptyFound = false;
                for (int y = min.y; y < max.y; y++)
                {
                    bool solid = baker.IsStaticSolidCell(new Vector3Int(x,y,z), world.transform.position, cellSize);
                    if (solid && emptyFound) return $"列 ({x},{z}) 有悬空顶板；当前湖盆支持 Heightfield，请移出洞穴/顶板。";
                    if (!solid && !emptyFound) { floor = y; emptyFound = true; }
                }
                if (floor != int.MaxValue && !baker.IsStaticSolidCell(new Vector3Int(x,floor-1,z), world.transform.position, cellSize))
                    return $"列 ({x},{z}) 没有 Solid 湖底。检查 Layer、地形 Collider 和 Bounds 最低面。";
                floors[(z-min.z)*width+x-min.x] = floor;
                if (floor != int.MaxValue) included++;
            }
            if (included == 0) return "Bounds 内全为 Solid，没有可容纳液体的列。";
            for (int z = min.z; z < max.z; z++) for (int x = min.x; x < max.x; x++)
            {
                int floor = floors[(z-min.z)*width+x-min.x];
                for (int y = floor; y < max.y; y++) for (int d = 0; d < 4; d++)
                {
                    int nx=x+(d==0?-1:d==1?1:0), nz=z+(d==2?-1:d==3?1:0);
                    bool neighbor = nx>=min.x && nx<max.x && nz>=min.z && nz<max.z
                        && floors[(nz-min.z)*width+nx-min.x] <= y;
                    if (!neighbor && !baker.IsStaticSolidCell(new Vector3Int(nx,y,nz), world.transform.position, cellSize))
                        return $"岸线在 ({nx},{y},{nz}) 开口。缩小 Bounds 或补齐连续 Solid 岸线；排水使用 Outlet。";
                }
            }
            return $"通过地形/岸线检查：{included} 列可容纳液体。进入 Play 后应得到 Region Id >= 0；连通性、所有湖的共享预算与重叠仍由 Runtime 最终校验。";
        }
    }
}
