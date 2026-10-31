namespace Game.Character
{
    /// <summary>
    /// 遭遇规则只读查询NPC的存活与当前激活状态。网络Registry可以实现本接口，Character便无需反向引用
    /// Multiplayer；修改激活状态仍由组合根显式传入的回调执行，接口不授予物品、连接或任意对象写权限。
    /// </summary>
    public interface IOnlineDungeonNpcReadOnly
    {
        bool TryGetNpcState(ulong actorId, out bool alive, out bool active);
    }
}
