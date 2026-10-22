using Xunit;

// 各Fixture共用一个真实test Schema，且过期测试会推进自己的TimeProvider并扫描全库。
// 禁止不同测试类互相回收租约；并发业务行为仍由具体测试内的Task.WhenAll显式覆盖。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
