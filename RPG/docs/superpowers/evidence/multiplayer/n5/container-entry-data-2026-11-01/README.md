# 容器首段：真实携带与组分入局数据切片（2026-11-01）

本轮依据总计划第24节04:28用户“可以”的实施授权，只交付线上真实携带闭包、组分、入局冻结、Shared/Core映射及DS工作区导入。**数据切片已实现并通过相关验证；容器完整线上玩法尚不可用，N5未完成。**

## 实际修改与目录职责

| 既有位置 | 本轮职责与不变量 |
|---|---|
| `Shared/Protocol/Runtime/Business/OnlineOwnedAssetContracts.cs`、`OnlineContracts.cs` | Owned Schema2、真实XY/旋转、显式容器状态与ulong GMU。缺State/缺Components仍未知，Schema1的明确空数组才是空瓶；不从模板补量。 |
| `Shared/Protocol/Runtime/Business/ContentManifest.cs`、Unity `Editor/Networking/ContentManifestExporter.cs` | 清单Schema2导出真实Catalog空间/用途/容量/mask/程序容量，Hash覆盖规则和稀疏占格；不靠ContentId猜规则，不改SO。 |
| `Core/Online/OnlineOwnedAssetContracts.cs`、`Multiplayer/Combat/OnlineOwnedAssetMapping.cs` | Run中立合同避免反向依赖Shared；保留非法非默认值供门禁拒绝，深复制状态/数组，JsonUtility只归一化全默认空壳。 |
| `Server/.../Inventory/InventoryService.cs`、`OwnedCombatAssetRules.cs` | 同RR视图读Grid/State，真实携带父链含备用杖安装卡，排除仓库及后代；占格旋转/空洞/碰撞、归属、数量及上限校验。旧平面Backpack可读但Freeze明确拒绝，旧Move拒Area=NULL真实布局。 |
| `Server/.../Migrations/006_container_inventory.sql`、`Infrastructure/SchemaMigrator.cs` | 新布局与显式State/组分表，无赠送/资产迁移DML。001–005未改；006应用后冻结Hash。执行器在未应用006前核验旧CHECK完整分组，专用迁移连接允许session变量，不增加DB权限。 |
| Unity `Run/Spells/OnlineOwnedWorkspaceFactory.cs`、`Multiplayer/Runtime/OnlineInventorySession.cs` | 复用真实Store占格/父链，独立恢复混样/空瓶，选中杖仍须装备；当前合同不能表达固体物品GMU，明确拒绝Kind.Material而不制造无数量壳。 |

预算为Owned4096项、WholeProfile4120项。备用杖也可携安装卡，99/171不足；预算不改现有背包格数，不承诺未来更大包或无限探索。代码与测试均复用现有领域目录；新增后端测试位于原Tests目录，没有逐功能新建Runtime文件夹，没有新增Unity脚本/.meta。

## 实际测试与失败证据

- JSON合同RED0/2→GREEN2/0；规则清单RED6/3→GREEN9/0，两组合计11/0。
- 后端携带规则RED25/5→GREEN30/0；真实Profile/Freeze/旧Move集成RED1/3→GREEN9/0。
- 006隔离迁移先缺资源失败，再暴露旧Slot CHECK及元数据转义问题；修复后1/0。首轮失败全部保留。实际只对私有`norn_online_test`应用006，001–005摘要保持；006 SHA256为`770ab11b00f07c2fa97b3dc647b579a1815bd1e5180f5d5770a89b5f07941957`，应用后未改字节。严格分组防护在执行器修复，未重写已应用迁移。
- 独立审查的Material壳、缺Components、旧CHECK分组问题均先复现，再做最小修复；JsonUtility实测保留缺字段且精确读取`9007199254740993UL`。
- **最终40个fixture联合EditMode615/0**（08:16:47–08:18:37 UTC），覆盖数据切片、32个旧正常Owned种子的版本修正、库存Codec、既有N5基线与火兴趣修复。拒绝旧版本等断言未降低。
- **正式P7/P8与共享库存PlayMode11/0**（08:19:54–08:20:26 UTC）。这是Editor验证，不是容器真实Native玩家路径。
- **业务Release全量首次143/2**：一个v4旧兼容fixture已修为v5并保留1–4拒绝；另一个`RoomInstanceTests.OldServerEpoch_CannotRenewOrAdmit_AndLeaseExpiryReleasesAssets`断言全局ExpireDue计数1却返回2，保留历史实例污染问题，未删数据/改断言。明确排除此case的其余**144/0**；不能写成完整业务全绿。
- **跨项目广域EditMode首次2989/66**（392个明确筛选名、3055实际case，不代表全部可发现测试）：32项旧正常Owned种子已修并在615/0中回归；其余34项涉及16个缺`Temp/ElementPerformance`目录的case及18个现有资源/反应/反射断言。目录已补足，余34项没有重跑，不宣称历史问题已修。逐项名称见`all-editmode-failures-by-fixture.json`，完整失败XML保留。全量观察脚本12分钟超时后，Unity继续自然运行至08:07:53；未取消/重跑有效测试。
- `GenerateProtocol.ps1 -Check`通过8个生成文件。本轮无新Proto字段，因此未手改Generated/Reset。应用协议为**v5**，受控清单Hash为`de0ca9a275e6ac947dbe6e4531b5a7f62e7e3efe3d1466c271bb51f9351a4273`。

## 当前阻断与下一步（已有授权，不重复询问）

下一小段首先接Owner表现/Codec。`Run/Spells/OnlineOwnedWorkspace.cs:71–80`仍限制99项且仅接受装备杖/卡，带真实包/容器会捕获失败；`Multiplayer/Combat/OnlineCombatWorld.cs:163–167`继而置`Faulted=true`停止副本。这是已核实阻断，不能把Factory导入成功当作正式入场成功。先扩展有界Owner投影、容器状态编码/Reset/客户端验证，完整安装后更新UI，再接已批准权威意图/幂等收据、共享首拾、背包丢地与原选择/采集/倾倒。不添加G/K/供料/炼金或死亡撤离结算。

**本轮未构建v5 Native Client/DS，现成v4产物不可混用；Gameplay运行时Hash尚待后续接线完成后一并刷新。** Owner链打通后再做一次匹配构建及真实玩家/重连/数量不补满验收，避免用已知会Faulted的中间版本做假验收。注册仍只给原两件战斗资产，没有赠送背包/容器；集成测试资产仅由隔离fixture写入，不等于正常玩家获取链已打通。

既存128 Key上限、DS两条Built-in Resource错误、P3、P7四端火显示时延、四人持续/压力/性能及公网Gate继续保留。Physics曾被自动审批拒绝恢复，本轮没有重试或换路，文件Hash仍`53cf96c365b85eefe78b3cda76af21f68246744277ae6ca21ffd6a67bdb72a1c`。无提交/推送、无无关回滚、无强杀用户进程。Noita候选仅记录于总计划第26节，尚不实施。
