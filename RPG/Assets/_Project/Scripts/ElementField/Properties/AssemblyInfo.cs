using System.Runtime.CompilerServices;

// Gameplay 写入口保持 internal，避免 Rendering 等上层模块绕过模拟规则直接改 Cell。
// 只把内部成员开放给专用测试程序集，使测试能够验证所有权边界，而不是为了测试把生产 API 改成 public。
[assembly: InternalsVisibleTo("Game.ElementField.Tests")]
