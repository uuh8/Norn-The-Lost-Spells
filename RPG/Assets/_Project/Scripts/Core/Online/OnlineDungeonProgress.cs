namespace Game.Core
{
    /// <summary>
    /// 正式副本的有限遭遇阶段。Armed表示空间入口已获得启动资格，Active表示当前区已开战，
    /// StageCleared仅表示本地图清场，不授予掉落、账号资产或跨地图结算权限。数字是Wire合同，只能追加。
    /// </summary>
    public enum OnlineDungeonPhase : byte { Armed = 1, Active = 2, StageCleared = 3 }

    /// <summary>
    /// 一份完整的遭遇进度读数。RunId、DungeonId与Hash由外层认证传输包装校验；此层不认识连接或Schema。
    /// Revision仅在进度实际变化时增加；ServerTick记录导出时的权威逻辑步，不能用Client帧号代替。
    /// EncounterIndexPlusOne从1开始，0是缺省无效值；同一副本内NPC激活与门的具体映射取自共同配置。
    /// </summary>
    public struct OnlineDungeonProgress
    {
        public uint Revision, ServerTick, EncounterIndexPlusOne, RemainingEnemies;
        public OnlineDungeonPhase Phase;
    }
}
