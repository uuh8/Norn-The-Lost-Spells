using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 角色战斗反馈（受击 + 死亡），适用于任何带 HealthComponent 的对象（玩家或敌人）。
    /// 订阅 DamageReceivedEvent / DeathEvent，按 TargetId 过滤出"打到的是不是我"，再做三件表现：
    ///   1. 受击：CrossFade 到受击动画（仅在本次未致死时）；
    ///   2. 闪红：受击瞬间把所有 Renderer 的颜色推向红色，再用 MaterialPropertyBlock 平滑插值回原色（零材质泄漏）；
    ///   3. 死亡：CrossFade 到死亡动画 → 禁用控制/AI（_disableOnDeath）→ 延时销毁。
    /// 动画状态名数据驱动（各角色在 Inspector 填自己 Controller 的节点名，如 GetHit_Bow / Die_Bow），
    /// 与项目其它 CrossFade 进入的状态一样：进入靠代码、退出靠 Animator 连线（受击动画需有回到 Idle 的 Has Exit Time 过渡）。
    /// 经 EventBus 与攻击方解耦——不引用 Game.Character；和近战/箭矢复用同一套伤害事件。
    /// </summary>
    [RequireComponent(typeof(HealthComponent))]
    public class CharacterCombatFeedback : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("角色 Animator；留空则 Awake 自动在子物体上找")]
        [SerializeField] private Animator _animator;

        [Header("受击动画 (CrossFade 进入；须与 Animator 节点名一致，如 GetHit_Bow)")]
        [SerializeField] private string _getHitStateName = "GetHit";
        [SerializeField] private float _getHitCrossFade = 0.05f;

        [Header("死亡 (CrossFade 进入；如 Die_Bow)")]
        [SerializeField] private string _dieStateName = "Die";
        [SerializeField] private float _dieCrossFade = 0.1f;
        [Tooltip("播放死亡动画后多久销毁本对象（秒）；设成略大于死亡动画时长")]
        [SerializeField] private float _destroyDelay = 2f;
        [Tooltip("死亡瞬间禁用的组件（拖入角色控制器 / 输入 / 敌人 AI），防止死后仍能行动")]
        [SerializeField] private Behaviour[] _disableOnDeath;

        [Header("受击闪红")]
        [SerializeField] private Color _flashColor = Color.red;
        [SerializeField] private float _flashDuration = 0.15f;
        [Tooltip("Shader 颜色属性名；URP Lit 为 _BaseColor，旧/部分 Shader 为 _Color")]
        [SerializeField] private string _colorProperty = "_BaseColor";

        [Header("流血 (命中本角色时；可空，不填则不出血——非血肉实体留空即可)")]
        [Tooltip("击中特效：命中瞬间在命中点生成（如 BloodExplosion）")]
        [SerializeField] private GameObject _bloodHitPrefab;
        [SerializeField] private float _bloodHitLifetime = 2f;
        [Tooltip("流血效果：挂在角色身上随其移动、持续一段时间（如 BloodDripping）")]
        [SerializeField] private GameObject _bloodDripPrefab;
        [SerializeField] private float _bloodDripLifetime = 3f;

        // 缓存（Awake 一次，运行时零分配）
        private int _id;                       // 与 HealthComponent 同源：gameObject.GetInstanceID()
        private int _getHitHash;
        private int _dieHash;
        private int _colorPropId;
        private Renderer[] _renderers;          // 本角色所有渲染器（含子物体蒙皮）
        private Color[] _originalColors;        // 各渲染器原始颜色，用于插值回弹
        private MaterialPropertyBlock _mpb;     // 复用一个 MPB，避免每帧 new、且不实例化材质

        private float _flashTimer;              // 闪红剩余时间（> 0 表示正在闪）
        private bool _dead;                     // 已死亡：忽略后续受击反馈

        private void Awake()
        {
            _id = gameObject.GetInstanceID();

            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            _getHitHash = HashOrWarn(_getHitStateName, "受击");
            _dieHash    = HashOrWarn(_dieStateName, "死亡");

            _colorPropId = Shader.PropertyToID(string.IsNullOrEmpty(_colorProperty) ? "_BaseColor" : _colorProperty);
            _mpb = new MaterialPropertyBlock();

            _renderers = GetComponentsInChildren<Renderer>();
            _originalColors = new Color[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                Material shared = _renderers[i].sharedMaterial;
                _originalColors[i] = (shared != null && shared.HasProperty(_colorPropId))
                    ? shared.GetColor(_colorPropId)
                    : Color.white;
            }
        }

        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void Update()
        {
            if (_flashTimer <= 0f) return;

            _flashTimer -= Time.deltaTime;
            // t: 0（刚受击，最红）→ 1（结束，回到原色）
            float t = Mathf.Clamp01(1f - _flashTimer / _flashDuration);
            ApplyTint(t);
            if (_flashTimer <= 0f) RestoreOriginal(); // 收尾，确保精确回到原色
        }

        #region 事件回调

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (e.TargetId != _id || _dead) return;

            StartFlash(); // 闪红始终触发（含 DoT，作为"正在受伤"的轻量反馈）

            // DoT/环境跳伤（燃烧/火场）：到此为止——不放血爆、不播受击动画，避免持续伤害把角色钉在受击姿态/无法移动
            if (!e.TriggerHitReaction) return;

            SpawnBlood(e.HitPoint, e.HitDirection);

            // 致死的那一击不播受击动画，交给死亡动画（OnDeath 同帧随后触发）
            if (e.RemainingHp > 0f && _getHitHash != 0 && _animator != null)
                _animator.CrossFadeInFixedTime(_getHitHash, _getHitCrossFade, 0);
        }

        private void OnDeath(DeathEvent e)
        {
            if (e.TargetId != _id || _dead) return;
            _dead = true;

            // 死亡动画
            // CrossFadeInFixedTime是Unity Animator的API，作用是在固定的时间（秒）内，从当前动画平滑过渡到目标动画。
            if (_dieHash != 0 && _animator != null)
                _animator.CrossFadeInFixedTime(_dieHash, _dieCrossFade, 0);

            // 禁用控制/输入/AI，防止死后仍能行动
            if (_disableOnDeath != null)
            {
                for (int i = 0; i < _disableOnDeath.Length; i++)
                    if (_disableOnDeath[i] != null)
                    {
                        _disableOnDeath[i].enabled = false;
                    }
            }

            // 死亡动画播完后销毁
            Destroy(gameObject, _destroyDelay);
        }

        #endregion

        #region 流血

        /// <summary>
        /// 命中本角色的流血表现：命中点先放击中特效(BloodExplosion)，再在身上挂持续流血(BloodDripping)。
        /// 由 DamageReceivedEvent 驱动，故近战/箭矢/法术任何来源命中都生效；两个 prefab 都可空（非血肉实体不出血）。
        /// 朝向用命中方向 HitDirection（特效 +Z 朝向飞行/挥击方向）。
        /// </summary>
        private void SpawnBlood(Vector3 hitPoint, Vector3 hitDir)
        {
            if (_bloodHitPrefab == null && _bloodDripPrefab == null) return;

            Quaternion rot = hitDir.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(hitDir) : Quaternion.identity;

            if (_bloodHitPrefab != null)
            {
                GameObject hit = Instantiate(_bloodHitPrefab, hitPoint, rot);
                ExcludeOwnerFromParticleCollision(hit);
                Destroy(hit, _bloodHitLifetime);
            }
            if (_bloodDripPrefab != null)
            {
                // 挂到本角色下，随角色移动；位置取命中点
                GameObject drip = Instantiate(_bloodDripPrefab, hitPoint, rot, transform);
                ExcludeOwnerFromParticleCollision(drip);
                Destroy(drip, _bloodDripLifetime);
            }
        }

        /// <summary>
        /// 把本角色所在层从血液粒子的碰撞遮罩(Collision.collidesWith)中剔除：
        /// 血滴/溅射粒子开启了 World 碰撞，若与角色自身碰撞体(同层)相撞，就会"在身上摊开"而非落到地面。
        /// 剔除自身层后，血液穿过自己、只溅射到地面/环境。前提：角色与地面不在同一层(角色应在 Player/Enemy 等专用层)。
        /// </summary>
        private void ExcludeOwnerFromParticleCollision(GameObject bloodFx)
        {
            if (bloodFx == null) return;
            int ownerLayerBit = 1 << gameObject.layer;
            ParticleSystem[] systems = bloodFx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.CollisionModule col = systems[i].collision;
                col.collidesWith &= ~ownerLayerBit; // 移除自身层，血液不再与自己碰撞
            }
        }

        #endregion

        #region 闪红

        private void StartFlash()
        {
            _flashTimer = _flashDuration;
            ApplyTint(0f); // 立即变红，避免首帧延迟
        }

        /// <summary>把所有渲染器的颜色在"红"与各自原色之间插值（t: 0=红, 1=原色），用 MPB 覆盖不改材质。</summary>
        private void ApplyTint(float t)
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                Color c = Color.Lerp(_flashColor, _originalColors[i], t);
                _renderers[i].GetPropertyBlock(_mpb);
                _mpb.SetColor(_colorPropId, c);
                _renderers[i].SetPropertyBlock(_mpb);
            }
        }

        private void RestoreOriginal()
        {
            _flashTimer = 0f;
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                _renderers[i].GetPropertyBlock(_mpb);
                _mpb.SetColor(_colorPropId, _originalColors[i]);
                _renderers[i].SetPropertyBlock(_mpb);
            }
        }

        #endregion

        private int HashOrWarn(string stateName, string label)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                GameLog.Warn($"{label}动画状态名为空，CrossFade 将无法切换动画", "Combat");
                return 0;
            }
            return Animator.StringToHash(stateName);
        }
    }
}
