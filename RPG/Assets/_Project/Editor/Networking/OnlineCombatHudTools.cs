using System;
using Game.Combat;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Project.Editor
{
    /// <summary>显式在线场景安装器：取既有单机 HP 子树的美术，只写传入的 Online Scene，不保存/修改取样场景。</summary>
    public static class OnlineCombatHudTools
    {
        private const string TemplateScene = "Assets/_Project/Scenes/Tests/Hub/P7_DungeonReview.unity";
        private const string ManaSpritePath = "Assets/_Project/Art/UI/MP_line.png";
        private const string BossPrefabPath = "Assets/_Project/Art/Prefabs/Boss/Boss.prefab";

        /// <summary>
        /// 在线 Registry 直接生成 Boss Prefab，不会得到 P8 Scene 实例额外添加的 Teleport 表现。
        /// 将纯显示 Adapter 明确写回同一个正式 Prefab；不写 P8 场景，也不重建 Prefab/meta。
        /// </summary>
        public static void InstallBossPresentation(GameObject prefab)
        {
            if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab) || AssetDatabase.GetAssetPath(prefab) != BossPrefabPath)
                throw new InvalidOperationException("Boss Teleport Presentation 只允许安装到正式 Boss.prefab 资产。");
            var open = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/P7/PortalPurpleOpen.prefab");
            var idle = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/P7/PortalPurpleIdle.prefab");
            var close = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/P7/PortalPurpleClose.prefab");
            if (open == null || idle == null || close == null)
                throw new InvalidOperationException("既有 Purple Portal Open/Idle/Close 资源缺失，不能安装空引用的 Boss 表现。");
            var root = PrefabUtility.LoadPrefabContents(BossPrefabPath);
            try
            {
                var health = root.GetComponent<HealthComponent>();
                if (health == null) throw new InvalidOperationException("正式 Boss Root 缺少 HealthComponent。");
                var presenters = root.GetComponentsInChildren<BossTeleportVfxController>(true);
                if (presenters.Length > 1) throw new InvalidOperationException("Boss Prefab 已有多份 Teleport Presenter，请先排查重复装配。");
                BossTeleportVfxController presenter;
                bool created = presenters.Length == 0;
                if (presenters.Length == 1) presenter = presenters[0];
                else
                {
                    var child = new GameObject("OnlineBossTeleportPresentation"); child.transform.SetParent(root.transform, false);
                    presenter = child.AddComponent<BossTeleportVfxController>();
                }
                var data = new SerializedObject(presenter);
                Set(data, "_bossHealth", health); Set(data, "_openPrefab", open); Set(data, "_idlePrefab", idle); Set(data, "_closePrefab", close);
                data.FindProperty("_visualHeightOffset").floatValue = 1.5f;
                data.FindProperty("_openDuration").floatValue = .35f; data.FindProperty("_closeDuration").floatValue = .35f;
                // 仅本工具新增的共享 Presenter 让位；旧 P8 Scene 自己的 Adapter 默认 false，不改原表现 Owner。
                if (created) data.FindProperty("_preferExistingPresenter").boolValue = true;
                data.ApplyModifiedPropertiesWithoutUndo();
                // SaveAsPrefabAsset 保留资产身份与已有序列化引用；不删除再创建，避免 GUID 变化。
                PrefabUtility.SaveAsPrefabAsset(root, BossPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        public static void Install(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != RoomLifecycleTools.PortalScenePath && scene.path != RoomLifecycleTools.ScenePath)
                throw new InvalidOperationException("Combat HUD 只允许安装到已加载的 OnlineRoom / RoomLifecycleLab；正式单机场景不可作为写入目标。");
            foreach (var root in scene.GetRootGameObjects())
                if (root.GetComponentInChildren<OnlineCombatHudController>(true) != null) return;

            var sample = SceneManager.GetSceneByPath(TemplateScene);
            bool openedHere = !sample.IsValid() || !sample.isLoaded;
            if (openedHere) sample = EditorSceneManager.OpenScene(TemplateScene, OpenSceneMode.Additive);
            GameObject canvasRoot = null;
            try
            {
                PlayerHealthBar source = null;
                foreach (var root in sample.GetRootGameObjects())
                { source = root.GetComponentInChildren<PlayerHealthBar>(true); if (source != null) break; }
                if (source == null) throw new InvalidOperationException("既有 P7 HUD 取样缺少 PlayerHealthBar，拒绝无美术的替代面板。");

                canvasRoot = new GameObject("OnlineCombatHud", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(OnlineCombatHudController));
                SceneManager.MoveGameObjectToScene(canvasRoot, scene);
                var canvas = canvasRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 12;
                var scaler = canvasRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1280, 800); scaler.matchWidthOrHeight = .5f;

                var local = Rect("LocalResources", canvasRoot.transform, new Vector2(.5f, 0), new Vector2(0, 68), new Vector2(540, 110));
                var boss = Rect("BossResources", canvasRoot.transform, new Vector2(.5f, 1), new Vector2(0, -82), new Vector2(660, 70));
                var hp = CloneBar(source, local, "HP", new Vector2(0, 26), 440, out var hpFill, out var hpValue);
                var mana = CloneBar(source, local, "Mana", new Vector2(0, -9), 440, out var manaFill, out var manaValue);
                CloneBar(source, boss, "BossHP", Vector2.zero, 640, out var bossFill, out var bossValue);
                var manaSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ManaSpritePath);
                if (manaSprite == null) throw new InvalidOperationException("现有 MP_line Sprite 缺失，不能把红贴图染成错误蓝条。");
                manaFill.sprite = manaSprite;
                var font = hpValue.font;
                Label("HPTitle", hp.transform, "HP", font, new Vector2(-246, 0), new Vector2(45, 30), 19);
                Label("ManaTitle", mana.transform, "MP", font, new Vector2(-246, 0), new Vector2(45, 30), 19);
                Label("ChargesTitle", local, "次数", font, new Vector2(-195, -42), new Vector2(50, 28), 18);
                var charges = Label("ChargesValue", local, "", font, new Vector2(-115, -42), new Vector2(110, 28), 19);
                Label("CooldownTitle", local, "冷却", font, new Vector2(73, -42), new Vector2(50, 28), 18);
                var cooldown = Label("CooldownValue", local, "", font, new Vector2(152, -42), new Vector2(105, 28), 19);
                Label("CooldownUnit", local, "s", font, new Vector2(211, -42), new Vector2(20, 28), 18);
                Label("BossTitle", boss, "魔法师", font, new Vector2(-248, 31), new Vector2(140, 28), 21);
                Label("PhaseTitle", boss, "阶段", font, new Vector2(232, 31), new Vector2(60, 28), 18);
                var phase = Label("PhaseValue", boss, "", font, new Vector2(296, 31), new Vector2(60, 28), 19);

                var data = new SerializedObject(canvasRoot.GetComponent<OnlineCombatHudController>());
                Set(data, "_localRoot", local.gameObject); Set(data, "_bossRoot", boss.gameObject);
                Set(data, "_hpFill", hpFill); Set(data, "_manaFill", manaFill); Set(data, "_bossFill", bossFill);
                Set(data, "_hpValue", hpValue); Set(data, "_manaValue", manaValue); Set(data, "_bossValue", bossValue);
                Set(data, "_chargesValue", charges); Set(data, "_cooldownValue", cooldown); Set(data, "_phaseValue", phase);
                data.ApplyModifiedPropertiesWithoutUndo();
                // 隐藏未收到事实的零值条；Controller 留在 Canvas root 上，因此隐藏子树不会停止订阅。
                local.gameObject.SetActive(false); boss.gameObject.SetActive(false);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            catch { if (canvasRoot != null) Object.DestroyImmediate(canvasRoot); throw; }
            finally { if (openedHere) EditorSceneManager.CloseScene(sample, true); }
        }

        private static GameObject CloneBar(PlayerHealthBar source, Transform parent, string name, Vector2 position, float width,
            out Image fill, out Text value)
        {
            var clone = Object.Instantiate(source.gameObject, parent, false); clone.name = name;
            // Instantiate 只复制 HP 子树。移除原单机事件/Health 引用，保留图形和 Text；不复制 Canvas/session/玩家。
            var component = clone.GetComponent<PlayerHealthBar>(); var serialized = new SerializedObject(component);
            fill = (Image)serialized.FindProperty("_fill").objectReferenceValue;
            value = (Text)serialized.FindProperty("_valueLabel").objectReferenceValue;
            Object.DestroyImmediate(component);
            if (fill == null || value == null) { Object.DestroyImmediate(clone); throw new InvalidOperationException("单机 HP 子树的 Fill/Value 引用缺失。"); }
            var rect = clone.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = position; rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
            // 子图形本来就是 stretch anchors；调整根宽度即可，字体不随整棵树缩小，维持原单机的可读字号。
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
            foreach (var graphic in clone.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            value.text = string.Empty; value.horizontalOverflow = HorizontalWrapMode.Overflow; value.verticalOverflow = VerticalWrapMode.Overflow;
            clone.SetActive(true); return clone;
        }
        private static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
        private static Text Label(string name, Transform parent, string text, Font font, Vector2 position, Vector2 size, int fontSize)
        {
            var rect = Rect(name, parent, new Vector2(.5f, .5f), position, size); var label = rect.gameObject.AddComponent<Text>();
            label.font = font; label.fontSize = fontSize; label.color = Color.white; label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow; label.verticalOverflow = VerticalWrapMode.Overflow; label.raycastTarget = false; label.text = text;
            var shadow = rect.gameObject.AddComponent<Outline>(); shadow.effectColor = new Color(0, 0, 0, .85f); shadow.effectDistance = new Vector2(1, -1);
            return label;
        }
        private static void Set(SerializedObject data, string field, Object value) => data.FindProperty(field).objectReferenceValue = value;
    }
}
