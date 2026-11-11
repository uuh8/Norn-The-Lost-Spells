using Game.Character;
using Game.Core;
using Game.Networking;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineCombatWorld
    {
        private bool ConfigureGroundCoins(OnlineGroundItemAuthority ground, out string error)
        {
            error = null;
            var dungeon = _world.DungeonDefinition;
            // 只接原P7来源。P8与历史无来源Lab不创建Coin世界，也不赠送金币或借用P7表。
            if (dungeon == null || dungeon.LootTableCount == 0) return true;
            if (!dungeon.TryValidateCoinSource(out error)) return false;
            return ground.ConfigureCoins(_instance != null ? _instance.RunId : null, dungeon.CoinPrefab,
                CanAdvanceCoins, ResolveAuthenticatedCoinActor, out error);
        }

        private bool CanAdvanceCoins() => !_disposed && _world.IsAuthority && _world.CanAdvance && !Faulted;

        private ulong ResolveAuthenticatedCoinActor(Transform touched)
        {
            if (!CanAdvanceCoins() || touched == null || touched.gameObject.scene != _world.gameObject.scene || _instance == null) return 0;
            for (int i = 0; i < OnlineActorLimits.PlayerCount; i++)
            {
                var slot = _world.Registry[i];
                if (!slot.Targetable || slot.Player == null || slot.Cast == null || !touched.IsChildOf(slot.Player.transform)) continue;
                var connection = _manager.ActorConnection(i);
                var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
                if (identity == null || identity.ActorId != (ulong)i + 1 || identity.ConnectionEpoch != slot.Epoch
                    || identity.RunId != _instance.RunId || !_manager.IsCurrentActorConnection(connection, identity)) return 0;
                // 个人退出Fence在Workspace.TryCreditCoins再次验证；意外断线无认证连接，因此不能继续自动拾取。
                return identity.ActorId;
            }
            return 0;
        }
    }
}
