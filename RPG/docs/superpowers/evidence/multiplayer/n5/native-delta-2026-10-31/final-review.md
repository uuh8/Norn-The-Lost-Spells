# N5 Native 增量最小差异只读审查

审查日期：2026-10-31。以 `native-delta-2026-10-31/*.cs.before.txt` 为阶段基准，读取两层 AGENTS、阶段计划、三个现有文件及其调用方、Native 编排/判定脚本和已归档测试报告。未修改仓库，未运行 Editor、测试、构建、服务或 Native 程序。

在限定源码差异中未发现确定的生产行为缺陷。以下是可确定的证据限制和尚未完成项，不能据此称整个 N5 已验收。

1. **聚合 ACK 不能证明逐端最终 ACK 已由 DS 接收。** `Run-N5DeltaNative.ps1:45` 仅要求总 `MaterialAcks > 0`；`check-native-delta.py:35` 仅要求两检查点间总数增加。`RoomInstanceBootstrap.cs:141` 的来源是全 Coordinator 的 `Acknowledged` 累计值，没有连接/Chunk 归属。因此可以报告“两客户端各自实际安装 Delta、DS 聚合安装 ACK 增加”，不能报告“两个连接的最终 Delta ACK 均已逐连接确认”。主控已明确接受此范围限制；不需要为本段增加生产诊断或重建。

2. **当前 Native 判定未核对新增 SnapshotId/ServerTick 的精确一致性。** `check-native-delta.py:16` 的比较集合不含这两项；`:43` 仅断言 SnapshotId 为正，未读取 ServerTick。新增字段的真实来源由源码及新增 EditMode 用例覆盖，但原生检查脚本本身不能证明它们逐字段匹配 DS 的对应提交 Header。若继续承诺这两项精确确认，可只在 task 目录 Python 中针对 `actual` 的 Delta 比较同 Key 的最终 DS `SnapshotId/ServerTick`，无需改变 Unity 源码或构建；不要将这两项强制应用于 Baseline，因为 Owner 的 `TryCopyChunk` 刻意使用当前 CommitSequence/最近 WorldTick，可能不同于 Chunk 最后变化时的 Header。

3. **本审查尚未见 Native 最终结果。** 读取时 `build-progress.json` 是 Building（build-room-client），`native-progress.json` 是 WaitingForBuilds。只有后续实际归档的两检查点和一致性结果才能证明本轮 Native 通过；本审查未代替运行。

4. **构建中的保护清单有一项暂态差异，需结束后核对恢复。** 实读 SHA256：派生正式资源 13/13 与阶段起始一致；保护文件 11/12 一致，包含正式 Scene、Build Scene List 和列出的单机存档。唯一不同是 `RPG/ProjectSettings/DynamicsManager.asset`，当前 `53CF96C365B85EEFE78B3CDA76AF21F68246744277AE6CA21FFD6A67BDB72A1C`，起始 `B7C9A9DC08B98990109D4346F68745E15E238DE400F96C583E53E1C83C743A78`。核对发生在构建期间，可能属于既有构建 scope 的临时配置，不能据此认定永久资产回归；由主控在构建完成后验证恢复。

已核实的支撑证据：

- Coordinator 本段仅给 `DescribeChunks`/值型 Stamp 添加 BaseRevision、SnapshotId、ServerTick，分别来自 Owner 已提交 Header 或 Assembler 已安装 Header；没有新 Schema、Interest、复制机制或 Store 写入口。Assembler 在真实 Installer 成功后才发布 Header。
- 探针安装条件仍是 `DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR`，且需要非空 `NORN_N5_MATERIAL_PROBE`；仅当第二变量 `NORN_N5_MATERIAL_SEQUENCE=delta` 时启用第二阶段。未设置环境变量时不创建对象；默认单次夹具流程保留。
- 第一阶段等待实际 WorldTick 至少增长 5 后暂停；等待请求时每 0.25 秒轮询，不重复投放。消费并删除一次性请求后只入队一次正式 ElementWriteRequest；接受后立即返回，恢复原材料时钟，累计至少 5 个材料 Tick 后暂停并禁用探针。未直接改 Store、Revision 或客户端缓存；不经过玩家库存/长期资产。
- 中文新增注释说明双开关、一次性请求、暂停检查点与真实材料时钟的边界。两阶段有限检查点不能外推持续反应、压力、性能、恢复或完整玩家路径。
- 解析归档 XML：RED 14 passed / 1 failed，唯一新增用例因缺少实际安装的 BaseRevision 失败；GREEN 15/0；原范围回归 308/0。新增用例使用真实 Owner→Coordinator→Assembler→Store 传输与安装，断言实际数量及 Delta Header 字段。这里只读核验既有报告，没有重新运行测试。
- Native 判定确实要求两个端各有 `BaseRevision >= 第一检查点Revision` 且 `Revision > BaseRevision` 的已安装变化 Chunk，结合相同 Generation、变化 Checksum 和匹配最终 DS 内容，能够区分真实 Delta 与仅 Revision 增长/新 Baseline。

没有动作被审批拒绝或存在不确定执行结果。默认沙箱无法创建进程，限定只读命令改用允许的 require_escalated 后完成；唯一写入是本审查报告。
