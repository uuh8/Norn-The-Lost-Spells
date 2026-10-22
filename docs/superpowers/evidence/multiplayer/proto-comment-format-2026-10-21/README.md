# 2026-10-21 基础指南与协议排版验证

本次范围：新增独立基础指南；调整 `Shared/Protocol/Protos/common.proto` 的分区、注释和排版；使用固定 protoc 34.2 重新生成 `Runtime/Generated/Common.cs`。消息名称、字段类型、编号、枚举值及协议版本不变。

| 检查 | 结果与含义 |
|---|---|
| Schema Descriptor 对比 | 修改前后 SHA-256 均为 `3b12c3670d89efb7d1391e9ce5f4fb27a1747e20471609e5c721999a34f024d6`；未包含 SourceCodeInfo，比较协议定义而非源码位置与注释 |
| 生成可重复性 | `GenerateProtocol` 的检查模式通过，现有生成源码与固定生成器输出一致 |
| 业务测试 | Release / net10.0，10 通过、0 失败、0 跳过；原始结果见同目录 `proto-comment-format.trx` |
| Schema 排版 | 20 个字段/枚举值均在分号后附中文注释 |
| 新指南静态检查 | 6 个本地 Markdown 链接目标存在，代码围栏配对，无终端操作教程 |

未重新执行 Unity Editor 测试、Player 构建、KCP 多进程实验或 Profiler。此前 N0-1/N0-2 记录保留其当时的文件指纹与验证边界；不要把此次生成源码的新指纹回填为历史构建的输入。
