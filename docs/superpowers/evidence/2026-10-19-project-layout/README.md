# 当前整理证据入口

先读 [验收与保留边界](验收与保留边界.md)。

- `moves.json`：保留对象的原路径、最终路径与 GUID；累计退役清单。
- `hub-retirement.json`：本阶段 32 个退役对象及 SHA256。
- `final-identity-validation.json` / `hub-identity-validation.json`：当前 GUID、94 个 ID、配置与正式布局核验。
- `hub-latest-playmode.json`：190 个唯一玩家用例的最新结果与对应批次；原始 XML 保留。
- `hub-all-editmode-summary.json`：九模块 2100 项结果和 19 项失败；主组基线比较另见 `hub-editmode-baseline-comparison.json`。
- `hub-content-pass-1/2.json`：内容工具重复维护、309 份配置无变化。
- `hub-final-editor-state.json` / `hub-dependency-comparison.json`：正式 Hub 交接、构建依赖变化与无测试夹具依赖。
- `脚本变更索引.md`：逐脚本的路径、职责与退役说明。
- `before-layout.zip` / `before-hub-consolidation.zip`：不同阶段的原始工作区快照；不能直接解压回 Assets。
- `migration-workbench.zip`：一次性 patch 与准备脚本，仅供审计，不是日常生成工具。

`layout-*`、`first-stage-*`、早期 red/retry XML/JSON 是历史执行记录。当前结果不以旧数字覆盖，也不把失败日志删除后宣称从未失败。`run-editor-tests.ps1` 与 `verify-current-layout.py` 是保留的明确核验入口。
