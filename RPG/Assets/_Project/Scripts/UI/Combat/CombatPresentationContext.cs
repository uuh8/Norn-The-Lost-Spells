using Game.Run;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// 一次战斗区域的临时只读接线。这里保存引用而不拥有 Scene/角色/Session，
    /// Host 退出区域就必须清空它，不能把引用对象 reparent 到 DontDestroyOnLoad 根。
    /// </summary>
    public struct CombatPresentationContext
    {
        public GameObject Actor;
        public Scene WorldScene; // 实际玩家所在的战斗 Scene；普通 Raid 的背包控制器可能仍留在 Hub。
        public IInventoryPresentationSource InventorySource; // 可暂为空，表示等待可信来源，不回落到单机档。
        public SinglePlayerInventoryController InventoryController; // 只复用原面板开闭/输入释放，不接管其 Session 写权限。
        public bool CrosshairVisible;
    }

    /// <summary>Owner 加代次构成释放凭证；同一对象重新进入区域也会得到新 Generation。</summary>
    public readonly struct CombatPresentationLease
    {
        public int OwnerId { get; }
        public ulong Generation { get; }
        public bool IsValid => OwnerId != 0 && Generation != 0;
        internal CombatPresentationLease(int ownerId, ulong generation)
        { OwnerId = ownerId; Generation = generation; }
    }
}
