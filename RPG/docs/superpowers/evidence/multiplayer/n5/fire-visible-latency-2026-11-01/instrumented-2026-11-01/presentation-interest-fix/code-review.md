# N5 Replica 表现兴趣修复审查

结论：发现 1 个 P2；无 P0/P1。此次为只读静态审查，没有运行 Editor、测试、构建或服务，也未修改仓库。仅审查主 Agent 指定的本轮兴趣接线及新增测试，不把工作区此前 N5 修改纳入本轮发现。

## P2：失活期间晚订阅会丢失仍有效的注册，恢复 active 后无法自行恢复兴趣

位置：[ElementWorldRuntime.Replica.cs:75](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.Replica.cs:75>)，同方法 69–78 行；结果由 [getter:29](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.Replica.cs:29>) 固化。

`RefreshReplicaPresentationInterest` 先清空两份缓存，又以 `actor.activeInHierarchy` / `camera.isActiveAndEnabled` 决定是否缓存身份。因此，同 Scene 的真实本地 Actor/Camera 已合法注册，但 Replica 在 Camera 临时失活时首次启用或重新启用，随后仅恢复 Camera 活跃状态，会永远回退到 world Transform：

1. 正常调用既有 Core 两个 Bind，Actor/Camera 一致。
2. `camera.enabled = false`。
3. Replica 的真实 `OnEnable` 触发 Refresh；第 76 行拒绝且缓存保持 null。
4. `camera.enabled = true`，不重新 Bind。Camera 组件 enabled 的变化本身不会发布 Core 两类事件。
5. 每次 `InterestPosition` 都在第 29 行因缓存为 null 回退，无法恢复本地 Actor 中心。

Actor 自身暂时 inactive 期间晚订阅也有同类问题。该判断来自当前代码控制流，未在本审查中执行 Unity 复现。常规 `LocalActorCameraBinding.OnEnable` 会重新 Bind，因此不把这个边界冒称为已发生的 Native 故障；它仍属于本轮要求的失活、晚订阅及重启恢复组合，而且不需要修改 Core 合同或扩展需求。

建议：将可信身份缓存与瞬时活跃状态分开；合法且相互匹配的注册可保留引用，getter 继续执行现有 active、Unity 假 null 和 Scene 检查，使失活时安全回退、恢复后自动跟随。增加上述失活期间晚订阅/重新启用后恢复的行为测试。

## 测试覆盖与 Minor 留档

- [WorldReplicaPresentationInterestTests.cs:83](<F:/Develop Project/RPG/RPG/Assets/_Project/Tests/ElementField/WorldReplicaPresentationInterestTests.cs:83>) 覆盖早/晚绑定、跟随位置、Origin 不动；95 行覆盖 default 同一 Bound 解绑事件、Owner 替换；116 行覆盖伪造事件和注册不匹配不能接管；137 行覆盖停止订阅及重启读取新注册；149 行覆盖空注册和 Actor 单独存在；161 行保留单机/Authority 对照。
- [WorldReplicaPresentationInterestTests.cs:127](<F:/Develop Project/RPG/RPG/Assets/_Project/Tests/ElementField/WorldReplicaPresentationInterestTests.cs:127>) 覆盖 Destroy 后 Unity 假 null，没有覆盖 Camera/Actor 临时 inactive 后恢复；尤其缺少上述 P2 组合。
- [FormalDungeonWorldPlayModeTests.cs:188](<F:/Develop Project/RPG/RPG/Assets/_Project/Tests/RunPlayMode/FormalDungeonWorldPlayModeTests.cs:188>) 通过正式 P8 WorldPrefab 的真实激活覆盖晚订阅、Actor 跨 Scene 后旧世界拒绝跟随、新 Scene 重绑定、旧 Owner 晚清理及消费者不清全局 Actor。测试没有模拟真实网络连接，此限制已在任务定义中明确，不能把它计为新的缺陷或 Native 验收。
- Minor：两个 Bind 的相反到达顺序、同步订阅者内重入替换尚无专门测试。当前实现忽略事件载荷并读取两份最新注册，未发现相应明确错误，不据此扩大必修范围。

## 已核对的边界

- [ElementWorldRuntime.cs:132](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs:132>) 只切换 Replica 的表现 getter，单机/Authority 原引用及回退表达式保留；Origin 未被此次方法改写。
- [ElementWorldRuntime.cs:167](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs:167>) Replica 启用仅订阅表现；OnDisable:190 / OnDestroy:789 均退订自己的 handler 并清自己的缓存，不调用全局 Clear 或改变 Owner。
- [ElementWorldRuntime.Replica.cs:64](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.Replica.cs:64>) 两个事件均只请求重读。Core 先更新静态注册再 Publish，Refresh 自身不 Publish；在所见同步重入路径没有旧载荷覆盖新身份的问题。
- [LocalActorCameraPresentation.cs:29](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Core/Online/LocalActorCameraPresentation.cs:29>) 仍是原 Owner 条件清理并发布 default 同一 Bound 事件；Core 文件不在本轮修改中。TryGet:37 使用 Unity 判空和 Actor 相等校验。
- [OnlineWorldBootstrap.cs:381](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Multiplayer/Movement/OnlineWorldBootstrap.cs:381>) 实际只公布 LocalPlayer，未从远端 Actor 枚举中选择；[GpuFireParcelRenderer.cs:85](<F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs:85>) 既有消费回退会读此 InterestPosition，本轮未改筛选/视锥/概率/预算。
- getter 仅作现有引用检查、Unity 属性读取和 Vector3 返回，未见显式 managed allocation、LINQ、集合扩容或闭包；这是静态观察，不能替代正校准 GC 计量。

测试结果仅引用主 Agent 交接：EditMode RED 2 passed / 8 failed，正式 P8 RED 0/1；修复后兴趣及既有相机消费者 14/0，正式全部 PlayMode 10/0。审查未重新验证这些 XML；完整 N5 回归由主 Agent运行，修复后 Native 尚未验证。不得据此宣布 Seed/Draw、四端或 N5 总验收通过。

本次唯一交付为此报告。没有阻塞的仓库写操作；默认 Windows 沙箱启动失败后，仅将只读检查交由自动审批执行，未尝试恢复此前被拒绝的 Physics 设置。
