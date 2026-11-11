using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run
{
    public sealed partial class OnlineGroundItemWorld
    {
        /// <summary>Home持久地面保留原位置；不重新接地/随机散布，以免每次往返偷偷移动物品。</summary>
        public bool TryRestorePractice(OnlineGroundInventorySnapshot source, OnlineWandResearchStateSnapshot continuity, out string error)
        {
            error = null;
            if (_disposed || _executing || Faulted || _attached.Count != 0 || _models.Count != 0 || _coins != null
                || source == null || source.Items == null || source.Items.Length > OnlineAccountInventorySnapshot.MaximumItems)
            { error = "Home持久地面只能在空World绑定玩家前恢复。"; return false; }
            var prepared = new List<GameObject>(source.Items.Length);
            bool installed = false; _executing = true;
            try
            {
                foreach (var root in source.Items)
                {
                    if (root == null || !_catalog.TryGet(root.ContentId, out var definition))
                    { error = "Home地面包含未知原物品。"; return false; }
                    var model = WorldItemVisualFactory.Create(definition); prepared.Add(model); model.SetActive(false);
                    SceneManager.MoveGameObjectToScene(model, _scene); model.transform.SetPositionAndRotation(root.Position, root.Rotation);
                    if (model.GetComponent<WorldItemPickup>() == null) { error = "Home原物品缺少地面模型绑定。"; return false; }
                }
                if (!_ground.TryRestorePracticeWithContinuity(source, continuity, out error)) return false;
                installed = true;
                for (int i = 0; i < prepared.Count; i++)
                {
                    var model = prepared[i]; var root = source.Items[i]; var proxy = model.GetComponent<WorldItemPickup>();
                    if (!proxy.TryBindOnlineAuthority(_ground.Store, root.InstanceId, root.WorldOwnerId))
                        throw new InvalidOperationException("Home资产已恢复，但原地面模型无法绑定同一ID。");
                    _models.Add(root.InstanceId, proxy); prepared[i] = null; model.SetActive(true);
                }
                return true;
            }
            catch (Exception exception)
            {
                if (installed) Faulted = true;
                error = "Home地面装配失败，保留原资产与现场：" + exception.Message; return false;
            }
            finally { foreach (var model in prepared) if (model != null) DestroyOwned(model); _executing = false; }
        }

        public bool TryCapturePracticeContinuity(OnlineWandResearchStateSnapshot target, out string error)
            => _ground.TryCapturePracticeContinuity(target, out error);

        /// <summary>业务收据已经接收这些ID后只卸运行中模型；不能再登记为终局销毁。</summary>
        public void CompletePracticeHandoff()
        {
            foreach (var proxy in _models.Values) if (proxy != null)
            { proxy.ReleaseOnlineBinding(); proxy.gameObject.SetActive(false); DestroyOwned(proxy.gameObject); }
            _models.Clear(); _ground.CompletePracticeHandoff();
        }
    }
}
