using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// 场景中的临时表现使用者。Solo 由共同工作区/Run 边界显式 Bind；Online 只消费本 Scene 的认证后角色事实。
    /// 此组件不常驻、不加载账号或存档、不拥有 Gameplay，其 lease 只能释放自己取得的 Context。
    /// </summary>
    public sealed class CombatPresentationScope : MonoBehaviour
    {
        [SerializeField] private bool _online;
        private CombatPresentationLease _lease;
        private CombatPresentationContext _context;
        private GameObject _onlineInventoryRoot;
        private SinglePlayerInventoryController _onlineInventory;
        private NearbyPickupListView _onlineNearbyList;
        private WandResearchController _onlineResearch;
        private GameObject _onlineResearchRoot;
        private bool _warnedInventoryMissing;
        public const string InventoryResourcePath = "Presentation/CombatInventory";
        public bool IsBound => CombatPresentationHost.IsCurrent(_lease);
        public bool LastBindSucceeded { get; private set; }

        private void OnEnable()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            SceneManager.sceneUnloaded += OnWorldUnloaded;
            SceneManager.sceneLoaded += OnWorldLoaded;
            if (!_online) return;
            EventBus<LocalActorPresentationEvent>.Subscribe(OnLocalActor);
            EventBus<OnlineInventoryPresentationEvent>.Subscribe(OnOnlineInventory);
            EventBus<OnlineHomeResearchAccessChangedEvent>.Subscribe(OnHomeResearchAccessChanged);
            // Scene 与网络 Actor 的创建顺序不固定；晚启用 Scope 重放已有事实，不轮询全场景。
            OnLocalActor(new LocalActorPresentationEvent
            { Actor = LocalActorPresentation.Actor, CrosshairVisible = LocalActorPresentation.CrosshairVisible });
#endif
        }

        /// <summary>
        /// 共同入口取得真实 Context 后调用。成功状态可供调用方决定是否抑制旧纯表现；失败保留原场景 UI。
        /// 已被另一 Scope 替换的旧 lease 不能靠迟到 Bind 自动抢回当前 HUD。
        /// </summary>
        public void Bind(in CombatPresentationContext context)
        {
            if (!CombatPresentationHost.CanBind(context)) { Clear(); return; }
            if (_lease.IsValid)
            {
                if (!CombatPresentationHost.IsCurrent(_lease)) { LastBindSucceeded = false; return; }
                if (_context.WorldScene.handle == context.WorldScene.handle)
                {
                    LastBindSucceeded = CombatPresentationHost.TryUpdate(_lease, context);
                    if (LastBindSucceeded) _context = context; else Clear();
                    return;
                }
                // 同一使用者跨世界也必须换 Generation，旧世界的任何延迟释放才不会命中新世界。
                Clear();
            }
            LastBindSucceeded = CombatPresentationHost.TryAcquire(this, context, out _lease);
            _context = LastBindSucceeded ? context : default;
        }

        /// <summary>先清本地引用再申请 Host 释放；即使已经失去 lease，也不会影响新使用者。</summary>
        public void Clear()
        {
            ClearOnlineResearch();
            var lease = _lease; _lease = default; _context = default; LastBindSucceeded = false;
            CombatPresentationHost.Release(lease);
            // Controller 留在本 Scene。退出先正常 Close 释放本机输入，再销毁 View；只有纯 HUD 留在 DDOL。
            if (_onlineInventory != null) _onlineInventory.Close();
            var root = _onlineInventoryRoot; _onlineInventory = null; _onlineNearbyList = null; _onlineInventoryRoot = null;
            if (root != null) { if (Application.isPlaying) Destroy(root); else DestroyImmediate(root); }
        }
        private void OnDisable()
        {
            SceneManager.sceneUnloaded -= OnWorldUnloaded;
            SceneManager.sceneLoaded -= OnWorldLoaded;
            EventBus<LocalActorPresentationEvent>.Unsubscribe(OnLocalActor);
            EventBus<OnlineInventoryPresentationEvent>.Unsubscribe(OnOnlineInventory);
            EventBus<OnlineHomeResearchAccessChangedEvent>.Unsubscribe(OnHomeResearchAccessChanged);
            Clear();
        }

        private void OnLocalActor(LocalActorPresentationEvent state)
        {
            // Hub/其他 additive Scene 的 Actor 事实没有此区域的显示资格；只能清自己的 lease。
            if (state.Actor == null || state.Actor.scene.handle != gameObject.scene.handle) { Clear(); return; }
            var next = _context.Actor == state.Actor ? _context : default;
            next.Actor = state.Actor; next.WorldScene = gameObject.scene; next.CrosshairVisible = state.CrosshairVisible;
            // 缓存由通过校验的 Character Replica 发布。Actor 对不上时保持空，绝不拿保留 Hub 的 Session 回落。
            next.InventorySource = OnlineInventoryPresentation.Actor == state.Actor ? OnlineInventoryPresentation.Source : null;
            Bind(next);
            if (LastBindSucceeded) TryBindOnlineInventory(state.Actor, next.InventorySource);
        }

        /// <summary>
        /// 接受当前本机 Actor 的只读来源。库存 UI 按 Scene 创建，先显式关闭单机回落再启用原 Controller；
        /// 同一稳定 Source 仅更新其内部读数，不能重复 BindPresentationSource 而关闭正在打开的 Tab。
        /// </summary>
        public bool TryBindOnlineInventory(GameObject actor, IInventoryPresentationSource source)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return false;
#else
            if (!_online || actor == null || actor != _context.Actor || actor != LocalActorPresentation.Actor ||
                actor.scene.handle != gameObject.scene.handle || !IsBound || source is SinglePlayerInventorySession) return false;
            if (source is Object value && value == null) source = null;
            if (!EnsureOnlineInventory()) return false;
            if (!_onlineInventory.PresentationIsReadOnly || !ReferenceEquals(_onlineInventory.PresentationSource, source))
                _onlineInventory.BindPresentationSource(source);
            // 附近候选属于Scene Actor，不随DDOL HUD常驻，也不能继续引用预制体里的单机Session。
            // Character的原F输入从同Actor晚绑定这一个Controller；UI层不反向引用Character程序集。
            var nearby = actor.GetComponent<NearbyPickupController>();
            if (nearby == null) nearby = actor.AddComponent<NearbyPickupController>();
            nearby.ConfigureOnline(source, actor.transform);
            _onlineInventory.ConfigureOnlineNearby(nearby);
            // F 的候选与屏幕提示必须使用同一个 Nearby Controller；只接 Tab 网格会导致能拾取却无提示。
            // 列表从原工作区同源导出，随此 Scene UI 释放，不能跟随 DDOL HUD 保留旧角色/门引用。
            _onlineNearbyList.ConfigureOnline(_onlineInventory, nearby, actor.GetComponent<Collider>());
            var next = _context; next.InventoryController = _onlineInventory; next.InventorySource = source;
            Bind(next);
            if (LastBindSucceeded) BindOnlineResearch();
            return LastBindSucceeded;
#endif
        }

        private bool EnsureOnlineInventory()
        {
            if (_onlineInventory != null) return true;
            var prefab = Resources.Load<GameObject>(InventoryResourcePath);
            if (prefab == null || prefab.activeSelf)
            {
                if (!_warnedInventoryMissing)
                {
                    _warnedInventoryMissing = true;
                    GameLog.Warn("共同库存资源缺失或未保持 inactive，请先 Export Shared Combat Inventory；HUD 保留，库存不回退单机档。", "CombatPresentation");
                }
                return false;
            }
            _onlineInventoryRoot = Instantiate(prefab, transform, false);
            _onlineInventoryRoot.name = "SharedCombatInventory";
            _onlineInventory = _onlineInventoryRoot.GetComponentInChildren<SinglePlayerInventoryController>(true);
            _onlineNearbyList = _onlineInventoryRoot.GetComponentInChildren<NearbyPickupListView>(true);
            if (_onlineInventory == null || _onlineNearbyList == null)
            {
                var invalid = _onlineInventoryRoot; _onlineInventoryRoot = null; _onlineInventory = null; _onlineNearbyList = null;
                if (Application.isPlaying) Destroy(invalid); else DestroyImmediate(invalid);
                GameLog.Warn("共同库存资源缺少原 Controller 或附近提示，请重新 Export Shared Combat Inventory。", "CombatPresentation"); return false;
            }
            // Instantiate 保留 inactive 根，所以原 Controller 尚未订阅输入或执行 Start。
            // 显式 null override 的含义是等待线上数据；即使作者误留字段，也不能自动读取单机 Session。
            _onlineInventory.BindPresentationSource(null);
            _onlineInventoryRoot.SetActive(true);
            return true;
        }

        private void OnOnlineInventory(OnlineInventoryPresentationEvent state)
        {
            // Actor 可能先于 Scene 被销毁；Unity 的 == 也会识别这种 fake null，立即结束自己的临时 UI 引用。
            if (_context.Actor == null)
            {
                // 首次Actor事件可以发生在加载Scene的Awake内，此时Scene.isLoaded还是false，Host正确拒绝绑定。
                // 库存稍后就绪时重放同一个可信本机Actor，而非把失败Context永远当作“没有本机角色”。
                ReplayCurrentActor();
                if (_context.Actor == null) { Clear(); return; }
            }
            if (state.Actor == _context.Actor) TryBindOnlineInventory(state.Actor, state.Source);
            else if (state.Actor == null && OnlineInventoryPresentation.Actor == null)
                TryBindOnlineInventory(_context.Actor, null);
        }
        private void OnHomeResearchAccessChanged(OnlineHomeResearchAccessChangedEvent change)
        {
            if (!_online || !IsBound || change.WorldSceneHandle!=0 && change.WorldSceneHandle!=gameObject.scene.handle) return;
            BindOnlineResearch();
        }
        private void BindOnlineResearch()
        {
            // 和平区域能力可能晚于Actor/库存到达。只重放同Actor同Scene的Core缓存；普通副本没有该能力，
            // 不创建研究UI，更不能沿保留Hub的HubInteractionController猜测权限或引用旧Session。
            if (_context.Actor==null || !(_context.InventorySource is OnlineInventoryPresentationSource source)
                || !OnlineHomeResearchPresentation.TryGet((int)gameObject.scene.handle,_context.Actor,out var access))
            { ClearOnlineResearch(); return; }
            if (_onlineResearch==null)
            {
                _onlineResearchRoot=new GameObject("SharedHomeResearch"); _onlineResearchRoot.SetActive(false);
                _onlineResearchRoot.transform.SetParent(transform,false);
                _onlineResearch=_onlineResearchRoot.AddComponent<WandResearchController>();
                _onlineResearch.BindOnlineInventory(_onlineInventory);
                _onlineResearch.ConfigureOnline(source,access);
                _onlineResearchRoot.SetActive(true);
            }
            else { _onlineResearch.BindOnlineInventory(_onlineInventory); _onlineResearch.ConfigureOnline(source,access); }
        }
        private void ClearOnlineResearch()
        {
            if (_onlineResearch!=null) _onlineResearch.ConfigureOnline(null,null);
            var root=_onlineResearchRoot; _onlineResearch=null; _onlineResearchRoot=null;
            if (root!=null) { if (Application.isPlaying) Destroy(root); else DestroyImmediate(root); }
        }
        private void OnWorldLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.handle == gameObject.scene.handle) ReplayCurrentActor();
        }
        private void ReplayCurrentActor()
        {
            if (!_online || !isActiveAndEnabled || !gameObject.scene.isLoaded) return;
            // sceneLoaded在Awake/OnEnable后、Start前通知。利用离散生命周期边界补绑定，不在Update轮询全场景，
            // 不放宽Host对真实已加载Scene和Actor归属的检查，也不重建Authority或重新发放资产。
            OnLocalActor(new LocalActorPresentationEvent
                { Actor = LocalActorPresentation.Actor, CrosshairVisible = LocalActorPresentation.CrosshairVisible });
        }
        private void OnWorldUnloaded(Scene scene)
        {
            // sceneUnloaded 时旧 Scene 可能已不再有效，仍要用保存的 handle 清掉临时引用，不能先 IsValid 早退。
            if (_lease.IsValid && _context.WorldScene.handle == scene.handle) Clear();
        }
    }
}
