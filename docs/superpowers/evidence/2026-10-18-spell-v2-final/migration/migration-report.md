# 法杖V2只读迁移报告

来源为隔离样例，不读取或改写玩家正式存档。Schema13 → Schema14；源实例ID、Mana上限、Regen、Cooldown、容量和BaseDraws保持，Reload默认取旧Cooldown，Spread=0，Shuffle=false。

- sandbox.wand: Mana=100, Regen=0, Cast=0.15, Reload=0.15, Slots=7, Draws=1, Spread=0, Shuffle=False
