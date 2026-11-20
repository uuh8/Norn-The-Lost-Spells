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
        // 事实投递需要逐订阅者隔离异常。只在订阅集合变化后的首次投递取快照，
        // 不在每次施法/输出时分配 GetInvocationList；普通 Publish 的调用路径不变。
        private static Delegate[] _factHandlers;

        // 可选交互没有Presentation接收者时应拒绝申请，避免拿到暂停却无人显示/释放。
        public static bool HasSubscribers => _event != null;

        /// <summary>
        /// 注册事件订阅。必须与 <see cref="Unsubscribe"/> 成对调用，否则造成内存泄漏。
        /// 推荐在 MonoBehaviour.OnEnable 中调用。
        /// </summary>
        public static void Subscribe(Action<T> handler) { _event += handler; _factHandlers = null; }

        /// <summary>
        /// 取消事件订阅。推荐在 MonoBehaviour.OnDisable 中调用。
        /// </summary>
        public static void Unsubscribe(Action<T> handler) { _event -= handler; _factHandlers = null; }

        /// <summary>
        /// 发布事件，同步调用所有当前订阅者。
        /// 热路径零 GC Alloc：T 为 struct，按值传递，JIT 不装箱。
        /// </summary>
        public static void Publish(T evt) => _event?.Invoke(evt);

        /// <summary>
        /// 投递权威已经裁决的结果或已经发生的事实。消费者失败不能撤销原结果，也不能使排在它
        /// 后面的资源 Owner/Adapter 丢失同一事实；每个失败保留事件、接收方法和异常日志。
        /// 仅用于结果/通知，不用于 Prepare/Complete 等命令本身：事务执行错误仍须沿原调用栈失败。
        /// 单次调用持有局部订阅快照，回调中的增删只影响下次投递，顺序及重复订阅与 Publish 一致。
        /// 与原 EventBus 一样由主线程订阅和投递，不新增跨线程保证。
        /// </summary>
        public static void PublishFact(T evt)
        {
            var listeners = _event;
            if (listeners == null) return;
            var snapshot = _factHandlers ?? (_factHandlers = listeners.GetInvocationList());
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { ((Action<T>)snapshot[i])(evt); }
                catch (Exception error)
                {
                    // 日志消费者也可能失败；诊断不能再次截断后续权威接收者。
                    // 不返回“投递成功”给调用者，更不能借通知失败回滚已确定的资源结果。
                    try { GameLog.Warn("已确定事实 " + typeof(T).Name + " 的订阅者 "
                        + snapshot[i].Method.DeclaringType?.FullName + "." + snapshot[i].Method.Name
                        + " 失败；原结果保留并继续投递。\n" + error, "EventBus"); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 移除此事件类型的全部订阅者。换场景时调用，
        /// 防止跨场景的 stale subscriber 持续引用已销毁对象造成内存泄漏。
        /// </summary>
        public static void Clear() { _event = null; _factHandlers = null; }
    }
}
