// a) 静态泛型类而非 Dictionary<Type, Delegate>：
//    CLR 为每个 T 生成独立类型实例，静态字段天然按 T 分区，
//    Publish 时无字典查找、无装箱——O(1) 直接调用。
//
// b) Subscribe/Unsubscribe 必须成对：
//    _event 持有对订阅者委托的强引用，若只 Subscribe 不 Unsubscribe，
//    subscriber 对象永远不会被 GC 回收，造成内存泄漏。
//    推荐在 MonoBehaviour.OnEnable 订阅，OnDisable 取消。
//
// c) Publish 热路径零 GC Alloc 的原因：
//    T 约束为 struct，Invoke(T evt) 按值传递；JIT 为每个具体 T
//    生成独立本地代码，不经过 object 装箱；Action<T>.Invoke 本身不分配堆内存。

using System;

namespace Game.Core
{
    public static class EventBus<T> where T : struct, IGameEvent
    {
        private static event Action<T> _event;

        // 可选交互没有Presentation接收者时应拒绝申请，避免拿到暂停却无人显示/释放。
        public static bool HasSubscribers => _event != null;

        /// <summary>
        /// 注册事件订阅。必须与 <see cref="Unsubscribe"/> 成对调用，否则造成内存泄漏。
        /// 推荐在 MonoBehaviour.OnEnable 中调用。
        /// </summary>
        public static void Subscribe(Action<T> handler) => _event += handler;

        /// <summary>
        /// 取消事件订阅。推荐在 MonoBehaviour.OnDisable 中调用。
        /// </summary>
        public static void Unsubscribe(Action<T> handler) => _event -= handler;

        /// <summary>
        /// 发布事件，同步调用所有当前订阅者。
        /// 热路径零 GC Alloc：T 为 struct，按值传递，JIT 不装箱。
        /// </summary>
        public static void Publish(T evt) => _event?.Invoke(evt);

        /// <summary>
        /// 移除此事件类型的全部订阅者。换场景时调用，
        /// 防止跨场景的 stale subscriber 持续引用已销毁对象造成内存泄漏。
        /// </summary>
        public static void Clear() => _event = null;
    }
}
