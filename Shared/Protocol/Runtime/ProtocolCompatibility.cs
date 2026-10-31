namespace Game.NetworkProtocol
{
    /// <summary>纯通信边界；不拥有账户、房间或玩家资产。</summary>
    public static class ProtocolCompatibility
    {
        /// <summary>我们的应用协议版本；不是 Google.Protobuf 库版本。</summary>
        // 0 是 proto3 标量缺省值，不能把“未提供版本”误认为合法握手。
        // v2 将 DungeonId 变为入场必需语义。只追加 proto 字段不会阻止旧 Client 忽略它，
        // 因此应用握手和 ContentManifest 必须一起升级，旧 Client/DS 明确拒绝，而非进入错误世界。
        public const uint CurrentVersion = 2;
        /// <summary>整个 HTTP Ping 请求体的最大字节数，不是所有未来消息的统一上限。</summary>
        public const int MaxPingPayloadBytes = 1024;
        /// <summary>Ping 文本的 UTF-8 字节预算；中文和 Emoji 不能按字符个数估算。</summary>
        public const int MaxPingMessageBytes = 256;

        /// <summary>当前只接受相同版本；格式兼容检查不能代替登录认证或玩法校验。</summary>
        public static bool IsSupported(uint version) => version == CurrentVersion;
    }
}
