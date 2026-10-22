namespace Game.Materials
{
    /// <summary>
    /// 初始化时从 ScriptableObject 复制出的只读值快照。Runtime 查询快照而不是按帧读取 Asset，
    /// 避免 Authoring Data 在 Play Mode 中被意外修改，也让热路径保持零分配。
    /// </summary>
    public readonly struct MaterialDefinitionSnapshot
    {
        public readonly MaterialId Id;
        public readonly MaterialBehaviorKind Behavior;

        public MaterialDefinitionSnapshot(MaterialId id, MaterialBehaviorKind behavior)
        {
            Id = id;
            Behavior = behavior;
        }
    }
}
