using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 已成功提交的施法声音。Clip 是本进程的只读资源引用，网络组合层只把它映射成固化的 SoundKey。
    /// 同根命令的 Payload 仍可发出后续声音，SoundOrdinal 防止把它与本机预测的第一声一起去重。
    /// </summary>
    public struct CombatCastSoundEvent : IGameEvent
    {
        public ulong ActorId, CommandId;
        public uint SoundOrdinal;
        public AudioClip Clip;
        public Vector3 Position;
    }
}
