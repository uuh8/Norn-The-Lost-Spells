using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// 共同 HUD 接管时暂时隐藏旧的纯表现叶树，退出后恢复其原激活位。
    /// 记录属于 Host 当前 lease，而非各个 Scene Scope：否则旧 Scope 迟到恢复时，
    /// 会把新 Context 已替代的旧血条重新打开。这里只改变可见性，不移动或销毁原资源。
    /// </summary>
    public sealed class CombatHudLegacySuppression
    {
        private struct SavedRoot
        {
            public GameObject Root;
            public bool Active;
        }
        private struct SavedStage
        {
            public DemoRunHudController Adapter;
            public bool Enabled;
        }
        private readonly List<GameObject> _candidates = new List<GameObject>(16);
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<SavedRoot> _hidden = new List<SavedRoot>(16);
        private readonly List<DemoRunHudController> _stageCandidates = new List<DemoRunHudController>(4);
        private readonly HashSet<int> _seenStages = new HashSet<int>();
        private readonly List<SavedStage> _stages = new List<SavedStage>(4);

        /// <summary>
        /// 只在 Context 切换时检查两个明确 Scene。普通 Raid 的 Actor 在副本而背包仍可能在 Hub，
        /// 所以两边的旧 HUD 都需移交；不能每帧 FindObjects，也不能把 DontDestroy 的新 HUD 自己隐藏。
        /// 先验证全部候选再变更，错误布局不会留下只隐藏了一半的旧界面。
        /// </summary>
        public void Suppress(Scene world, Scene inventoryScene, Transform persistentHud)
        {
            if (_hidden.Count != 0 || _stages.Count != 0) throw new InvalidOperationException("旧 HUD 尚未恢复，不能叠加另一份抑制记录。");
            try
            {
                Collect(world, persistentHud);
                if (inventoryScene.IsValid() && inventoryScene.handle != world.handle) Collect(inventoryScene, persistentHud);
                for (int i = 0; i < _candidates.Count; i++) RequirePureLeaf(_candidates[i]);
                // 全部布局通过后才记录和变更。先快照所有 active/enabled，因为 Stage.OnDisable
                // 会主动收起通知；边禁用边记录会把作者原来的 active=true 错记为 false。
                for (int i = 0; i < _candidates.Count; i++)
                {
                    var node = _candidates[i];
                    _hidden.Add(new SavedRoot { Root = node, Active = node.activeSelf });
                }
                for (int i = 0; i < _stageCandidates.Count; i++)
                    _stages.Add(new SavedStage { Adapter = _stageCandidates[i], Enabled = _stageCandidates[i].enabled });
                // Adapter 在两个叶树之外，不能只关叶树让它继续响应事件并重新打开文字或通知。
                // enabled=false 只停此表现组件；它引用的单机 RunController 和父 Canvas 始终由原 Scene 拥有。
                for (int i = 0; i < _stages.Count; i++) _stages[i].Adapter.enabled = false;
                for (int i = 0; i < _hidden.Count; i++) _hidden[i].Root.SetActive(false);
            }
            catch
            {
                // 预检失败时尚无快照；变更阶段出错则按完整快照还原，不能留下半套被抑制的旧界面。
                Restore();
                throw;
            }
            finally
            {
                // 临时候选不能在错误/退出后继续强引用旧场景的对象。
                _candidates.Clear(); _seen.Clear(); _stageCandidates.Clear(); _seenStages.Clear();
            }
        }

        private void Collect(Scene scene, Transform persistentHud)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            // GetRootGameObjects/GetComponentsInChildren 会生成数组，只允许出现在离散装配阶段。
            var roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                var components = roots[r].GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] is DemoRunHudController stage &&
                        (persistentHud == null || !stage.transform.IsChildOf(persistentHud)))
                    {
                        RequireStageLeaves(stage, scene);
                        AddCandidate(stage.GameplayPresentationRoot); AddCandidate(stage.NotificationPresentationRoot);
                        if (_seenStages.Add(stage.GetInstanceID())) _stageCandidates.Add(stage);
                    }
                    if (!(components[i] is ICombatHudElement element)) continue;
                    var node = element.PresentationRoot;
                    if (node == null || node.scene.handle != scene.handle ||
                        (persistentHud != null && node.transform.IsChildOf(persistentHud))) continue;
                    AddCandidate(node);
                }
            }
        }

        private void AddCandidate(GameObject root)
        { if (_seen.Add(root.GetInstanceID())) _candidates.Add(root); }

        private static void RequireStageLeaves(DemoRunHudController stage, Scene scene)
        {
            var gameplay = stage.GameplayPresentationRoot; var notification = stage.NotificationPresentationRoot;
            if (gameplay == null || notification == null || gameplay == notification ||
                gameplay.scene.handle != scene.handle || notification.scene.handle != scene.handle ||
                !gameplay.transform.IsChildOf(stage.transform) || !notification.transform.IsChildOf(stage.transform) ||
                stage.transform.IsChildOf(gameplay.transform) || stage.transform.IsChildOf(notification.transform))
                throw new InvalidOperationException("Stage必须声明同Scene内的独立表现叶树，不能隐藏Adapter祖先、跨Scene引用或整个Canvas。");
        }

        private static void RequirePureLeaf(GameObject root)
        {
            // 这些组件拥有角色/资产/交互或其他窗口生命周期。即使作者把 HUD 挂到了错误父节点，
            // 也不能为避免重复画面而关闭它们；应修正 authoring 的叶树划分。
            if (root.GetComponentInChildren<HealthComponent>(true) != null ||
                root.GetComponentInChildren<DemoRunController>(true) != null ||
                root.GetComponentInChildren<SinglePlayerInventorySession>(true) != null ||
                root.GetComponentInChildren<PracticeInventoryWorkspace>(true) != null ||
                root.GetComponentInChildren<SinglePlayerInventoryController>(true) != null ||
                root.GetComponentInChildren<DemoPauseMenuController>(true) != null ||
                root.GetComponentInChildren<GameplayGuideController>(true) != null ||
                root.GetComponentInChildren<EventSystem>(true) != null)
                throw new InvalidOperationException("HUD 根混入了角色、库存或交互生命周期，拒绝整体隐藏：" + root.name);
        }

        /// <summary>恢复原 activeSelf 而非强行全开；Unity 已销毁的节点用 == 判空，正常跨图时直接略过。</summary>
        public void Restore()
        {
            Exception failure = null;
            // 先把叶树放回原激活位，此时旧 Stage Adapter 仍禁用；全部恢复后才允许 OnEnable
            // 重新订阅和请求视图，避免其同步重放结果又被后续旧快照覆盖。原 disabled 必须仍为 disabled。
            for (int i = 0; i < _hidden.Count; i++)
            {
                try { if (_hidden[i].Root != null) _hidden[i].Root.SetActive(_hidden[i].Active); }
                catch (Exception error) { if (failure == null) failure = error; }
            }
            for (int i = 0; i < _stages.Count; i++)
            {
                try { if (_stages[i].Adapter != null) _stages[i].Adapter.enabled = _stages[i].Enabled; }
                catch (Exception error) { if (failure == null) failure = error; }
            }
            _hidden.Clear(); _stages.Clear();
            // 即使单个已损坏对象恢复失败，也先尝试其它快照并释放强引用，然后保留异常而非伪称恢复成功。
            if (failure != null) throw new InvalidOperationException("旧HUD恢复未完整成功，请检查首个恢复异常。", failure);
        }
    }
}
