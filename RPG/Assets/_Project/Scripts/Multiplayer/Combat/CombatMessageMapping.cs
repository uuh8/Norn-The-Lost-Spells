using Game.Core;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>只映射按键时刻的意图值；Actor 身份来自认证连接，伤害/资源/Prefab 由服务器其他层裁决。</summary>
    public static class CombatMessageMapping
    {
        public static OnlineCastIntent Read(CombatCastIntentMessage message)
        {
            // Protobuf 解码对象会复用；先复制 Vector3/数值，调用者就不会在下一包到来后失去点击快照。
            return new OnlineCastIntent
            {
                CommandId = message.CommandId,
                ConnectionEpoch = message.ConnectionEpoch,
                ProgramRevision = message.ProgramRevision,
                WandInstanceId = message.WandInstanceId,
                ClaimedOrigin = new Vector3(message.OriginX, message.OriginY, message.OriginZ),
                AimPoint = new Vector3(message.AimX, message.AimY, message.AimZ)
            };
        }

        public static void Write(in OnlineCastIntent intent, ulong gameplayHash, CombatCastIntentMessage message)
        {
            // Reset 也清 UnknownFieldSet；只覆盖已知字段会把上次解析的权限夹带字段重新发出去。
            message.ResetForReuse();
            message.ProtocolVersion = ProtocolCompatibility.CurrentVersion;
            message.GameplayHash = gameplayHash;
            message.CommandId = intent.CommandId;
            message.ConnectionEpoch = intent.ConnectionEpoch;
            message.ProgramRevision = intent.ProgramRevision;
            message.WandInstanceId = intent.WandInstanceId ?? "";
            message.OriginX = intent.ClaimedOrigin.x;
            message.OriginY = intent.ClaimedOrigin.y;
            message.OriginZ = intent.ClaimedOrigin.z;
            message.AimX = intent.AimPoint.x;
            message.AimY = intent.AimPoint.y;
            message.AimZ = intent.AimPoint.z;
        }
    }
}
