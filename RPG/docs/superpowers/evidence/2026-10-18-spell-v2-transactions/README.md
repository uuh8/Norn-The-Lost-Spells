# V2资源事务：当前16槽、无Copy基线

本次实现真实归属表上的V2根事务、已支付子层预算、耗尽后的冻结程序过滤、材料延迟供给、幂等收据窗口和Session共享3条执行归档。通过Editor临时内存库存审查，正式角色仍使用Legacy。用户认可多排格子的视觉方向，尚未人工试玩；Editor Gate1/2均未冒称通过。

- `editmode-results.json`：最终250通过/0失败，列出实际执行的Fixture和用例。
- `playmode-results.json`：最终48通过/0失败，含原Owned施法、附着材料世界链路、格子工作台。
- `code-manifest.json`：本次19个脚本的SHA256，区分本次工作与原有脏工作区。
- `protected-files.json`：相对于多排格子交付时的基线，正式Hub Scene与背包两个脚本保持相同Hash。

RED证据保留于`Temp/SpellProgrammingV2/t4-*.xml`：契约0/3、功能3/13、预约/收据边界22/3、Session23/1、Editor入口0/1。早期测试将合法的16层误期望为TooComplex，已纠正为合法上界验证，没有放大库存容量伪造Owned超限证据。

完整PlayMode初次47/1，单独附着用例14/1返回WorldUnavailable；日志定位旧测试瓶缺WorldModel。只在测试克隆中提供临时Capsule模型，正式SO及运行时前置条件不变；最终原用例及整组48/0。

限制：动态Copy在T8启用，当前合法16槽构筑无法触发Owned的17层/129输出/1024步超限；对应Skills原子拒绝测试已经回归，真实Owned扩展路径仍需后续覆盖。收据保留最近256条，窗口外旧请求拒绝且不会重付；诊断归档最多3条，淘汰不取消实际票据。资源提交不等于世界发射，真实V2发射/动画/据点入口尚未接入。当前Schema仍为13，未升级或改写正式玩家存档；不宣称本次服务热路径或整帧零GC。

审查入口：Unity菜单`Norn → Spell Programming V2 → 打开资源事务 Sandbox`。无需装配Prefab、Animator或Canvas；样例克隆缺Prefab时只在内存中引用现有Arrow Prefab做preflight，窗口不生成世界弹体、不打开或保存Scene、不写Profile。

逐项解释、取舍和Editor操作见同一living explainer的Task4。未创建worktree、未提交Git、未派SubAgent；未回退原有脏改动。
