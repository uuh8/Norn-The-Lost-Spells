using System.Runtime.CompilerServices;

// 测试能检查事务内部不变量；生产模块仍只暴露只读接口和意图命令。
[assembly: InternalsVisibleTo("Game.Run.Tests")]
[assembly: InternalsVisibleTo("Game.Run.PlayMode.Tests")]
