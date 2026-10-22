namespace Game.Combat
{
    /// <summary>
    /// 纯函数伤害计算管道：相同 DamageRequest 必定得到相同 DamageResult，
    /// 不读取场景、不修改生命值、也不发送事件，因此可以脱离 PlayMode 在 EditMode 单测。
    /// 当前三种伤害类型都按基础值 passthrough；`DefenseProfile` 仅作为未来减伤系统的数据契约保留。
    /// </summary>
    public static class DamagePipeline
    {
        /// <summary>
        /// 把攻击快照解析为最终伤害。参数以 in 只读引用传递，Pipeline 无权修改输入。
        /// </summary>
        public static DamageResult Resolve(in DamageRequest req)
        {
            float final;
            bool mitigated = false;

            switch (req.Type)
            {
                case DamageType.True:
                    final = req.BaseAmount; // 无视防御
                    break;
                case DamageType.Physical:
                    final = req.BaseAmount;
                    break;
                case DamageType.Magical:
                    final = req.BaseAmount;
                    break;
                default:
                    final = req.BaseAmount;
                    break;
            }

            // 在统一出口做非负钳制，保证任何上游配置或未来减伤公式都不会让“伤害”反向回血。
            if (final < 0f) final = 0f;
            // DamageResult 同样是值快照；真正扣血由 HealthComponent 在纯计算之外完成。
            return new DamageResult(final, req.Type, mitigated);
        }
    }
}
