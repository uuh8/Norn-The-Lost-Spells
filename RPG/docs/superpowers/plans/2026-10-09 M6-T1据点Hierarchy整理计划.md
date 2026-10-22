# M6-T1 据点 Hierarchy 整理计划

用户已要求参照 P7 整理正式据点，方便自行配置界面。本轮是 Editor 作者组织调整：复用现有对象、组件、序列化引用和 Canvas，不新增玩法、库存副本、UI 写入口或输入键。原工作区执行，不使用 SubAgent、worktree、自动提交，不保存用户当前 Scene。

## 1. 结果与取舍

根节点按 `__Systems`、`__Gameplay`、`__Level`、`__Cameras`、`__HubInteractions`、`__UI` 分组；真实材料世界保留独立根节点并明确命名 `__ElementWorld`。`__Gameplay` 只能包含 WizardPlayer：生产出发链会搬走玩家的 `transform.root`，混入据点组件会把它们一起带走。材料世界有离开销毁、返回重建的独立生命周期，不能与玩家共根。

`__UI` 下直接列出背包/法杖/仓库 Canvas、商店 Canvas、法杖 HUD、容器 HUD、交互提示 Canvas。界面区域使用中文用途后缀；已由 Runtime 按名称查找的 `Program`、`Available`、`Tools` 等节点保持原名。背包与仓库共用真实库存和工作区，通过打开模式展示不同区域，不复制两套可写界面。保持各 Canvas 的原顺序、Render Mode、sortingOrder、Scaler 和 RectTransform；不合并 Canvas，避免改变绘制、缩放和 Raycast 行为。

仅移动已知据点根对象，保留未知用户对象；所有新父对象为激活的单位 Transform。保留世界位置、旋转、缩放、UI 世界四角、组件引用、activeSelf；不重新执行地图装配，不重置用户布局。Editor 整理可以重复调用，不重复创建分组。

## 2. 文件与责任

- 新增 `Assets/_Project/Editor/SinglePlayerHubHierarchySetup.cs`：Editor-only 分组、易读命名、整组 Undo、引用/位置审计、独立审阅副本和请求入口。提供选择各主要区域的菜单。
- 更新原正式据点装配末尾调用整理；更新 P7 对齐、空间布点、反馈装配工具的根节点查找，使已分组场景仍可处理，避免创建重复标牌。反馈 UI 使用已有序列化区域引用，兼容可读命名。
- 更新既有据点 EditMode / PlayMode 测试的根对象假设，练习门测试也指向本轮审阅副本；验证真实传送、返回资产、Tab/E 和 UI 拖拽没有因分组失效。
- 学习文档、验收记录和试玩清单追加本轮结果与配置入口。新增据点 Hierarchy 配置指南，说明组件用途、界面入口、Runtime 动态节点和当前模式重排限制。

不新增模型、贴图、字体、VFX 或其他美术依赖；继续使用已有占位资源。

## 3. 验证与应用

1. 记录当前 Scene、dirty 状态及正式磁盘 hash。只在非 Play、编译完成后接受作者请求。
2. 从正式磁盘创建 `SinglePlayer_Hub_HierarchyReview.unity` 独立副本，整理前后逐项检查原组件引用、Transform 世界状态、RectTransform 世界四角、Canvas 顺序和数量；再执行一次检查幂等性。
3. 使用现有官方 TestRunner：据点配置 EditMode；正式据点操作与 P7/P8 实际接触/回程 PlayMode。测试使用内存档和隔离 Input Runtime，不改正式库存。
4. 在正式当前场景以一个 Undo 组应用同样整理，留 dirty 由用户保存；审计前后磁盘 hash 不变，清除测试副本路由。用户可 Ctrl+Z 整组撤销。
5. 不声称已完成人工配置、用户试玩、Standalone 或 Profiler；Editor-only 整理没有新增 Runtime 热路径。

## 4. 排错与回滚

风险首先是 `transform.root` 影响场景搬迁，其次是父层级影响坐标、Canvas 次序、Runtime 字符串路径与旧工具根对象查找。先检查具体调用链，再保持玩家/材料世界生命周期隔离、单位父对象与原 Canvas 次序；不以更换玩家 Controller 或删除旧对象解决层级问题。工具异常时回滚本次 Undo 组，保留原场景和资产。已知区域使用组件引用定位；未知对象不处理。
