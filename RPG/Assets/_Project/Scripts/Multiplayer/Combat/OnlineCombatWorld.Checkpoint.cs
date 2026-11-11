using Game.Core;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public sealed partial class OnlineCombatWorld
    {
        internal bool TryCaptureRunCheckpoint(Shared.InstanceLaunchDescriptor launch, string id, ulong sequence,
            ulong retirementAck, uint tick, Shared.OnlineRunAssetCheckpointWorld material,
            OnlineItemRetirementSnapshot[] retired, ulong retirementSequence, out CapturedRunCheckpoint captured, out string error,
            bool terminal = false)
        {
            captured = null; error = "CheckpointAuthorityFactsUnavailable";
            if (_disposed || !_world.IsAuthority || !_world.CanAdvance || Faulted || _instance == null
                || _instance.Inventory == null || launch == null || _instance.RunId != launch.RunId) return false;
            // 个人退出先排空自己的瓶/供料并可靠保存。全员最终收据齐全后，用户已批准剩余
            // 普通弹体、未拾物/币及材料随世界结束，不等待弹体命中来额外生成奖励，也不分配给玩家。
            if (terminal && !_instance.HasConfirmedAllMemberExits) return false;
            var owners = new OnlineOwnedCheckpointSnapshot[4]; var epochs = new uint[4];
            for (int i = 0; i < _instance.Inventory.MemberCount; i++)
            {
                if (!_instance.Inventory.TryGetFrozenMember(i, out var entry) || entry.ActorId < 1 || entry.ActorId > 4) return false;
                if (_instance.Inventory.IsMemberExited(entry.ActorId)) continue;
                int slotIndex = (int)entry.ActorId - 1; var slot = _world.Registry[slotIndex];
                epochs[slotIndex] = slot.Epoch;
                // epoch0只表示从未创建角色；有历史epoch却没Workspace不能回灌Entry。
                if (slot.Epoch != 0 && (slot.Cast == null || !slot.Cast.TryCaptureCheckpoint(out owners[slotIndex]))) return false;
            }
            // 个人最终收据已提交的Owner不再有工作区；其损耗只读历史缓存，不能丢弃或回灌Entry。
            // Instance负责Receipt/Exited门与深拷贝。缺缓存返回-1，保留现场，不能以空数组伪装已知0损耗。
            var exitedBuffer = new OnlineExitedMemberLossSnapshot[Shared.OnlineRunAssetCheckpoint.MaximumMembers];
            int exitedCount = _instance.CopyExitedMemberLossesTo(exitedBuffer);
            if (exitedCount < 0 || exitedCount > exitedBuffer.Length) return false;
            var exitedLosses = System.Array.Empty<OnlineExitedMemberLossSnapshot>();
            if (exitedCount != 0)
            {
                exitedLosses = new OnlineExitedMemberLossSnapshot[exitedCount];
                System.Array.Copy(exitedBuffer, exitedLosses, exitedCount);
            }
            var ground = _groundItems != null ? _groundItems.Capture() : new OnlineGroundInventorySnapshot { Version = 1 };
            // Ground只发活动币；可靠检查点另读原身份完整墓碑，不能重连后忘掉赢家再造币。
            var coins = System.Array.Empty<OnlineCoinCheckpointRecord>();
            if (_groundItems != null)
            {
                var buffer = new OnlineCoinCheckpointRecord[Shared.OnlineRunAssetCheckpoint.MaximumCoins];
                int count = _groundItems.CopyCoinCheckpointsTo(buffer);
                if (count < 0 || count > buffer.Length) return false;
                if (count != 0) { coins = new OnlineCoinCheckpointRecord[count]; System.Array.Copy(buffer, coins, count); }
            }
            return RunAssetCheckpointCapture.TryBuild(new RunCheckpointCaptureInput
            {
                Launch = launch, EntryInventory = _instance.Inventory, CheckpointId = id, Sequence = sequence,
                LastAcknowledgedRetirement = retirementAck, RetirementSequence = retirementSequence, ServerTick = tick,
                Owners = owners, ConnectionEpochs = epochs, Ground = ground, World = material, Coins = coins,
                ExitedLosses = exitedLosses,
                TerminalCapture = terminal,
                Retirements = retired, ReadPhysicalPose = ReadCheckpointFlightPose
            }, out captured, out error);
        }
        private RunCheckpointFlightPoseStatus ReadCheckpointFlightPose(ulong actor, string inventory,
            string flight, string content, out RunCheckpointPhysicalPose pose)
        {
            pose = default;
            for (int i = 0; i < _outputs.Length; i++)
            {
                var output = _outputs[i];
                if (output == null || output.FlightId != flight) continue;
                if (output.Actor != actor || output.InventoryId != inventory || output.ContainerContentId != content)
                    return RunCheckpointFlightPoseStatus.Inconsistent;
                if (output.Terminated || output.Root == null || output.Body == null) return RunCheckpointFlightPoseStatus.Missing;
                pose = new RunCheckpointPhysicalPose { Position = output.Root.transform.position,
                    Rotation = output.Root.transform.rotation, Velocity = output.Body.linearVelocity };
                return RunCheckpointFlightPoseStatus.Available;
            }
            // 碰撞后代理可能已结束，但Run仍持有等待落量的Flight；聚合器显式保留该分支，不造假姿态。
            return RunCheckpointFlightPoseStatus.Missing;
        }
    }
}
