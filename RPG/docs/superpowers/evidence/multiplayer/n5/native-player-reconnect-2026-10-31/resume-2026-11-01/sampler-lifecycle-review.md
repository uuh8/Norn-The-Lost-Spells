# 2026-11-01 显式 Replica Sampler 生命周期只读 Review

结论：在已确认的正式 `Menu -> Hub -> Online` 路径中，未发现此次两个文件最小改动的确定缺陷或 Native 构建阻塞。未改仓库、未运行 Unity/测试/服务/构建/生成或 Git。

## 实际核对

- `OnlineMaterialReplicationProbe.Install` 的 DS 分支保持 `DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR`：仅 `NORN_N5_MATERIAL_PROBE` 显式开启时建立写夹具；本次 DDOL 只位于 Client helper，未扩大到 DS 或场景 Gameplay world。
- Client 自动入口仍在 `DEVELOPMENT_BUILD && !UNITY_EDITOR` 编译分支，且 helper 首先拒绝 null/empty 输出路径。Release / 普通 Editor 不会自动安装；测试以反射调用相同私有 helper，因此不需放宽生产安装门。
- DDOL 对新建 root 调用，root 没有 parent；随后添加 sampler；持久的是显式只读诊断 root，不会把启动 Scene 的其他 root、Actor 或 World 带入 DDOL。
- 自动安装仅由一次进程启动初始化调用；现有正式路径没有第二个 helper 调用方，因此缺少 singleton guard 不构成当前确定重复实例问题。未来如新增重复安装调用需另设幂等规则；现测未覆盖该扩展情形。
- 测试 Cleanup 只 Destroy 自有 sampler，等待一帧后移除独有临时输出；恢复先前仍加载的 ActiveScene，再卸载自有临时 scenes，使用 IsValid/isLoaded 守卫。P7/P8 原 Scene 清理依然保留。
- 与 `task/FormalDungeonWorldPlayModeTests.cs` 既有快照进行实际文本比较：从 `private IEnumerator CheckMap` 到文件末尾逐字符相同。原 P7 12 NPC / 4 encounter、只激活首组2敌人的断言与 P8 安全区退出后才激活 Boss 的完整断言均保留。

## _world 缓存检查及已纠正的前提

最初根据 `OnlineRoomClient.EnterConnectionScene` 保留 Hub roots / SetActive(false) / Additive 加载，提出 sampler 可能先缓存 Hub SinglePlayer world 后一直不能寻找 Replica。这个论证需要“正式 Hub 中实际存在 ElementWorldRuntime”这一前提，不能只从通用场景加载代码推出；初次称为确定问题证据不足，已向主 Agent 撤回阻塞判断。

随后核查实际资源：

- ElementWorldRuntime 脚本 GUID 为 `70bac533a845ec14b82b33078f2e6ee0`。
- 正式 `Scenes/SinglePlayer/SinglePlayer_Hub.unity` 不含此 GUID；所有第一方 prefab 只有 `Prefabs/World/Online/P7World.prefab`、`P8World.prefab` 含它。
- 第一方 Runtime Scripts 没有 `AddComponent<ElementWorldRuntime>` 或 `AddComponent<ElementWorldExposureSystem>`（后者带 RequireComponent）入口。
- 正式 Home 有一个 ElementWorldRuntime，但 `HomeSceneTransfer.Load` 使用 Single 加载，离开时旧 world 被销毁，不是被保留的 Hub world。
- 正式联机 `OnlineWorldBootstrap.StopWorld` 会 Detach 材料并 Destroy `_worldContent`。采样器使用 UnityEngine.Object 的 `_world == null` 判断，旧 World native 对象销毁后会再次查询，未使用纯 CLR null 运算阻断重绑。

所以当前实际原生路径没有已证的 Hub 缓存锁定。条件局限仍存在：若将来保留一个存活的非 Replica world，或同时存在多个候选 World，`FindFirstObjectByType` 加非空缓存并不保证选择当前 Replica；这种新场景需重新绑定/按模式筛选及对应测试，不能由本次场景卸载测试推导为已覆盖。

## 已保存验证证据与局限

只读解析 `native-player-reconnect-2026-10-31/resume-2026-11-01` 的 XML：

- `sampler-red-results.xml`：2026-11-01 01:55:37–01:55:38 UTC，真实 0 passed / 1 failed，失败测试为启动 Scene 卸载存活。
- `formal-and-sampler-green-results.xml`：01:57:31–01:57:34 UTC，5 passed / 0 failed，包含同一生命周期用例、原 P7/P8 两用例及 null/empty 两默认关闭用例。

这些结果由主 Agent 运行，本 review 没有重新运行。测试证明对象跨旧 Scene 卸载存活、enabled 保留和默认关闭无创建；它没有装配 ReplicaStore，也没有验证跨 Scene 后真正写出新 WorldEpoch 的采样 JSON、DS/native 编译分支或双 Native 实际流程。原生验证仍是后续实际 Gate。

Review 时源码 SHA-256：

- Probe：`A6F43109A6C25E1BECFFD6C80EA0E053F2ECA99AE045B0A60F5BF49B672476C0`
- Test：`3C509ED4ABB523C25481B5A11A586166FDE1013CFE626C7EEF7604FDBAF00DD9`

没有权限阻塞或未完成的审查动作。
