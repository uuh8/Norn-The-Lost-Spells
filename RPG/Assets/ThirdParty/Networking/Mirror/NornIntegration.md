# Norn 中的 Mirror 来源与边界

固定版本：96.11.3。来源为 [Mirror 官方 Release](https://github.com/MirrorNetworking/Mirror/releases/tag/v96.11.3) 的 `Mirror-96.11.3.unitypackage`。

安装包 SHA-256：`b78356116bbff4013949e42900bd49c556195afe2511a41a8c766986d8f3581d`。

本次保留上游源码、依赖、许可证和原始 `.meta`，仅排除 Examples/Tests；第一方代码在 Game.Networking / Game.Multiplayer，不修改上游 Transport 或 Weaver。路径由原 `Assets/Mirror` 整体安排到 `Assets/ThirdParty/Networking/Mirror`，不重新生成上游 GUID。

手动获取时，浏览器打开 Release，展开 Assets，下载 unitypackage；Unity 选择 `Assets → Import Package → Custom Package`，选择文件，在导入面板排除 Examples/Tests。通过 Unity Project 窗口整体移动到约定目录，保持 `.meta` 与资源一起移动。不要在已有当前版本时再重复导入到旧路径。

KcpTransport 来自 Mirror 包，使用 UDP 传输；当前本地实验只开放回环连接准入。Mirror 内置消息头/握手仍是 Mirror 协议，我们的 Envelope Payload 才是 Protobuf，不声称所有网络字节都是 Protobuf。

此文件为第一方来源说明，其余文件按上游许可证维护。接入和验收详见唯一联机系统开发解析。

Unity 6 的 ILPostProcessor 只提供直接引用列表，Mirror Weaver 在检查 Transport 类型时还需要解析其间接依赖 `Mirror.BouncyCastle.Cryptography`。启用 Override References 的第一方 Networking/Test asmdef 显式列出这个上游 DLL，避免 resolver 警告；这只是满足 Weaver 的程序集解析，并不表示本地实验已启用 EncryptionTransport。没有修改上游 DLL 或 resolver。

首次被 Unity 导入时，上游工具会将自己的脚本模板移动到 Unity 要求的 `Assets/ScriptTemplates`，并追加 MIRROR/版本宏；随包包含的 Hosting Editor 也会追加 EDGEGAP_PLUGIN_SERVERS。这些是插件自动集成副作用，已记录在项目设置和学习文档，没有开通或调用任何云服务器账户。
