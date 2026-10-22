# N0-2 委托 GUI 验收与 Server 启动对照（2026-10-21）

用户明确答复“你自行验收”。本轮实际执行可见 Client 的连接和退出按钮，随后收紧 Server 的两个表现启动边界。没有新增项目/Assembly 或升级框架。

## 验收结果

- `gui-gate.json`：可见 GUI 验收通过；Client 未使用自动连接探针参数。A 经按钮连接，截图 Replies=17；B 在 A 退出前 Replies=66，A 经按钮退出且进程消失后，B 增长到 226 / 272。B 经按钮退出后 Server 仍存活，最后清理本轮 Server。
- `gui-actions.txt` / `gui-owned-processes.json`：动作时间与严格登记的进程身份；不会按进程名结束用户其他程序。
- 截图只采纳 `gui-gate.json` 中列出的游戏界面。此前一次捕获混入其他窗口、后续焦点失败均未当作成功证据。普通鼠标投递不稳定时改用同一已登记窗口的 OS 鼠标消息，仍经过真实 IMGUI 按钮。
- `final/server-build.json` / `final/client-build.json`：本轮最终两个原工程构建均 Succeeded / Errors=0。
- `final/multiprocess-report.json` 与日志：最新产物 A=6 / B=14，A 退出后 B 持续收包，两 Client 退出后 Server 仍存活；本轮进程全部清理。

## 单变量启动对照

| 输入 | 启动方法数 | App UI 方法数 | 黑幕方法数 | Built-in Resource 报错数 |
|---|---:|---:|---:|---:|
| 原始构建 | 57 | 3 | 1 | 2 |
| 仅 Server 追加 APP_UI_EDITOR_ONLY | 54 | 0 | 1 | 2 |
| 再移除 Server 黑幕入口 | 53 | 0 | 0 | 2 |

原始数据见 `startup-comparison.json`、`startup-baseline.json`、`app-ui-only/`、`no-fade/`。两次边界调整确实生效，但没有消除原 native 报错，不能将它们归为错误直接根因。2 条资源初始化错误继续作为 N0 未解决项。

`final/server-startup.json` / `final/client-startup.json` 表明 Server 不含上述表现入口，Client 仍保留黑幕安装入口。`extraScriptingDefines` 仅用于本次 Server 编译，未写入全局 Player Settings；46 个场景、Build Scene List、ProjectSettings.asset 的基线 Hash 均未改变。

## 边界

本轮已完成委托 GUI Gate，不再要求用户重复执行这次验收；它不等于 N0 总体通过。未执行完整单机过场/视觉流程或 Profiler，未重新运行旧 EditMode/业务测试，也未实现 Headless Grid、认证后 Snapshot、账户、MySQL、Linux 或远端安全。旧证据不覆盖回写。
