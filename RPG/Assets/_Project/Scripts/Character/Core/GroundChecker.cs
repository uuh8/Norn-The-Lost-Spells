using UnityEngine;
using Unity.Profiling;

namespace Game.Character
{
    /// <summary>
    /// 独立的地面检测组件，挂在 Player GO 上。
    /// 每帧执行 SphereCast，对外暴露 IsGrounded / GroundNormal / GroundAngle。
    /// 状态机里所有 State 通过这三个属性读地面数据，不直接用 isGrounded。
    /// </summary>
    public class GroundChecker : MonoBehaviour
    {
        // 检测球半径：略小于 CharacterController.radius（0.3）
        // 太大会误检测到侧面的墙，太小退化成 Raycast 的"线"
        [SerializeField] private float _sphereRadius = 0.3f;

        // 从脚底往下最多探这么远，范围内有地面 = 接地
        // 不能太大（会在半空中就判接地），不能太小（会漏检轻微凹凸地面）
        [SerializeField] private float _groundCheckDistance = 0.2f;

        // 只检测这些 Layer 上的碰撞体，LayerMask 在底层是一个 32 位的整数（int），每一bit对应一个 Layer
        // 用 LayerMask 的意义：防止把其他角色、可拾取物等误判为地面
        [SerializeField] private LayerMask _groundLayer;
        private CharacterController _controller;
        private bool _controllerDriven;
        private readonly RaycastHit[] _hits=new RaycastHit[16];
        private static readonly ProfilerMarker Marker=new ProfilerMarker("Norn.Character.GroundProbe");

        // ── 对外只读属性，State 直接读这三个，不允许外部写 ──────────────

        // 是否接地
        public bool IsGrounded { get; private set; }

        // 脚下地面的法线方向（垂直平地时 = Vector3.up）
        // 斜坡时法线会倾斜，M2-3 斜坡滑落靠这个计算滑动方向
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        // 地面法线与世界 Up 的夹角（平地 = 0°，45°斜坡 = 45°）
        // 与 CharacterController.slopeLimit 对比，判断是否超坡
        public float GroundAngle { get; private set; }

        private void Awake()=>_controller=GetComponent<CharacterController>();
        public void UseControllerSampling()=>_controllerDriven=true;
        public void Refresh()=>CheckGround();
        private void Update(){if(!_controllerDriven && Time.timeScale>0)CheckGround();}

        private void CheckGround()
        {
            using var sample=Marker.Auto();
            // Transform原点不一定等于胶囊脚底；center/height和作者缩放才定义真实支撑位置。
            Vector3 foot=Foot();float radius=Radius();
            float lift=_controller!=null?Mathf.Max(.06f,_controller.skinWidth*Mathf.Abs(transform.lossyScale.y)+.02f):.06f;
            Vector3 origin=foot+Vector3.up*(radius+lift);
            int count=Physics.SphereCastNonAlloc(origin,radius,Vector3.down,_hits,_groundCheckDistance+lift,_groundLayer,QueryTriggerInteraction.Ignore);
            float nearest=float.PositiveInfinity;Vector3 normal=Vector3.up;
            for(int i=0;i<count;i++)
            {
                var hit=_hits[i];
                // 球扫可能先碰自身/侧墙；不能让最近的一面墙遮掉真正的脚底支撑。
                if(hit.collider==null||hit.collider.transform.IsChildOf(transform)||hit.normal.y<=.05f||hit.distance>=nearest)continue;
                nearest=hit.distance;normal=hit.normal;
                // SphereCast在薄物体边缘返回球面接触Normal；短竖直Ray获取支撑表面自身的Normal，避免平栏杆误滑坡。
                if(hit.collider.Raycast(new Ray(hit.point+Vector3.up*.08f,Vector3.down),out var surface,.16f)&&surface.normal.y>.05f)normal=surface.normal;
            }
            IsGrounded=nearest<float.PositiveInfinity;GroundNormal=IsGrounded?normal:Vector3.up;
            GroundAngle=IsGrounded?Vector3.Angle(GroundNormal,Vector3.up):0;
        }
        private Vector3 Foot()=>_controller!=null?transform.TransformPoint(_controller.center)-Vector3.up*(_controller.height*Mathf.Abs(transform.lossyScale.y)*.5f):transform.position;
        private float Radius()=>_controller!=null?Mathf.Max(_sphereRadius,_controller.radius-.01f)*Mathf.Max(Mathf.Abs(transform.lossyScale.x),Mathf.Abs(transform.lossyScale.z)):_sphereRadius;

        // 在 Scene 视图中可视化检测范围（只在选中 Player 时显示）
        // 绿色 = 接地，红色 = 悬空
        // 这不影响任何游戏逻辑，只是 Debug 用的辅助线
        private void OnDrawGizmos()
        {
            Gizmos.color = IsGrounded ? Color.green : Color.red;

            Vector3 origin = Foot() + Vector3.up * (Radius()+.1f);

            // 画起点的球（检测起始位置）
            Gizmos.DrawWireSphere(origin, Radius());

            // 画终点的球（检测最远到哪里）
            Gizmos.DrawWireSphere(
                origin + Vector3.down * (_groundCheckDistance+.1f),
                Radius());
        }
    }
}
