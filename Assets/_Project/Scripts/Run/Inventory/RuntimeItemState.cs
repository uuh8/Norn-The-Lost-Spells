using System;

namespace Game.Run
{
    /// <summary>
    /// 玩家拥有的某一份资产。保存恢复传入原 ID；新发放才生成 Guid，移动不重建身份。
    /// 构造接收原始恢复数据，归属表统一验证后才承认它是合法资产，避免半恢复进入库存。
    /// </summary>
    public sealed class RuntimeItemState
    {
        public string InstanceId { get; }
        public string ContentId { get; }
        public int RemainingCharges { get; }
        public bool HasUnlimitedCharges { get; }
        // 后续附着/组分模块提供规范化键；这里不把键冒充材料账本，也不在其中保存保护资格。
        public string StackStateKey { get; }
        // 免费来源属于实例，不能仅按名字禁止所有付费同配置装备；现有不可变拷贝和DTO保留此状态键。
        public bool IsStarterEquipment => StackStateKey == StarterKitService.FreeStateKey;
        public ContainerComposition Composition { get; }
        public MaterialItemState Material { get; }
        public SpellAttachmentState Attachment { get; }
        // 模型ContentId只决定外观/模板；随机固定属性随实例保存，不能放进共享SO或UI缓存。
        public WandStats? RolledWandStats { get; }

        public RuntimeItemState(string instanceId, string contentId, int remainingCharges,
            bool hasUnlimitedCharges = false, string stackStateKey = "", ContainerComposition composition = null, MaterialItemState material = null,
            SpellAttachmentState attachment = null, WandStats? rolledWandStats = null)
        {
            InstanceId = instanceId;
            ContentId = contentId;
            RemainingCharges = remainingCharges;
            HasUnlimitedCharges = hasUnlimitedCharges;
            StackStateKey = stackStateKey;
            Composition = composition; Material = material; Attachment = attachment;
            RolledWandStats=rolledWandStats;
        }

        public static RuntimeItemState Create(string contentId, int remainingCharges = 0,
            bool hasUnlimitedCharges = false)
        {
            if (string.IsNullOrWhiteSpace(contentId))
                throw new ArgumentException("新资产需要稳定 ContentId。", nameof(contentId));
            if (remainingCharges < 0)
                throw new ArgumentOutOfRangeException(nameof(remainingCharges));

            // 这是发放/拾取时的离散分配，不在 Update、查询或移动中创建身份字符串。
            return new RuntimeItemState(Guid.NewGuid().ToString("N"), contentId,
                remainingCharges, hasUnlimitedCharges);
        }
    }
}
