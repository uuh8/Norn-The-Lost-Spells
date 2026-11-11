# Owner完整携带投影阶段证据

## 已落盘实现


- Producer不再把所有非装备杖都当卡：使用真实背包、道具栏、备用杖及嵌套卡/容器状态；仅真实Spell实例进入SpellUses，资源状态仍只对应最多3根装备杖。Client复用既有Store/Catalog验证实际形状、父子闭包、容量/材料掩码，再原子替换只读Source。
- `InventorySnapshotCodec`严格校验4096项、精确ulong组分、位置与规范wire；负Slot仅接受规范int32编码，不截断非法MaterialKey。Proto字段、生成源码、递归Reset与unknown fields同步维护，应用协议仍为尚未发布的v5。
- 包体上界20MiB来自保守19,040,256B预算；正式8×8/3×1/32卡等Catalog参数没有扩大。16KiB页、完整FNV1a32校验（非认证）、校验后安装；每连接只保留当前+最新待发两份，Client一份组装缓存。4页/帧轮转，并以KCP实际待发片段数和192片段保守水位暂缓，未提高系统拥塞阈值。
- 只有完整Codec+身份+Run数据/时间版本校验通过才排候选；安装成功后才提交序号。错误高序号、未完成高序号页不能阻塞合法后续；旧epoch、旧身份、旧世界不能回灌。World Detach立即释放所属发送/接收缓存，迟到旧Dispose不会清掉新World。
- `IReliableQueueMetrics`放既有Networking传输目录，避免Multiplayer反向依赖具体Mirror.Transports；新增纯传输/测试脚本在既有Protocol/Tests目录，没有按功能新增Runtime文件夹。Hash刷新只保存指定CombatDefinition，不全局保存未知dirty资源。
- 默认关闭的现有只读诊断扩为完整Items/总数/真实位置/精确组分副本，保留旧卡与消耗字段。Authority不伪造目录容量；Client读取实际已安装Source。Native测试夹具仅可向本轮新建隔离账号显式写入，不是注册赠送或正常获取路径已实现的证据。

### 本轮真实验证（各轮不相加当覆盖）

- Producer/Source RED82/28，Codec RED33/17，Reset RED0/3→GREEN3/0；Producer/Source/Codec GREEN161/0。
- 分页/安装RED6/20；纯分页与Inbox GREEN26/0时，真实Envelope仍RED1；接线后GREEN188/0。
- Review守卫RED10/7；后续时间栅栏RED41/4含3个Unity fake-null断言适配问题，保持资源与订阅断言。一次编译Blocked没有运行测试，旧XML已标stale，不计为测试结果。
- **最终42 fixture并集回归711/0，09:36:12–09:39:35 UTC**，覆盖本轮有界传输、数据/时间版本与生命周期，以及既有N5。
- 新诊断真实RED1/3→整个Driver fixture GREEN17/0；正式P7/P8+共享背包PlayMode **11/0，09:49:36–09:50:06 UTC**。只读诊断最后补充后按影响范围回归，未声称再次全量重跑711。
- **业务Release完整148/0**；原全局ExpireDue测试在私有test Schema无其他Active实例的前提下独立两轮1/0，随后完整集通过。保留最初143/2和144/0历史，未删用户/历史资产、未改断言；测试夹具跨Host历史污染风险仍需区分。
- 广域原剩34逐项分类后，限定复核86case **84/2**：15项目录前置与5项按已有批准合同修正的旧测试已过；Goo期望77实际76未改策略；水面法线断言在补齐输出目录后显露失败，保留原失败，不归因本轮Owner。另12项旧内容断言未复跑/未解决，不能写全仓GREEN。
- Proto官方生成一致性8文件通过。v5匹配Native与Owner逐端验收正在准备，结果以后续checkpoint为准，禁止混用旧v4产物。

证据目录：[`container-owner-projection-2026-11-01`](./)，包括原RED、Blocked说明、最终回归XML、逐项34审计与隔离业务TRX引用。

### 当前边界与下一步

先串行刷新Hash/构建匹配v5菜单Client和真正Dedicated Server，再低负载双端验证正常菜单入场、实际Source归属/组分、背包显示及同进程新Ticket重连不补满。随后接已批准意图/幂等收据与操作闭环；G破瓶/K固体/供料/炼金/新死亡撤离结算不在本段。

**09:09 UTC用户明确延期四人高负载稳定性与性能，状态是延期、未验证，不是通过，当前功能推进不因此阻塞。** 不再开启四图形客户端压测，不降低内存守卫。低负载正确性仍可验，恢复性能验收由用户另行安排。128Key上限、DS两条资源启动错误、P3、P7四端时延与GPU实际屏幕呈现仍留档；N5Complete=false。Physics拒绝恢复保持原样，不重试；不提交/推送。


### 10:15 UTC检查点：首轮Native进入据点失败已定位并最小修复，复验待完成

首组匹配v5完整菜单Client（09:59:01）/真正DS（10:03:44）均构建成功、0 Error。首轮隔离Native A实际登录、点击进入据点后，仍停在主菜单；HTTP Profile200成功但`OnlineAccountClient.Refresh()`保留N1旧`Items.Length > 24`门禁，将665/38项的合法Profile清空，未发布入Hub事件。未到DS/Owner网络通道，不能归因同步性能。原始日志、最后portal状态、输入审计和正常清理保留于`native/first-attempt-failure.json`及同目录；守卫没有因低内存触发，自有Native/业务/MySQL已关闭，用户Editor未动。

按已批准完整Profile预算最小接续：私有纯验证上限对齐Shared MaximumItems+24=4120，Refresh真实调用，保留本次HTTP成功+认证+有效Profile门；不截断、补发或绕过登录。实际RED4/10，修复后账户边界+Driver+Inbox相关回归41/0（10:11:44–10:11:55）。这是新补充运行，不与711/0相加。因运行时代码改变，10:14:35再次成功刷新Hash，并正在必要重建第二组匹配产物；禁止以复跑通过替代首轮失败归因，Native功能仍未通过。


### 2026-11-01T11:00 UTC阶段收束：Owner投影Native 通过，完整容器功能未完成

- 匹配v5完整菜单Client 10:19:39 / 真正Dedicated Server 10:24:20均Succeeded、0 Error，参见`matched-native-pair-profilefix.json`。本轮后续重试只改观察脚本，没有继续重建或修改游戏参数。
- 最终隔离P8双Native连续运行检查点：owner-initial, owner-after-cast, owner-reconnected；A665项、B38项各自与当前DS逐项值/库存版本一致，混样瓶Water20+Poison7=27GMU、工具瓶明确空，只有本端Owner库存可读。实际键鼠走门/准备/开始；没有DS坐标投放或暂停模拟。A同一PID 7292，ConnectionEpoch 1→2，重连后次数29；重新取得新Ticket请求有2次观测。
- 原Tab UI报告`InventoryIsOpen=True`；PIL窗口截图全黑，不能据此验收屏幕显示。此段只接受数据/原UI状态证据，不把CPU状态或截图当GPU呈现。容器正常取得路径也不能由测试SQL夹具证明。
- 消耗验证保留真实值：30→29。此前相同0.12s短按分别扣1和4，原始Native5/6日志保留，原因未知；没有缩短输入或改施法参数去凑单次结果。重连只核对实际已消耗值精确保留，**不代表短按输入正确性通过**。
- 所有失败都保留：Native1为Client遗留24项Profile门禁（已RED4/10→GREEN41/0最小修复）；Native2缺真实Ready确认；Native3在3099910144B可用内存触发原3GiB守卫，两个窗口均正常关闭；Native4观察脚本误读不存在的DS.ActorCharges；Native5漏点原Reconnect按钮（不是已确认自动重连缺陷）；Native6短按次数26而脚本固定等29。Native7将观察假设与游戏合同区分，不改协议/行为。
- Editor于10:37:23在无运行/编译/构建/测试/脏场景且新增标记仅导入字体时按既有授权正常退出；没点保存/丢弃，没强杀，保护Hash保持；可用内存约19GiB后再测。最终自有进程清理Errors=[]，剩余归属进程[]。Editor保持关闭，后续实际开发需要时再打开，不自动执行昨晚关机。

当前Source验证记录按各自版本保留：42fixture711/0之后新增诊断17/0、账户/Driver/Inbox41/0，正式P7/P8与共享背包PlayMode11/0，业务Release完整148/0；不相加、不宣称最后Source再次完整RunAll。Proto一致性8文件、Owner源文件范围diff检查通过；已应用006 Hash不变。原34项20通过、2失败、12未重跑。

下一小段继续已批准的权威意图/幂等收据、共享拾取/背包丢地、选择/采集/倾倒与数量守恒，不重问同一许可；短按扣次差异要作为现有输入行为问题调查，不能混作容器或性能结论。P7最新Native容器、GPU实际显示、完整操作与实际消耗容器的重连仍未测。四人高负载/性能由用户明确延期、未验证；128Key、DS资源错误、P3和P7四端显示时延继续留档，N5Complete=false。Noita不实施、Physics拒绝恢复不重试、不提交推送。


### 11:03 UTC工作环境恢复与协作修正

用户11:01要求恢复Editor。所有本轮Native/DS/业务/MySQL已正常停止后，11:03:28正常启动原工程Unity Editor，PID15628，未启动测试/构建；当前以editor-restoring.json记录实际启动状态。此前临时关闭许可不应被泛化成以后自由关Editor；今后如需关闭用户Editor，先说明并取得本次明确许可，不再无提示沿用旧许可。前文Editor保持关闭仅是测试结束瞬间的历史状态，本条为最新接续。
