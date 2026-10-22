namespace Game.NetworkProtocol
{
    /// <summary>生成类的手写 partial 扩展：供消息池清除上次请求，生成文件本身不改。</summary>
    public sealed partial class ProtocolPingRequest
    {
        /// <summary>归还消息时清理所有字段，尤其 MergeFrom 保留的未知字段。</summary>
        public void ResetForReuse()
        {
            ProtocolVersion = 0;
            Sequence = 0;
            Message = string.Empty;
            // 与固定 protoc 的生成字段名耦合；升级编译器或扩展 Schema 时必须检查并回归。
            _unknownFields = null;
        }
    }

    /// <summary>回复独立重置；不能只重置请求却让服务端回显残留上一条数据。</summary>
    public sealed partial class ProtocolPingReply
    {
        public void ResetForReuse()
        {
            ProtocolVersion = 0;
            Sequence = 0;
            Message = string.Empty;
            _unknownFields = null;
        }
    }
}
