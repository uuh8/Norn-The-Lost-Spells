using System;
using Game.Core;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    /// <summary>完整HTTP账号Profile到Core只读值；不从CombatAssets筛掉仓库，也不读取任何本地档。</summary>
    internal static class OnlineAccountInventoryMapping
    {
        internal static bool TryMap(Shared.OnlineProfileReply profile, string manifestHash,
            out OnlineAccountInventorySnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (profile == null || string.IsNullOrWhiteSpace(profile.PlayerId) || profile.Version == 0 || profile.Gold < 0
                || profile.ContentManifestHash != manifestHash || profile.Items == null
                || profile.Items.Length > OnlineAccountInventorySnapshot.MaximumItems
                || profile.WorldItems == null || profile.WorldItems.Length > OnlineAccountInventorySnapshot.MaximumItems)
            { error = "账号Profile身份、版本、内容或完整数组非法。"; return false; }
            var items = new Shared.OnlineOwnedItemSnapshot[profile.Items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var item = profile.Items[i];
                if (item == null) { error = "账号Profile含空物品项。"; return false; }
                items[i] = new Shared.OnlineOwnedItemSnapshot { InstanceId = item.InstanceId, ContentId = item.ContentId,
                    Revision = item.Revision, ParentInstanceId = item.ParentInstanceId, LocationKind = item.LocationKind,
                    Slot = item.Slot, X = item.X, Y = item.Y, QuarterTurns = item.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    EntryBoundBackpack = item.EntryBoundBackpack, UseTemplateWandStats = item.UseTemplateWandStats,
                    WandStats = item.WandStats, ContainerState = item.ContainerState,
                    MaterialState = item.MaterialState, AttachmentState = item.AttachmentState };
            }
            // 复用原手写null壳规范化与深拷贝；wrapper只搬Items，不授予Run身份或执行资格。
            var copied = OnlineOwnedAssetMapping.ToCore(new Shared.OnlineOwnedAssetSnapshot { Items = items });
            var world = new OnlineAccountWorldItemSnapshot[profile.WorldItems.Length];
            for (int i = 0; i < world.Length; i++)
            {
                var record = profile.WorldItems[i];
                if (record == null || record.Pose == null) { error = "账号地面记录缺失姿态。"; return false; }
                var pose = ToCorePose(record.Pose);
                if (!pose.IsValid()) { error = "账号地面记录姿态非法。"; return false; }
                world[i] = new OnlineAccountWorldItemSnapshot { InstanceId = record.InstanceId, WorldOwnerId = record.WorldOwnerId, Pose = pose };
            }
            snapshot = new OnlineAccountInventorySnapshot { SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion,
                PlayerId = profile.PlayerId, Version = profile.Version, Gold = profile.Gold,
                ContentManifestHash = profile.ContentManifestHash, AssetsLocked = profile.AssetsLocked,
                ActiveRunId = profile.ActiveRunId ?? string.Empty,
                SelectedWandInstanceId = profile.CombatAssets?.SelectedWandInstanceId ?? string.Empty, Items = copied.Items, WorldItems = world };
            return true;
        }

        internal static OnlineAccountWorldItemPose ToCorePose(Shared.OnlineHubWorldItemPose pose)
            => pose == null ? null : new OnlineAccountWorldItemPose { SceneKey = pose.SceneKey, WorldSceneKey = pose.WorldSceneKey,
                PositionX = pose.PositionX, PositionY = pose.PositionY, PositionZ = pose.PositionZ,
                RotationX = pose.RotationX, RotationY = pose.RotationY, RotationZ = pose.RotationZ, RotationW = pose.RotationW,
                PoseKind = pose.PoseKind, HasVisualScaleOverride = pose.HasVisualScaleOverride,
                VisualScaleX = pose.VisualScaleX, VisualScaleY = pose.VisualScaleY, VisualScaleZ = pose.VisualScaleZ };
        internal static Shared.OnlineHubWorldItemPose ToSharedPose(OnlineAccountWorldItemPose pose)
            => pose == null ? null : new Shared.OnlineHubWorldItemPose { SceneKey = pose.SceneKey, WorldSceneKey = pose.WorldSceneKey,
                PositionX = pose.PositionX, PositionY = pose.PositionY, PositionZ = pose.PositionZ,
                RotationX = pose.RotationX, RotationY = pose.RotationY, RotationZ = pose.RotationZ, RotationW = pose.RotationW,
                PoseKind = pose.PoseKind, HasVisualScaleOverride = pose.HasVisualScaleOverride,
                VisualScaleX = pose.VisualScaleX, VisualScaleY = pose.VisualScaleY, VisualScaleZ = pose.VisualScaleZ };
    }
}
