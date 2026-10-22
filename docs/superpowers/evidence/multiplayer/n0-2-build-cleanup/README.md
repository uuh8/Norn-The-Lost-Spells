# N0-2 构建错误收尾证据（2026-10-21）

本轮仅修复现有第三方 URP 模板重复关键字，增加 Editor 指定资产诊断，重新构建并复测本地通信。没有创建新 .NET 项目、升级框架或实现正式玩法。

| 证据 | 含义 |
|---|---|
| `previous-server-build.json` / `previous-shader-error.txt` | 保留此前 Succeeded / Errors=1 的历史记录，不将新结果回填覆盖历史 |
| `before-reimport-server-build.json` | 本轮未改源码的首次增量构建 Errors=0；说明不能仅凭增量构建判断导入问题消失 |
| `shader-import-red.json` / `shader-import-red-command.json` | 强制重新导入旧源码，实际重复关键字错误 1，HasErrors=true |
| `shader-import-green.json` / `shader-import-green-command.json` | 补丁后同一入口错误 0，HasErrors=false；2 条弃用 Warning 保留 |
| `server-build.json` / `client-build.json` 及 command 报告 | 原工程 Unity 6000.3.16f1，真正 Dedicated Server / Client，两次均 Succeeded / Errors=0 |
| `multiprocess-report.json`、三个探针 JSON 与日志 | 本轮新构建；A=6、B=14 次回复，Server 在两客户端退出后存活，B 结束前最近回复间隔约 0.31 秒 |
| `owned-processes.json` | 仅登记并清理由本轮创建的 Server/Client PID，不结束用户已有进程 |
| `source-before.json` / `source-after.json` | Shader、Editor 工具等输入指纹；本轮检查的 46 个场景与全局 Build Scene List 未改变 |
| `probe-harness-first-attempt.json` / `probe-harness-first-server.log` | 首轮临时验收脚本 UTC DateTime 重复解析导致 Ready 超时误报；Server 实际 Ready，进程均已清理；修正脚本后重跑通过 |

Shader 修改只有 Forward / GBuffer 两处旧 pragma 块；保留当前 URP 17.3 的完整关键字集合。未修改 Material 属性、顶点/片元计算或 Shader GUID。

## 未通过与未覆盖

- 用户尚未完成两个可见客户端的按钮与退出行为验收，N0-2 开发者 Gate 仍待确认。
- Server 启动日志仍有两条既有 `Built-in Resource Error`，旧 `n0-2/project-probe-server.log` 也存在。调用源未确认，本轮 Ping 可用不证明其无害；记录为 N0 Headless 兼容待查，未删 Package 或过滤日志。
- Shader Import 有两条弃用 Warning；0 Errors 不表示 0 Warnings，也不表示 Server 运行日志零错误文本。
- 未重跑 Unity EditMode、业务测试、完整单机路径、视觉与 Profiler；未实现 Headless Grid、认证后 Snapshot、MySQL、Linux 或远端安全验收。
