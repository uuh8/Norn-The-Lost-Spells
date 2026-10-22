# L4.28：P8 Boss 场景同步验证（2026-10-04）

P8 已迁移到当前 Grid Liquid 与连续表面。此前共享代码已更新，但只有 P7 的 Scene 接线完成切换，P8 仍使用 GpuPbf。这里补齐 P8 场景配置，不新增玩法或降低画质。

## 验证结果

| 证据 | 结果及意义 |
| --- | --- |
| `l428-p8-grid-red.json` | 迁移前 5 项中 3 通过、2 失败；失败定位 P8 仍为 GpuPbf。P7 Grid 与两场景 Fire 规则当时已通过 |
| `l428-p8-grid-green.json` | 迁移后 6/6；包含 P8 实际 Scene Runtime 的三液体写入、数量、Gameplay 查询与表面采集 |
| `l428-final-concentrated.json` | 最后一轮 30 Fixture、267/267；Failed/Skipped=0、Aborted=false、SceneRestored=true。22:36:26—22:37:20 Asia/Shanghai |
| `p7-grid-cutover-audit.txt` / `p8-grid-cutover-audit.txt` | 最后只读接线审计均 0 失败；P7/P8 Scene 均 Clean |
| `editor-after.json` / `manifest.json` | P7 当前实例的十根对象开关、TimeScale、场景文件 SHA256 保持；P8 审计后活动 Scene 仍为 P7 |
| `scene-block-changes.json` | P8 修改 16 个既有 YAML Block，增加 20 个，删除 0 个；Boss、玩家、真实 Terrain、UI 与 NavMesh 原 Block 不变 |

短测打开 P8 的真实场景和组件，初始化实际世界后分别写入 Water、Poison、Sticky 各 255 GMU，推进三次 Tick。765 GMU 总量保留、待写队列清空；三种材料均能通过真实数量查询读取，连续外壳采集有样本及 Owner。这个测试确认 Scene 接线后的数据链有效，未进行 Boss 战实战、视觉最终验收或 FPS 测量。

集中检查由已运行的 Unity Editor 直接调用 NUnit 方法与 Coroutine，包含真实 GPU Readback 和 CPU Mesh 回归。JSON 是诊断报告，不是官方 Unity Test Runner XML，也不代表全项目测试。

## 同步内容与所有权

P8 保留自己的 World Profile GUID，配置与当前 P7 Grid Profile 对齐。Liquid Profile、Render Profile、规则与 Material 资产共享；P8 自己拥有 LiquidWorldRuntime、连续表面和三材料各自的动态回退／Stable 表面，所有 World/Interest 引用均绑定 P8。

旧 PBF、Streaming、粒子 Gameplay 桥接及旧 Renderer 停用。旧 PBF 的粗 Terrain Box 代理根对象停用，Grid 支撑查询使用真实 TerrainCollider。火焰与反应此前已引用当前共享配置，这轮继续保留统一样式，检查本场景引用。

旧 P8 Repair 工具会在 Domain Reload 时自动套用 PBF 模板，已改为显式菜单／文件请求；Audit 只读，Sync 才迁移。通过 Additive 打开临时 Scene，结束后恢复活动 Scene，只关闭工具打开的 Scene。正常编译不会再次把 P8 改回旧后端。

## 文件与复核

本轮修改 P8 Scene/Profile、`P8ElementWorldSceneRepair.cs`、`GridLiquidP7CutoverAudit.cs`、`P8ElementWorldSceneContractTests.cs`、`run_current_liquid_gate.py`，并更新执行记录、交接与学习文档。`final-source/` 保留代码／配置快照，`manifest.json` 记录 Hash；本轮前的备份在 `Backups/ElementPerformance/L428/`。

重新验证可在 Editor 使用 `Tools/Game/Element/Validate P8 ElementWorld Scene`；集中脚本的默认 Fixture 已包含 P8。只读 Audit 继承的部分消息仍写 P7，报告头部 Scene 和实际传入 Scene 决定检查对象。

P7 未保存、丢弃、重载或重启。P8 Scene 与指定 P8 Profile 已明确保存。共享生产 Runtime／Shader 与上一轮最终版本相同，既有性能边界仍见 [L4.27 索引](../2026-10-04-liquid-capture-spikes/README.md)：本轮 Scene 同步不构成密集物质稳定 60 FPS 的证据。
