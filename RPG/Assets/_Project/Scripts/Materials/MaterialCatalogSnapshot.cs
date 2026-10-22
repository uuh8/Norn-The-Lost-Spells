namespace Game.Materials
{
    /// <summary>
    /// 固定 256 槽直接索引表。MaterialId 的底层是 byte，因此无需 Dictionary Hash；
    /// 初始化后每次 TryGet 都是 O(1)、无装箱、无托管分配。
    /// </summary>
    public sealed class MaterialCatalogSnapshot
    {
        private readonly MaterialDefinitionSnapshot[] _definitionsById;

        internal MaterialCatalogSnapshot(MaterialDefinitionSnapshot[] definitionsById)
        {
            _definitionsById = definitionsById;
        }

        public bool TryGet(MaterialId id, out MaterialDefinitionSnapshot definition)
        {
            definition = _definitionsById[(byte)id];
            return id != MaterialId.Empty && definition.Id == id;
        }
    }
}
