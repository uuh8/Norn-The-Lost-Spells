# 法术研究台：多排格子交付证据

Unity 6000.3.16f1，独立UGUI Sandbox。界面通过相同卡牌格子、分组、触发线和高亮呈现逻辑，未挂计算侧栏、Scope/Output编号或牌堆调试文本。

![七卡与增伤范围](grid-seven-icons-native.png)

## 运行入口

Unity菜单：`Norn > Spell Programming V2 > 打开独立 UGUI Sandbox`，然后Play。此Scene用于Editor Gate1，不修改正式Hub/背包，也没有提前接入Owned库存或实际世界投射物。

点5号增伤，命中子排4、6号同时高亮；标题可折叠。播放/步进观察实际读取顺序；拖动画布或Scrollbar平移；Slider缩放，“适应全部”观察完整构筑。右侧只有法术库格子，底部无原参考图的四个业务按钮。

## 已验证的范围

- `editmode-results.json`：149通过/0失败，含69项Legacy回归、冻结投影、跳过、缩放/收缩/Resize与UI选择。
- `playmode-results.json`：5通过/0失败，真实UGUI指针事件、缩放后EventSystem.RaycastAll、拖拽/双向滚动和最后一格可达。
- `grid-complex-fit-native.png`：16层Trigger、128输出、129指令完整全图；缩到全图时文字很小，放大后可逐卡查看。
- `grid-complex-last-card-native.png`：放大/平移到最末卡且能准确点击，不通过裁剪掉内容适配Viewport。
- `grid-1280x960-native.png`：真实4:3 Game View；主图为1920×1080。临时尺寸已清理，原选择已恢复，见`game-view-restore.json`。
- `warm-allocation.json`：预热后的逐步Seek/Zoom同步方法0 B托管分配；Refresh ProfilerRecorder有效且有样本。不表示字体/冷构建/整帧/实际连发零分配。
- `protected-files.json`：正式Hub、背包和Skills Programming共16项Hash未变；正式Spell资产前后均无Git修改项。

`workbench-wrap-native.png`与`workbench-eight-native.png`分别是连续牌堆Wrap和八卡Scope隔离样例。图中使用现有项目Sprite，未生成新美术素材。

本目录保存真实Game View截图及精简结果；完整NUnit XML在本机`Temp/SpellProgrammingV2/grid-delivery-*.xml`。测试不替代人工判断是否容易理解；阶段一Editor Gate1仍待开发者体验。
