namespace Game.NetworkProtocol
{
    /// <summary>纯通信边界；不拥有账户、房间或玩家资产。</summary>
    public static class ProtocolCompatibility
    {
        /// <summary>我们的应用协议版本；不是 Google.Protobuf 库版本。</summary>
        // 0 是 proto3 标量缺省值，不能把“未提供版本”误认为合法握手。
        // v2 将 DungeonId 变为入场必需语义。只追加 proto 字段不会阻止旧 Client 忽略它，
        // 因此应用握手和 ContentManifest 必须一起升级，旧 Client/DS 明确拒绝，而非进入错误世界。
        // v3要求认证后的独立世界身份握手；旧v2不能忽略WorldEpoch后直接激活材料世界。
        // v4要求每连接每Chunk独立订阅代次及Enter/Leave生命周期；旧v3会忽略新字段、
        // 把退出后迟到页当作当前材料，故不能只靠proto追加字段实现安全兼容。
        // 升级的是应用合同，不升级Protobuf/Mirror依赖；配套Manifest与Client/DS必须一起更新。
        // v5 的 Owned Schema2 保留真实包内布局与容器组分；旧端忽略组分会把有内容瓶当空瓶。
        // 身份清单同步升级为受控规则清单，Client/DS/业务服必须匹配，不混用 v4 构建。
        // v6要求工具选择由DS确认并随Owner快照同步；旧v5会忽略工具模式而继续使用选杖状态。
        // v7要求共享地面容器/首拾归属与数量一致；旧v6会忽略Ground和Drop/Pickup，不能混用旧产物。
        // v8接通容器G及其受控模型/等待事实；旧端不认识新命令，必须在认证前拒绝混用。
        // v9要求真实固体状态和K意图；旧端会忽略数量，必须拒绝混用，不能补模板量兼容。
        // v10接通卡片的权威供料附件与真实背包瓶；旧端不能忽略附件设置或把空来源补满。
        // v11接通通用库存移动/装备/丢地/拾取、地面完整实例与杖内子卡，且保留保底包可信绑定。
        // 旧v10既不认识新命令，也不能安全安装空手/备用杖资源，必须在入场前拒绝混用。
        // v12要求Owned3真实金币与独立修订；旧端可能展示默认0，不能与新余额源混用。
        // v13增加唯一Coin地面事实和可靠个人退出；旧端会忽略新字段，不能混入同一房间。
        // v14显式区分主动退出/最终死亡，并支持受信单人入场；旧端不能忽略终态原因或混用人数合同。
        // 研究会话快照、成功返回门姿态及F退出意图必须由同版Client/DS解释，禁止混接旧14程序。
        // v16补齐DS裁决的毒爆表现事实，旧端不认识Kind12，须配套构建与Manifest。
        public const uint CurrentVersion = 16;
        /// <summary>整个 HTTP Ping 请求体的最大字节数，不是所有未来消息的统一上限。</summary>
        public const int MaxPingPayloadBytes = 1024;
        /// <summary>Ping 文本的 UTF-8 字节预算；中文和 Emoji 不能按字符个数估算。</summary>
        public const int MaxPingMessageBytes = 256;

        /// <summary>当前只接受相同版本；格式兼容检查不能代替登录认证或玩法校验。</summary>
        public static bool IsSupported(uint version) => version == CurrentVersion;
    }
}
