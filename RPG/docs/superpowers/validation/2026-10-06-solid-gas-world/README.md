# 固体与气体基础交付验证

本次范围为真实材料基础、固体几何/碰撞/导航、气体通量/暴露/显示，以及 P7/P8 接线。自动材料转换仍在后续 Phase D，长时/Boss/视觉验收仍未执行。

| 证据 | 结果与边界 |
|---|---|
| `identity-red.xml` | 新身份用例先失败，0 Passed / 2 Failed |
| `assets-red.xml` | 四个生产材料资产创建前失败，6 Passed / 4 Failed；其中几何与 Shader 用例已通过 |
| `official-assets-green.xml` | 官方 Unity EditMode，四材料/Prefab + 两 Shader，6 Passed / 0 Failed |
| `official-gate-log.txt` | 身份/Catalog/路由/查询/旧液体路由的官方批次为 41 Passed / 0 Failed；该轮原始 XML 已被后续 Runner 输出覆盖，保留日志证据，不能假称另存了该 XML |
| `native-checks.txt` | 本轮最终源码的补充检查：直接在 Unity Editor 内调用 26 个同步 NUnit 断言，26 Passed / 0 Failed。保留当前 P7；只对自己打开的 P8 做 Additive 读取与关闭，不运行场景、不通关 |
| `coarse-solid-mask-red.txt` | 复查粗液体格与细冰格的接口，偏心冰被中心采样漏过；新增用例先失败，修复后并入最终原生检查 |
| `setup-report.txt` | 显式安装创建材料/法术/Prefab/Profile，配置 P7/P8；已打开的 P7 仅修改内存，未在安装器中保存其既有修改；P8 由安装器打开、保存并关闭 |
| `source-hashes.json` | 验证时相关源码、Shader、资产、Scene 与已编译 Assembly 的 SHA256 |

原生补充检查不包含官方 TestRunner 的完整 Fixture 生命周期。本次选择的同步 Fixture 均无 SetUp/TearDown/UnityTest；检查覆盖量守恒、负坐标、容量失败、冻结恢复、通风记账、跨 Chunk 表面、导航洞口、角色占据、Collider 创建/移除、呼吸高度、局部支撑失效、资产和场景接线。

暖机纯 Gas Solver 与几何构建检查的 managed GC 增量为 0，空固体 Tick 重建计数保持不变。尚未验证整场景 `GC Alloc=0`、NavMesh 路径实战、Gas GPU Overdraw、相机穿入气团的视觉结果、长时材料累积与 Boss 帧率。不能用这些断言替代实战 FPS。

安装备份见 `Backups/MaterialWorldSolidGas/`，按安装报告里的时间目录定位。当前大量既有液体、火焰、P7/P8 修改属于用户已有工作，不通过 Git reset 或整目录覆盖回滚。
