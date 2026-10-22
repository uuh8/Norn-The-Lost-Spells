using System;

namespace Game.ElementField
{
    /// <summary>
    /// 当前场景唯一 Element Write Sink 的轻量注册表。
    ///
    /// Registry 只保存接口引用，不搜索 Scene，也不决定使用固定场还是稀疏世界；具体 Runtime 在
    /// OnEnable/OnDisable 中成对注册和释放。重复 Runtime 被明确拒绝，避免同一 Projectile 随机写入两套数据。
    /// </summary>
    public static class ElementRuntimeRegistry
    {
        public static IElementWriteSink ActiveSink { get; private set; }

        public static bool TryRegister(IElementWriteSink sink)
        {
            if (sink == null)
                throw new ArgumentNullException(nameof(sink));

            if (ActiveSink == null)
            {
                ActiveSink = sink;
                return true;
            }

            // Unity 组件可能经历重复 Enable 通知；同一个所有者重复注册应保持幂等。
            return ReferenceEquals(ActiveSink, sink);
        }

        public static void Unregister(IElementWriteSink sink)
        {
            if (sink != null && ReferenceEquals(ActiveSink, sink))
                ActiveSink = null;
        }

        public static bool TryEnqueueWrite(in ElementWriteRequest request)
        {
            return ActiveSink != null && ActiveSink.TryEnqueueWrite(in request);
        }
    }
}
