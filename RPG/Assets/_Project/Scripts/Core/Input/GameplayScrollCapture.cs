using UnityEngine;
namespace Game.Core
{
    /// <summary>滚轮专项占用；InputAction不会互相吞输入，镜头滚轮消费者需主动检查此契约。</summary>
    public static class GameplayScrollCapture
    {
        private static Object _owner;
        public static bool IsCaptured=>_owner!=null;
        public static void Acquire(Object owner){if(owner!=null)_owner=owner;}
        public static void Release(Object owner){if(_owner==owner)_owner=null;}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void Reset()=>_owner=null;
    }
}
