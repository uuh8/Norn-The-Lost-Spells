namespace Game.ElementField
{
    /// <summary>
    /// 可选的只读表现运动参数。重力来自已初始化Solver或同源可信Profile，不能由相机/Client修改玩法。
    /// Renderer用它重建同一对已提交端点之间的二阶路径，不启动本地模拟、不拥有额外GMU。
    /// 旧只读夹具不实现本能力时仍按零加速度显示，避免隐式强加9.81给无关数据源。
    /// </summary>
    public interface ILiquidPresentationDynamicsSource
    {
        float FallGravity {get;}
        float FallTerminalVelocity {get;}
    }
}
