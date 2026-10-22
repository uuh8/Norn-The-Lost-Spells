namespace Game.Character
{
    /// <summary>
    /// 法术根层的世界空间瞄准方式。显式数据避免按 Projectile 类型猜测行为，
    /// 也让旧 Prefab 缺省为 Direct 时保持既有直线投射语义。
    /// </summary>
    public enum SpellAimMode : byte
    {
        Direct = 0,
        BallisticToPoint = 1
    }
}
