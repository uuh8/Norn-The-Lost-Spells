# Rapid 长期 Busy 持有者根因修复计划

继续用户明确授权的自动排查和修复，原工作区执行；实际游玩验收仍由用户负责。不提交/推送，不修改 Dev 账号资产，不对用户进程做按名批量操作。

## 已完成及本轮边界

上一轮已经修复地面收纳误套全局 Busy，不能据此声称 Rapid 的旧持有者已结束。本轮只追截图中的 Rapid：四 Damage 位于 0–3、空槽 4、Fire 位于 5；旧 checkpoint 为 ProgramRevision2、120 Mana/0 CD、Fire13、Damage各7，无 Trigger、无 AlwaysCast。背包中的 Termination 不在施法程序内。

旧 checkpoint 不保存活 Ticket/Scope/输出观察、Draw/Cycle 或 Driver Timeline/Queue；旧日志没有持有者诊断。因此必须区分当前可复现缺陷和仍缺失的现场信息，不能把重建 Workspace 冒充精确恢复旧故障。

## 具体执行

1. 源码分流：分别核查 Driver Timeline/Queue、库存 Prepare/材料锁、V2 Ticket 与 OutputReleased 终结。保留已排除的假设，避免重复扩大拾取门。
2. 补真实 Caster 边界：有状态连续释放、正式在局改槽得到 Revision2、低 Mana 零输出、Fire 最后1→0后的纯 Modifier 程序。停手后验证真正的旧物编辑命令成功，不以已豁免的 F 当唯一证据。
3. 补 Native 原 Mouse：从正式业务入局，原 UI 改槽、持键直到实际发生 EmptyOutput，再分别走先松后 Tab、持键 Tab 内松；确认停手后没有新 Intent，采样权威 Busy 原值与新 MoveOwned 收据。复用当前匹配 Client/DS；未改 Runtime 前不重复构建。
4. 一旦取得失败，冻结同 Actor/Epoch/Command 证据并定位尚未结束的具体状态；先保存 RED，再修其生命周期。禁止超时清 Ticket、忽略 Busy 或增加自动补货。
5. 针对修复验证 EditMode/PlayMode，必要时构建并验证 Native；更新中文解析，保留失败、未复现及人工项。没有证据不写唯一归因。

## 责任与文件

- 主 Agent：产品、Character regression、Editor/测试/构建/服务/Git、证据和解析。
- 子 Agent 1：只读复核 Machine/Replay/投递中断。
- 子 Agent 2：仅 `.tools/audit-staging/20261116-rapid-busy/` 中的 Native Program/README 草稿；根审核后应用。不得改 Runtime、操作 Editor 或服务。
- 重点入口：`OnlineCastDriver`、`OwnedWandCastAdapter`、`SpellCaster`、`CastSpawnTransaction`、`WandExecutionTransactionService`、`WandExecutionReplay`、`SpellProgramMachine`。

## 本轮假设与验证门

- 持续按住并不保证 Fire 耗尽：四 Damage 会先消耗 Mana，低蓝时 Fire 被跳过，合法产生 AdvancedWithoutEmission。因此“低蓝空放”和“Fire耗尽”是两个独立控制。
- 零输出图不需要世界输出观察；源码的正常 Complete/Collect 会回收它。若测试通过，只能排除这一正常分支，不能当现场已修复。
- Mana/CD 正常不代表活 Ticket 已结束；也不能从旧 checkpoint 推断用户没有失败。
- 场景/设置沿用 79 文件保护基线；Editor 先 inspect-safety，禁止保存原 DirtyAssets，新增 meta 由 Unity 生成。

## 证据接续与最小修复设计

- Character 真实 Caster 两例（暖执行后改槽、320轮低蓝/零输出；Fire13→0后纯Modifier）均通过，覆盖资源请求收据窗口循环。
- 修前 Native 从最初低蓝样本、20秒延长到两轮各50秒，三次各32 checks；最后一轮分别观察到97/95个低蓝 EmptyOutput，松手8秒后新 MoveOwned 成功。这些是正常分支控制，不能据此说用户旧故障不存在。修后仅再跑一次同样50秒的配套构建检查。
- 明确故障注入已取得两项有效 RED：Rapid和普通杖资源已经 Commit，InventoryChanged 的消费者抛异常，Caster 却丢弃尚未发送的 OutputReleased。八秒后 Mana 已恢复、Fire 已扣1，新的 MoveOwned 仍 Busy。该持有者为已提交但未观察的 Ticket，是否为原截图的实际触发源尚缺历史日志。
- 修复在已确定事实的通知边界：原命令 Publish 保持原语义；已提交 InventoryChanged、施法结果、输出观察和出生/声音事实隔离单个消费者异常，确保后续权威接收者仍收到同一事实。原收据、费用和同一 Projectile 保持，不用重施法、补货或取消票据恢复。
- 隔离投递使用订阅快照缓存；仅订阅变化后重建 InvocationList，正常重复投递不新增每次分配。只记录消费者异常，不能吞掉 Prepare/Commit 本身的错误，也不能把命令未执行解释为成功。

## 收尾范围

用户明确现在不方便复测，修好后由其以后验收。限定收尾为225项EditMode、33项PlayMode、三份匹配Client/DS构建、一次修后Native及默认入口交付；不再要求用户提供现场或扩展排查。第一次DS构建中Editor发生Texture分配OutOfMemoryError，保留失败证据，冷启动Editor并在各构建前回收未引用资产后重试；不修改Runtime、场景内容、系统内存配置或账号资产。
