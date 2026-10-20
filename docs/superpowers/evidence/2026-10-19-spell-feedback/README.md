# 2026-10-19 法术研究台与实战一致性修复证据

五项反馈已完成：库排除已安装实例；提供九张新卡的 Icon 清单；恢复 Animator 的 ArrowSpawnTime；Home/法术实验区增加独立研究区域；统一和平试验资格，避免双重牌被跳过而退化成轮流单发。另拒绝耗尽资格带入新练习，取消 V2 未释放的旧来源，并保留 Legacy 点击来源约定。

## 使用与资源

- 正式 Hub 原研究入口保持；家/法术实验区初始落点右侧 `(6,1.5,0)`、`4×4×4` Box 金色框内按 F 打开，Esc 关闭，Tab 仍是原背包。P7/P8 不提供详细研究台。
- 右侧只列散装实例；安装消失、拆卸返回。相同法术的其他实例不被合并删除，另根杖上的安装卡也不列回库。
- 和平试验须持有正数资格（无限卡例外），不扣下一次远征资格；Mana、Emit 次数和材料照常消耗。真正 Raid 仍使用已提交资格。零资格安装卡在出发保存/加载前拒绝。
- 双触发用例使用实际蒸汽球、冰球和有限次双重卡，覆盖 Hub→P7 和 Hub→Home；测试墙用真实 Physics 碰撞验证两颗父弹各自释放子层。Mana不足、Shuffle、已推进牌堆仍可能合法改变下一发。乱序在原推演身份文字中简短提示；背包最大值已标为 Mana上限。
- 九张卡：法力回流、快速施放、快速周转、α 借用、γ 借用、短寿命、终止触发弹、双重施放、增伤。稳定 ID 与实际资源见 `new-spells.json`；完整制作/导入/接线要求见 `Assets/_Project/Docs/2026-10-19 新增法术与Icon制作清单.md`。

## 验证范围

`latest-playmode.json` 按最新有效用例名合并，**137 项通过，0 项未通过**。这是多批次相关回归，不是一次全仓库 PlayMode。覆盖研究台多实例/交换/移除、F/Esc/Tab、权限和区域、正式Hub、实际Home/P7、双触发、Legacy、材料载荷、商店仓库、Portal、Boss及按住施法。`tests/` 保留原始红绿及旧断言纠正记录，没有删除失败历史。

EditMode 扫描 Skills/Run/Character 的128个明确 fixture，完整快照 **951/15**；其中站点测试还依赖旧 E 牌及工具 SessionState，改为验证正式场景的真实Box，最后相关资格/据点9/0。按相同名字覆盖后最新 **952通过/14失败**，见 `editmode-summary.json`；14项失败名字与上版交付完全相同，涉及既有作者UI/分类/旧配置契约。没有为凑绿恢复已被作者移除的组件或布局，也没有将这一结果称为全仓库绿。

快/重杖分别按住真实InputSystem左键30秒，约85/50次，Median间隔约0.35/0.60秒。恢复原动画起手后，间隔包含起手和法杖冷却，不能沿用前版动画脱钩的0.05秒实射指标。细项见 `performance/`。

原生 Profiler 用真实分配阳性对照确认记录有效，40暖机帧的 Idle/资源Tick/Grid Refresh/Zoom Marker 为0B；原始记录压缩保留，`profile-source.json` 提供解压后SHA256。它不等于整帧/Build/Spawn/完整动画路径零GC，也不代表极端多弹性能已封口。本轮未重复512弹压力；上版已知集中生成尖峰仍保留为性能边界。

人工手感/美术验收、Standalone 与目标机发布性能未执行；没有把自动 PlayMode 当作人工体验。

## 保护、备份与定位

- `before-scenes-and-prefab.zip`：本轮修改前 Home Scene、PracticeInventoryWorkspace Prefab 及各自原 `.meta`，来自实际工作区，不是 git HEAD。只改显式V2默认值与设施接线，不改玩家存档。
- `home-layout-preservation.json`：原416个RectTransform均保留，无修改/删除，Scene `.meta` 相同。正式Hub区域数据按作者当前Box检查，详见 `actual-hub-areas.json`，不以旧Station点覆盖作者调整。
- `verified-runtime-source-hashes.json`：457个相关Runtime C#源文件快照。`runtime-files-changed-since-previous-delivery.json` 列出相对上次交付的9个Runtime改动；其余原有脏工作区不回退。
- `screenshots/`：实际研究界面、安装后法术库与正式区域截图，已逐张查看。
- 学习解析沿用 `Assets/_Project/Docs/2026-10-18 法术编程V2开发解析（更新中）.md` 的本轮 Task。计划是 `docs/superpowers/plans/2026-10-19 法术研究台与实战一致性修复计划.md`。

回滚时先退出 Play，只恢复本轮场景/Prefab备份及对应代码补丁，保留 `.meta`；不要对共享脏工作区执行 reset/clean。未创建 worktree、SubAgent、commit，也未发送外部消息。
