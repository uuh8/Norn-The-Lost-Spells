# Norn 共享通信契约

同一份源码有两个编译入口：

- .NET：Game.NetworkProtocol.csproj，普通 Microsoft.NET.Sdk Class Library，netstandard2.1，C# 9。
- Unity：package.json + Runtime/Game.NetworkProtocol.asmdef，经本地 UPM Package 导入。

这不是 ASP.NET Core Web 项目，不需要 Program.cs 或端口，不独立启动。首版只放消息与版本约束，不放 Unity 组件、数据库访问、网络连接或资产权威。

## 文件流向

```text
Protos/common.proto
    → RPG/Tools/Networking/GenerateProtocol.ps1
    → Runtime/Generated/Common.cs
        → 业务服 ProjectReference 编译
        → Unity 本地 UPM + asmdef 编译
```

只修改 .proto 和必要的手写纯 C# 契约；Generated 不手改，不向 Unity 另行复制同名协议 DLL。协议更新后运行生成脚本、-Check 和完整新业务服测试，并在 Unity 验证共享包。

## 四种版本含义

- netstandard2.1：API 兼容范围，不是进程 Runtime。
- Package 0.1.0：本项目共享包的版本。
- ProtocolVersion 1：网络兼容检查使用的应用协议版本；0 是缺省值，应拒绝。
- Google.Protobuf 3.34.2 / protoc 34.2：第三方序列化运行库与生成器版本。

字段编号构成 Wire Contract；新增字段用新编号，删除字段保留 reserved，不能重用编号。当前只有基础消息，账户、房间等 Schema 随对应功能建立，不先铺空项目。

## 输出与依赖

Directory.Build.props 把 bin/obj 放到同级 Server/.artifacts/Protocol，避免生成的 AssemblyInfo 污染 UPM。csproj 只编译 Runtime/**/*.cs。Google.Protobuf 由 NuGet 引用；Unity 的第三方 DLL 在 RPG/Assets/ThirdParty/Networking/Protobuf，版本与摘要由公共工具检查。

Tools/protoc 是固定版本开发工具缓存，不提交、不部署。Runtime 下面 Unity 自动生成的 .meta 属于包源码，应随 Shared 仓库保存，不能手工伪造 GUID。
