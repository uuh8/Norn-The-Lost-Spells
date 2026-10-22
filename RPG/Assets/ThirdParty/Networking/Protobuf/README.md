# Google.Protobuf Unity 运行库

- 版本：3.34.2；程序集版本：3.34.2.0。
- 来源：NuGet 官方 `Google.Protobuf` 包的 `lib/netstandard2.0/Google.Protobuf.dll`。
- 许可证：BSD-3-Clause，原文位于 LICENSE.txt；原始包元数据位于 Google.Protobuf.nuspec。
- 包与 DLL 校验信息在 `Tools/Networking/dependency-lock.json`；DLL 由 Initialize-OnlineTools.ps1 导入，不人工修改。
- Unity 自带 Span/Memory 相关 BCL，不复制另一套 System.Memory。**Unsafe 是单独程序集**：N0-2 隔离测试揭示 ASCII 编码路径会用到它，现补官方 NuGet `System.Runtime.CompilerServices.Unsafe 6.1.2` 的 netstandard2.0 DLL（程序集版本 6.0.0.0），保留许可证与包元数据，摘要在 dependency-lock 中。不能用 Editor 内的可选插件依赖替代 Player 所需的 Runtime。
- 此目录是第三方序列化运行库。我们自己的 Schema / 生成代码属于同级 `Shared/Protocol`，不要在这里编写业务消息。

现有 Unity AI Assistant 包内还包含 Editor 专用的 Google.Protobuf 3.33.2，其 PluginImporter 标记 isOverridable=1。保留包缓存原样，由项目 Runtime 覆盖路径交给 Unity 处理。必须在 Editor 验证菜单中确认实际加载为 3.34.2.0；不能仅根据本目录存在新 DLL 就宣称 Editor 已正确选中。

AI Assistant 同样携带可覆盖的 Editor 专用 Unsafe（程序集版本 6.0.3.0）。本项目 Runtime DLL 版本与其不同，按实际 Editor 重载和验证检查选择结果；不修改 PackageCache，不把隔离测试通过描述为 AI Assistant 功能已经验证。
