using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// Client 唯一战斗 HUD 管理根。只让纯 UI 实例常驻，角色、库存 Session、PauseCoordinator
    /// 和服务端模拟都继续由各自 Scene/模块拥有；这里不加载存档、不生成资产、不推进资源 Tick。
    /// </summary>
    public sealed class CombatPresentationHost : MonoBehaviour
    {
        public const string ResourcePath = "Presentation/CombatHud";
        private static CombatPresentationHost _active;
        private static ulong _nextGeneration;
        private GameObject _hudRoot;
        private LocalActorHudBinding _actorBinding;
        private BossHealthBarController _bossHud;
        private DemoRunHudController _stageHud;
        private HeldItemHud _held;
        private Canvas _heldCanvas;
        // 抑制记录属于当前 Host lease；旧 Scope 的迟到释放不得恢复旧 HUD，造成两套界面同时出现。
        private readonly CombatHudLegacySuppression _legacy = new CombatHudLegacySuppression();
        private CombatPresentationContext _context;
        private CombatPresentationLease _lease;

        // 显式使用 Unity 判空，避免返回已销毁 native 对象的托管 wrapper。
        public static CombatPresentationHost Active => _active != null ? _active : null;
        public GameObject HudRoot => _hudRoot;
        public GameObject Actor => _context.Actor;
        public IInventoryPresentationSource InventorySource => AliveSource(_context.InventorySource);
        public bool IsBound => _lease.IsValid;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        // Reset 是 Unity 的实例消息名，Editor AddComponent 会调用它；静态初始化不要占用该名称。
        private static void ResetStatics() { _active = null; _nextGeneration = 0; }

        /// <summary>
        /// 区域确认角色和实际 WorldScene 后申请。只有资源验证成功才返回 lease，调用者随后才可抑制旧 HUD。
        /// 新 Owner 替换旧 Context；生成代次单调递增，旧 Scope 的迟到回调没有清新 UI 的资格。
        /// </summary>
        public static bool TryAcquire(Object owner, in CombatPresentationContext context, out CombatPresentationLease lease)
        {
            lease = default;
#if UNITY_SERVER && !UNITY_EDITOR
            return false; // Dedicated Server 不加载 Canvas、Font 或这个 Client Resource。
#else
            if (owner == null || !CanBind(context)) return false;
            var host = EnsureHost(); if (host == null) return false;
            _nextGeneration++; if (_nextGeneration == 0) _nextGeneration++;
            var candidate = new CombatPresentationLease(owner.GetInstanceID(), _nextGeneration);
            host.ClearCurrent();
            // 接线和旧 HUD 纯度验证成功后才发放有效凭证；失败不能留下一个看似已取得 Context 的 Owner。
            if (!host.TryApply(context)) return false;
            host._lease = candidate; lease = candidate; return true;
#endif
        }

        /// <summary>
        /// 同一有效 lease 可离散刷新来源或角色；跨世界必须 Release 再 Acquire，不能沿用旧世界的释放凭证。
        /// 返回 false 不抢占新 Owner，也不把失败请求安装成当前 Context。实际替换若被纯度预检拒绝，
        /// 旧临时绑定结束并恢复 Scene 原 UI，新 Controller 则从未交给 Host，不能被其关闭。
        /// </summary>
        public static bool TryUpdate(in CombatPresentationLease lease, in CombatPresentationContext context)
        {
            var host = Active;
            if (host == null || !IsCurrent(lease) || !CanBind(context) || host._context.WorldScene.handle != context.WorldScene.handle) return false;
            // Run 进度可能重复发布同一组 Context；这是幂等事实，不得 Close 原 Tab 或取消仍有效的拖拽。
            if (host.Matches(context)) return true;
            return host.TryRebindWithinWorld(context);
        }

        /// <summary>调用者只释放自己的 lease；先解绑纯 UI 和原背包输入，再由 Scene Owner 卸载实际世界。</summary>
        public static void Release(in CombatPresentationLease lease)
        { if (IsCurrent(lease)) _active.ClearCurrent(); }

        internal static bool IsCurrent(in CombatPresentationLease lease) => _active != null && lease.IsValid &&
            _active._lease.OwnerId == lease.OwnerId && _active._lease.Generation == lease.Generation;

        internal static bool CanBind(in CombatPresentationContext context) => context.Actor != null &&
            context.WorldScene.IsValid() && context.WorldScene.isLoaded && context.Actor.scene.handle == context.WorldScene.handle;

        private static IInventoryPresentationSource AliveSource(IInventoryPresentationSource source)
        { return source is Object value && value == null ? null : source; }

        private bool Matches(in CombatPresentationContext context) => ReferenceEquals(_context.Actor, context.Actor) &&
            _context.WorldScene.handle == context.WorldScene.handle &&
            ReferenceEquals(AliveSource(_context.InventorySource), AliveSource(context.InventorySource)) &&
            ReferenceEquals(_context.InventoryController, context.InventoryController) && _context.CrosshairVisible == context.CrosshairVisible;

        /// <summary>
        /// 同世界的只读来源可能从“等待快照”变成 Ready。此时不能停用整个 HUD：Unity 会调用
        /// Boss HUD.OnDisable 清身份，但 Boss 的首次 Initialized 事件不会因为换背包来源自动重发。
        /// 保持表现树启用，只替换变动的引用；旧 Controller 的 Close 也仅在真正更换 Controller 时调用。
        /// </summary>
        private bool TryRebindWithinWorld(in CombatPresentationContext context)
        {
            _legacy.Restore();
            try
            {
                _legacy.Suppress(context.WorldScene,
                    context.InventoryController != null ? context.InventoryController.gameObject.scene : default, _hudRoot.transform);
            }
            catch (System.InvalidOperationException error)
            {
                // 接线拒绝时结束旧 lease，但绝不关闭尚未被接管的新背包，保留可用的场景原界面。
                ClearCurrent();
                GameLog.Warn("共用 HUD 更新被拒绝：" + error.Message, "CombatPresentation");
                return false;
            }
            if (_context.InventoryController != context.InventoryController && _context.InventoryController != null)
                _context.InventoryController.Close();
            if (_context.Actor != context.Actor) _actorBinding.Bind(context.Actor);
            var source = AliveSource(context.InventorySource);
            if (!ReferenceEquals(AliveSource(_context.InventorySource), source)) _held.BindPresentationSource(source);
            _held.ConfigureVisibility(context.InventoryController, null, null, _heldCanvas);
            if (_heldCanvas != null) _heldCanvas.enabled = context.InventoryController == null || !context.InventoryController.IsOpen;
            if (_context.CrosshairVisible != context.CrosshairVisible)
                EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = context.CrosshairVisible });
            _context = context; _context.InventorySource = source;
            return true;
        }

        private static CombatPresentationHost EnsureHost()
        {
            if (_active != null) return _active;
            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            { GameLog.Warn("共用战斗 HUD 资源缺失，请先导出 Presentation/CombatHud；保留原界面。", "CombatPresentation"); return null; }
            var root = new GameObject("CombatPresentationHost"); root.SetActive(false);
            var host = root.AddComponent<CombatPresentationHost>();
            // 父根先 inactive：即使资源作者误勾 active，也不能先触发旧数据源的 Awake/OnEnable。
            host._hudRoot = Instantiate(prefab, root.transform, false); host._hudRoot.name = "SharedCombatHud";
            host._hudRoot.SetActive(false);
            host._actorBinding = host._hudRoot.GetComponentInChildren<LocalActorHudBinding>(true);
            host._bossHud = host._hudRoot.GetComponentInChildren<BossHealthBarController>(true);
            host._stageHud = host._hudRoot.GetComponentInChildren<DemoRunHudController>(true);
            host._held = host._hudRoot.GetComponentInChildren<HeldItemHud>(true);
            if (host._actorBinding == null || host._held == null || !PureClientTree(host._hudRoot))
            {
                GameLog.Error("共用战斗 HUD 不是已验证的纯表现资源，拒绝常驻并保留原界面。", "CombatPresentation");
                if (Application.isPlaying) Destroy(root); else DestroyImmediate(root);
                return null;
            }
            // Host/Scope 是唯一绑定 Owner；禁用克隆的自动网络事件订阅，但仍复用其公开 Bind 方法。
            host._actorBinding.enabled = false;
            // 关卡提示也来自原界面，但克隆不再拥有单机RunController。先明确等待线上读源，
            // 再激活树，防止Start误用原场景序列化的区域文本；空源只隐藏提示叶树，不影响HP/准心。
            if (host._stageHud != null) host._stageHud.BindPresentationSource(null);
            host._heldCanvas = host._held.GetComponentInParent<Canvas>(true);
            _active = host; root.SetActive(true); return host;
        }

        private static bool PureClientTree(GameObject root)
        {
            // 只在第一次克隆的离散路径检查，不在 Update 查层级。Editor 导出器仍负责更严格的表现白名单。
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) return false;
                string ns = behaviours[i].GetType().Namespace;
                if (ns == "Game.Run" || ns == "Game.Character" || ns == "Game.Combat" || ns == "Game.Multiplayer" || ns == "Game.Networking") return false;
            }
            return true;
        }

        private void Awake()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            gameObject.SetActive(false);
#else
            if (_active != null && _active != this) { Destroy(gameObject); return; }
            _active = this;
            // DontDestroyOnLoad 会保留整个 Transform 子树，所以本根绝不能接收 Scene 的 Session/Actor。
            // EditMode 回归不伪造实际跨 Scene 存活；真实常驻行为由 PlayMode/Native 路径另验。
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);
#endif
        }
        private void OnEnable()
        {
            SceneManager.sceneUnloaded += OnWorldUnloaded;
            EventBus<DungeonPresentationSourceChangedEvent>.Subscribe(OnDungeonSourceChanged);
        }
        private void OnDisable()
        {
            SceneManager.sceneUnloaded -= OnWorldUnloaded;
            EventBus<DungeonPresentationSourceChangedEvent>.Unsubscribe(OnDungeonSourceChanged);
            ClearCurrent();
        }
        private void OnDestroy()
        { if (_active == this) { ClearCurrent(); _active = null; } }

        private bool TryApply(in CombatPresentationContext context)
        {
            // 实际 Actor、Resource 已验证才抑制源界面；仅处理纯表现叶树，原 Pause/Guide/Tab 继续留在 Scene。
            // 这里尚未接管新 Controller；预检失败时不能先把它写进 _context，再用 ClearCurrent 误关其面板。
            try
            {
                _legacy.Suppress(context.WorldScene,
                    context.InventoryController != null ? context.InventoryController.gameObject.scene : default, _hudRoot.transform);
            }
            catch (System.InvalidOperationException error)
            {
                // 仅把可预期的 authoring/组合拒绝转换为失败结果；其余编程异常仍保留原始堆栈用于定位。
                _legacy.Restore(); _context = default; _lease = default;
                GameLog.Warn("共用 HUD 接管被拒绝，已保留场景原界面：" + error.Message, "CombatPresentation");
                return false;
            }
            _context = context; _context.InventorySource = AliveSource(context.InventorySource);
            // 必须先安装区域过滤，再激活订阅；否则启用瞬间可能接收其他 additive Scene 的 Boss 初始化。
            if (_bossHud != null) _bossHud.BindWorld(context.WorldScene.handle);
            BindDungeonSource();
            _hudRoot.SetActive(true);
            // 子树先完成 Awake/OnEnable，再安装本 Scope；准心也先订阅原 Core 事件，避免漏掉初始显示事实。
            _actorBinding.Bind(context.Actor);
            _held.BindPresentationSource(_context.InventorySource);
            _held.ConfigureVisibility(context.InventoryController, null, null, _heldCanvas);
            if (_heldCanvas != null) _heldCanvas.enabled = context.InventoryController == null || !context.InventoryController.IsOpen;
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = context.CrosshairVisible });
            // EventBus 不缓存历史事件。HUD 若晚于开战创建，需要在订阅完成后向当前 World 的
            // 活跃 Boss 请求只读完整快照；该请求不会重新开战、扣资源或重播 Phase/VFX。
            EventBus<BossHudReplayRequestedEvent>.Publish(new BossHudReplayRequestedEvent
            { WorldSceneHandle = context.WorldScene.handle });
            return true;
        }

        private void UnbindViews()
        {
            // 原控制器仍拥有其 Pause/Input Reason；先 Close 让它正常释放，不在 Host 直接写 Time.timeScale。
            if (_context.InventoryController != null) _context.InventoryController.Close();
            if (_lease.IsValid) EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = false });
            if (_actorBinding != null) _actorBinding.Bind(null);
            if (_stageHud != null) _stageHud.BindPresentationSource(null);
            if (_held != null)
            {
                _held.BindPresentationSource(null);
                _held.ConfigureVisibility(null, null, null, _heldCanvas);
            }
            // 原 Boss HUD 的 OnDisable 会清 BossId/阶段；隐藏纯子树，不触碰 Scene 的 Guide/Pause/Tab。
            if (_hudRoot != null) _hudRoot.SetActive(false);
            // 准心清理已在旧界面恢复前发布，避免恢复后又被旧 lease 的 false 覆盖。
            _legacy.Restore();
            _context = default;
        }
        private void ClearCurrent()
        { UnbindViews(); _lease = default; }
        private void OnWorldUnloaded(Scene scene)
        { if (_lease.IsValid && _context.WorldScene.handle == scene.handle) ClearCurrent(); }

        private void BindDungeonSource()
        {
            if (_stageHud == null) return;
            DungeonPresentation.TryGet(_context.WorldScene.handle, out var source);
            _stageHud.BindPresentationSource(source);
        }

        private void OnDungeonSourceChanged(DungeonPresentationSourceChangedEvent change)
        {
            // 默认事件代表当前缓存被合法清除；其余事件必须属于绑定的实际World。
            // 不直接信事件携带的引用，重新读缓存可同时处理HUD晚到、重连换源及旧Owner迟到释放。
            if (!_lease.IsValid || change.WorldSceneHandle != 0 && change.WorldSceneHandle != _context.WorldScene.handle) return;
            BindDungeonSource();
        }
    }
}
