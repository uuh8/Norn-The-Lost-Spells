# Replica表现兴趣修复：已验证到Native CPU Draw

依据总计划第25节已定位问题及2026-11-01接续授权，修复仅为Replica显示兴趣跟随真实本地Actor；Origin、Authority/单机、视锥/距离/概率/预算均不改。Physics拒绝恢复继续保持当前字节，不重试。复用既有Core Actor/Camera注册、default同一Bound事件解绑，不新增公开跨模块合同。

实施顺序：先真实Replica EditMode行为RED（绑定前后顺序、移动而Origin不动、Clear/重连、非本地拒绝、Unity假null、禁用/重启、无Actor、其他模式）；再正式P8 Replica PlayMode跨Scene/Owner替换RED；修改现有ElementWorldRuntime及其Replica分部的私有表现缓存/订阅；GREEN、现有相机消费者回归与N5同范围回归；匹配缓存Client/DS构建；干净检查后正常退出Editor，在3GiB守卫下双端Native重测，先验证实际Seed/Upload/Draw CPU，再做同版本计时。不得把CPU Draw当屏幕显示或据P8宣布解释全部四端P7现象。

新增测试文件在既有Tests/ElementField，不建Runtime目录。实际状态由本目录`*-report.json`和progress记录；写测试不等于已运行。测试夹具只克隆内存配置/实例，不保存正式资产或真实存档。Editor/构建/服务均由主Agent统一协调，无提交推送。

## 实施与实际验证（持续更新）

仅改ElementWorldRuntime.cs与现有Replica分部：Replica OnEnable订阅原Core两类通知并补读可信注册，OnDisable/Destroy对称退订；本地Actor/Camera配对、Scene、Unity假null和active保护决定显示距离中心。固定Origin不改，单机/Authority仍走原作者_interestPoint；消费者不Clear全局Owner。新增测试在既有ElementField目录，正式场景测试加入原FormalDungeonWorldPlayModeTests；Core/Rendering/协议/资产未修改。

真实RED为EditMode2通过/8失败（模式对照通过，8Replica均错误返回Origin），正式P8 Scene0/1。首修后兴趣/相机14/0、PlayMode10/0、完整同范围N5472/0。独立审查无P0/P1，有1个P2：失活期间晚订阅把身份缓存清空，恢复active无事件时不能恢复。追加2个行为用例取得0/2 RED，最小分离身份缓存与瞬时active门后16/0、PlayMode10/0；最终完整回归正在运行。审查原报告与处理见code-review.md/review-decision.json，不以重复审查代替测试。

两份Runtime源码Hash已冻结，旧Client/DS ElementField DLL及exe Hash已保存；修复后的匹配构建与Native尚未运行。后续使用已验证的提前聚焦与3GiB正常关窗守卫，不重复此前未聚焦菜单等待错误。Physics保持53cf…72a1c原样，无重试恢复。

## 最终检查点：07:14 UTC

最终完整回归474/0，相关16/0、正式PlayMode10/0；Client06:46:52/DS06:51:12均Succeeded/0 error。以上“尚未运行”保留为历史，本节为最新。

正常退出Editor后native-1因15秒.NET句柄判断超时，原始失败及清理保留；native-2实际Win32观察同一UnityWndClass隐藏而.NET句柄0，按精确身份正常显示/聚焦后完成真实菜单/Hub/P8/单次火球30→29。没有改Runtime、参数或重复构建。07:04:07自有窗口/DS/业务/私有13384均停止。

Native379/290/290事件全部Completed/0溢出，每端34版安装及逐连接ACK；A20/B21个版本有Seed/Upload/Draw CPU链、共同17版，距/视锥拒绝0，247项离线检查通过。34份目标提交指纹与修前一致。每份版本只记首个对应阶段，不把20/21解释成总粒子数。完整原始数据/校准/每版上下界见`native-2/*-timing.json`和`timing-analysis.json`。

| 成功分段 | A中位 / P95 ms | B中位 / P95 ms |
|---|---|---|
| 提交→安装（各34版） | 26.860 / 39.040 | 29.492 / 40.505 |
| 安装→Seed（20/21版） | 33.037 / 83.296 | 33.487 / 83.914 |
| 安装→Draw CPU（20/21版） | 34.090 / 84.079 | 34.038 / 84.474 |

B同版本安装约晚1.226–2.895ms；首次Draw A在R1、B在R2，B首提交晚35.779–35.798ms，明确不是同版本网络延迟。跨进程区间最大宽0.0223ms；Draw是CPU提交，不是GPU/屏幕出现。原概率/80ms发种间隔未改，剩余Reason2是去重示例而非全部拒绝计数。P8双端不能完整归因旧四端P7，也非完整性能/零GC证明。

最低可用14.299GiB，无守卫低线；当前已无自有PID/13384，Editor关闭，约19.38GiB可用。DS2条资源错误/P3/128Key未解决，Physics原样且无恢复重试。summary.json与environment-after.json记录实际状态；下一授权工作为既有容器首数据切片，目前未编码。
