namespace Game.Combat
{
    /// <summary>
    /// 某个求值时刻的只读元素事实。
    /// Snapshot 让纯规则先完整读取旧状态，再由 Adapter 统一写回结果，避免边判断边修改导致顺序漂移。
    /// </summary>
    public readonly struct ElementStateSnapshot
    {
        // 四个强度均使用项目统一的 [0,100] 百分比语义。
        // Snapshot 不在构造函数中 Clamp，因为它只负责记录事实；公式入口会对危险输入做保护。
        public readonly float Fire;
        public readonly float Water;
        public readonly float Poison;
        public readonly float Goo;

        // 每个通道单独保存来源，因为 Poison 可能来自敌人，而 Fire 可能来自环境。
        // 触发 Toxic Combustion 时，Runtime 才能按规则选择正确的伤害来源。
        public readonly StatusSource FireSource;
        public readonly StatusSource WaterSource;
        public readonly StatusSource PoisonSource;
        public readonly StatusSource GooSource;

        public ElementStateSnapshot(
            float fire,
            float water,
            float poison,
            float goo,
            StatusSource fireSource,
            StatusSource waterSource,
            StatusSource poisonSource,
            StatusSource gooSource)
        {
            // 这里执行的是一次“值复制”：Evaluator 之后只看到这一时刻的数据，
            // 即使 StatusController 随后变化，也不会改变已经进入公式的 Snapshot。
            Fire = fire;
            Water = water;
            Poison = poison;
            Goo = goo;
            FireSource = fireSource;
            WaterSource = waterSource;
            PoisonSource = poisonSource;
            GooSource = gooSource;
        }
    }
}
