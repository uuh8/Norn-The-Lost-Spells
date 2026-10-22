# Norn 项目入口

整个游戏按系统职责组织，开发阶段和版本号不作为正式目录。

| 要找的内容 | 入口 |
|---|---|
| 游戏代码与 Assembly 边界 | `Scripts/`，按九个领域分组 |
| 法术规则、法术物品、法杖参数、程序 | `ScriptableObjects/Spell/` |
| 其他物品与容器 | `ScriptableObjects/Items/` |
| 据点、实验区、副本、初始库存与商店配置 | `ScriptableObjects/World/` |
| InputAction 配置 | `ScriptableObjects/Input/` |
| 正式 Hub / Home | `Scenes/SinglePlayer/` |
| 测试和 Authoring 审阅场景 | `Scenes/Tests/` |
| 编辑器工具 | `Editor/`，按 Spells / Items / World 等领域分组 |
| 自动测试与专用数据 | `Tests/` 与 `Tests/Fixtures/` |
| 详细架构、调试与学习材料 | `Docs/项目架构与目录导览.md` |

新增资源放进所属系统；不要新建 `V3`、`Task27` 或“一次开发一个正式根目录”。物品 ContentId、场景加载路径、`.meta` GUID 是兼容契约，移动目录不能顺手修改它们。

当前玩家流程：主菜单 → 据点 → 携带库存进入副本/实验区 → 返回据点。P7 旧开场法术、临时法术 Session、旧 Palette/Frame 编辑界面已退役；背包负责简洁编辑，研究台负责运行逻辑预览。

资源文件名采用下划线与 PascalCase：`Spell_Emit_Fireball`、`Spell_Modify_DamageUp`、`Item_Spell_Emit_Fireball`、`Wand_ResearchRapid`。存档中的 `norn.v2.*` 是保留的稳定 ID，不是文件命名规范。
