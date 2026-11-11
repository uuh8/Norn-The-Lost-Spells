using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Core
{
    /// <summary>私人据点的原模型姿态值，不携带资产内容或Physics写权限；网络层只搬运有限标量。</summary>
    [Serializable] public sealed class OnlineAccountWorldItemPose
    {
        public string SceneKey = "", WorldSceneKey = "";
        public float PositionX, PositionY, PositionZ, RotationX, RotationY, RotationZ, RotationW = 1;
        public int PoseKind;
        public bool HasVisualScaleOverride;
        public float VisualScaleX = 1, VisualScaleY = 1, VisualScaleZ = 1;
        public OnlineAccountWorldItemPose CopyValue() => (OnlineAccountWorldItemPose)MemberwiseClone();
        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(SceneKey) || string.IsNullOrWhiteSpace(WorldSceneKey) || SceneKey.Length > 128 || WorldSceneKey.Length > 128
                || PoseKind < 0 || PoseKind > 2 || !Finite(PositionX) || !Finite(PositionY) || !Finite(PositionZ)
                || !Finite(RotationX) || !Finite(RotationY) || !Finite(RotationZ) || !Finite(RotationW)
                || Mathf.Abs(RotationX * RotationX + RotationY * RotationY + RotationZ * RotationZ + RotationW * RotationW - 1) > .01f)
                return false;
            return Finite(VisualScaleX) && Finite(VisualScaleY) && Finite(VisualScaleZ)
                && (!HasVisualScaleOverride || ValidScale(VisualScaleX) && ValidScale(VisualScaleY) && ValidScale(VisualScaleZ));
        }
        public static bool Same(OnlineAccountWorldItemPose a, OnlineAccountWorldItemPose b)
            => ReferenceEquals(a, b) || a != null && b != null && a.SceneKey == b.SceneKey && a.WorldSceneKey == b.WorldSceneKey
                && a.PositionX == b.PositionX && a.PositionY == b.PositionY && a.PositionZ == b.PositionZ
                && a.RotationX == b.RotationX && a.RotationY == b.RotationY && a.RotationZ == b.RotationZ && a.RotationW == b.RotationW
                && a.PoseKind == b.PoseKind && a.HasVisualScaleOverride == b.HasVisualScaleOverride
                && a.VisualScaleX == b.VisualScaleX && a.VisualScaleY == b.VisualScaleY && a.VisualScaleZ == b.VisualScaleZ;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidScale(float value) => Finite(value) && Mathf.Abs(value) > .00001f;
    }
    [Serializable] public sealed class OnlineAccountWorldItemSnapshot
    {
        public string InstanceId = "", WorldOwnerId = "";
        public OnlineAccountWorldItemPose Pose;
        public OnlineAccountWorldItemSnapshot CopyValue() => new OnlineAccountWorldItemSnapshot
        { InstanceId = InstanceId, WorldOwnerId = WorldOwnerId, Pose = Pose?.CopyValue() };
    }
    /// <summary>
    /// 账号Profile的中立完整值。它不携带Token、Session、Repository或Run执行权限。
    /// 仓库/携带使用同一组原实例位置；不能拿CombatAssets携带子集冒充账号全部资产。
    /// </summary>
    [Serializable] public sealed class OnlineAccountInventorySnapshot
    {
        public const uint CurrentSchemaVersion = 1;
        public const int MaximumItems = OnlineOwnedAssetSnapshot.MaximumItems + 24;
        public uint SchemaVersion, Version;
        public string PlayerId = "", ContentManifestHash = "", ActiveRunId = "", SelectedWandInstanceId = "";
        public long Gold;
        public bool AssetsLocked;
        public OnlineOwnedItemSnapshot[] Items;
        public OnlineAccountWorldItemSnapshot[] WorldItems = Array.Empty<OnlineAccountWorldItemSnapshot>();

        /// <summary>只在HTTP/Profile/换Scene的冷边界复制；UI不能借保留的DTO数组修改已安装快照。</summary>
        public OnlineAccountInventorySnapshot CopyValue()
        {
            var result = new OnlineAccountInventorySnapshot { SchemaVersion = SchemaVersion, Version = Version,
                PlayerId = PlayerId, ContentManifestHash = ContentManifestHash, ActiveRunId = ActiveRunId,
                SelectedWandInstanceId = SelectedWandInstanceId, Gold = Gold, AssetsLocked = AssetsLocked,
                Items = Items == null ? null : new OnlineOwnedItemSnapshot[Items.Length],
                WorldItems = WorldItems == null ? null : new OnlineAccountWorldItemSnapshot[WorldItems.Length] };
            if (Items != null) for (int i = 0; i < Items.Length; i++) result.Items[i] = CopyItem(Items[i]);
            if (WorldItems != null) for (int i = 0; i < WorldItems.Length; i++) result.WorldItems[i] = WorldItems[i]?.CopyValue();
            return result;
        }

        private static OnlineOwnedItemSnapshot CopyItem(OnlineOwnedItemSnapshot item)
        {
            if (item == null) return null;
            var result = new OnlineOwnedItemSnapshot { InstanceId = item.InstanceId, ContentId = item.ContentId,
                ParentInstanceId = item.ParentInstanceId, Revision = item.Revision, LocationKind = item.LocationKind,
                Slot = item.Slot, X = item.X, Y = item.Y, QuarterTurns = item.QuarterTurns,
                RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                EntryBoundBackpack = item.EntryBoundBackpack, UseTemplateWandStats = item.UseTemplateWandStats };
            if (item.WandStats != null)
            {
                var value = item.WandStats;
                result.WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = value.MaxMana,
                    ManaRegenPerSecond = value.ManaRegenPerSecond, BaseCastDelay = value.BaseCastDelay,
                    BaseReloadTime = value.BaseReloadTime, BaseSpreadDegrees = value.BaseSpreadDegrees,
                    ProgramCapacity = value.ProgramCapacity, BaseDraws = value.BaseDraws, Shuffle = value.Shuffle,
                    AlwaysCastContentIds = value.AlwaysCastContentIds == null ? null : (string[])value.AlwaysCastContentIds.Clone() };
            }
            if (item.ContainerState != null)
            {
                var values = item.ContainerState.Components;
                var copy = values == null ? null : new OnlineOwnedMaterialAmountSnapshot[values.Length];
                if (copy != null) for (int i = 0; i < copy.Length; i++)
                    if (values[i] != null) copy[i] = new OnlineOwnedMaterialAmountSnapshot { MaterialKey = values[i].MaterialKey, Quantity = values[i].Quantity };
                result.ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = item.ContainerState.SchemaVersion, Components = copy };
            }
            if (item.MaterialState != null) result.MaterialState = new OnlineOwnedMaterialStateSnapshot
            { SchemaVersion = item.MaterialState.SchemaVersion, MaterialKey = item.MaterialState.MaterialKey, Quantity = item.MaterialState.Quantity };
            if (item.AttachmentState != null) result.AttachmentState = new OnlineOwnedSpellAttachmentStateSnapshot
            { SchemaVersion = item.AttachmentState.SchemaVersion, ContainerId = item.AttachmentState.ContainerId,
                Dose = item.AttachmentState.Dose, Enabled = item.AttachmentState.Enabled };
            return result;
        }
    }

    /// <summary>站点每帧只读当前身份/版本/租约；值类型和已缓存字符串不复制整份Items。</summary>
    public readonly struct OnlineAccountHubSummary
    {
        public readonly string PlayerId, InventoryId, ActiveRunId;
        public readonly uint Version;
        public readonly bool AssetsLocked;
        internal OnlineAccountHubSummary(string playerId, string inventoryId, uint version, bool assetsLocked, string activeRunId)
        { PlayerId = playerId; InventoryId = inventoryId; Version = version; AssetsLocked = assetsLocked; ActiveRunId = activeRunId; }
    }

    /// <summary>inactive Hub也能预安装；组合层不必反向依赖UI或Run实现类型。</summary>
    public interface IAccountInventoryHubConsumer
    {
        bool TryPrepareAccountInventory(OnlineAccountInventorySnapshot snapshot, out string error);
        bool HasPreparedAccountInventory(string playerId, uint minimumVersion);
    }

    /// <summary>返回Hub激活前先封闭旧写入口；这里只撤销本地写，不授予任何账号写权限。</summary>
    public interface IAccountInventoryHubWriteGuard
    {
        bool RequireAccountInventoryReadOnly(int sceneHandle);
    }

    public struct OnlineAccountInventoryHubChangedEvent : IGameEvent
    {
        public int SceneHandle;
        public string PlayerId;
        public uint Version;
    }

    /// <summary>
    /// 唯一账号快照缓存和显式Hub作用域。首次加载先声明原菜单目标，ESC复用原返回Scene。
    /// 失效身份清数据但保留目标/Scene的旧档禁写要求，不能因网络失败回落到Inspector中的本地存档。
    /// </summary>
    public static class OnlineAccountInventoryHubScope
    {
        private static int _owner, _sceneHandle;
        private static OnlineAccountInventorySnapshot _snapshot;
        private static string _fingerprint;
        private static string _hubInventoryId;
        private static string _pendingHubTarget;
        private static int _pendingHubOwner;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _owner = _sceneHandle = 0; _snapshot = null; _fingerprint = null;
            _hubInventoryId = null;
            _pendingHubTarget = null; _pendingHubOwner = 0;
        }

        /// <summary>仅已确认、未被副本占用的账号可预声明目标；不加载场景、不解锁任何租约。</summary>
        public static bool TryArmHubEntry(int owner, string scenePathOrName, out string error)
        {
            error = null;
            if (owner == 0 || _owner != owner || _snapshot == null || _snapshot.AssetsLocked
                || !string.IsNullOrEmpty(_snapshot.ActiveRunId))
            { error = "账号资产尚未确认或仍由原副本持有，不能进入据点。"; return false; }
            if (!TryNormalizeHubTarget(scenePathOrName, out var target))
            { error = "原据点场景目标非法，未改用本地存档入口。"; return false; }
            if (_pendingHubTarget != null && (_pendingHubOwner != owner || _pendingHubTarget != target))
            { error = "另一次据点加载仍在等待，不替换其资产来源。"; return false; }
            _pendingHubTarget = target; _pendingHubOwner = owner;
            return true;
        }

        /// <summary>仅用于明确尚未接受的加载；已绑定Scene的禁写要求保留，不能用取消绕回旧档。</summary>
        public static void CancelPendingHubEntry(int owner)
        {
            if (_pendingHubOwner != owner) return;
            _pendingHubTarget = null; _pendingHubOwner = 0;
        }

        public static bool IsRequiredForScene(int sceneHandle)
        {
            if (sceneHandle == 0) return false;
            if (_sceneHandle == sceneHandle) return true;
            if (_sceneHandle != 0 || _pendingHubTarget == null) return false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle == sceneHandle) return IsRequiredForScene(scene);
            }
            return false;
        }

        /// <summary>Session直接交入自己的Scene，在Awake/OnEnable时不依赖isLoaded或sceneLoaded回调。</summary>
        public static bool IsRequiredForScene(Scene scene)
        {
            if (!scene.IsValid()) return false;
            if (_sceneHandle == scene.handle) return true;
            if (_sceneHandle != 0 || _pendingHubTarget == null || !MatchesHubTarget(scene, _pendingHubTarget)) return false;
            BindRequiredScene(scene);
            return true;
        }

        private static bool TryNormalizeHubTarget(string value, out string target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value != value.Trim()
                || value.IndexOf('\0') >= 0 || value.IndexOf(':') >= 0) return false;
            string normalized = value.Replace('\\', '/');
            if (normalized.IndexOf('/') >= 0)
            {
                if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || !normalized.EndsWith(".unity", StringComparison.Ordinal)
                    || normalized.Contains("//") || normalized.Contains("/../") || normalized.Contains("/./")) return false;
            }
            else if (normalized == "." || normalized == ".." || normalized.EndsWith(".unity", StringComparison.Ordinal)) return false;
            target = normalized; return true;
        }

        private static bool MatchesHubTarget(Scene scene, string target)
            => target.IndexOf('/') >= 0 ? scene.path == target : scene.name == target;
        private static void BindRequiredScene(Scene scene)
        {
            _sceneHandle = scene.handle;
            SceneManager.sceneUnloaded -= OnSceneUnloaded; SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        public static bool TryPublish(int owner, OnlineAccountInventorySnapshot snapshot, out string error)
        {
            error = null;
            if (owner == 0 || snapshot == null || snapshot.SchemaVersion != OnlineAccountInventorySnapshot.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(snapshot.PlayerId) || snapshot.Version == 0 || snapshot.Gold < 0
                || snapshot.Items == null || snapshot.Items.Length > OnlineAccountInventorySnapshot.MaximumItems
                || snapshot.WorldItems == null || snapshot.WorldItems.Length > OnlineAccountInventorySnapshot.MaximumItems)
            { error = "账号资产快照不完整。"; return false; }
            if (_owner != 0 && _owner != owner) { error = "账号资产来源已有另一个Session持有。"; return false; }
            if (_snapshot != null && (_snapshot.PlayerId != snapshot.PlayerId || snapshot.Version < _snapshot.Version))
            { error = "账号资产身份或版本已变化。"; return false; }
            var copy = snapshot.CopyValue();
            // 锁可在同一库存版本变化，资产正文不可以。排序只作用于本副本，不依赖SQL行序。
            var comparable = copy.CopyValue(); comparable.AssetsLocked = false; comparable.ActiveRunId = "";
            Array.Sort(comparable.Items, (a, b) => string.CompareOrdinal(a?.InstanceId, b?.InstanceId));
            Array.Sort(comparable.WorldItems, (a, b) => string.CompareOrdinal(a?.InstanceId, b?.InstanceId));
            string fingerprint = JsonUtility.ToJson(comparable);
            if (_snapshot != null && copy.Version == _snapshot.Version && fingerprint != _fingerprint)
            { error = "相同账号资产版本不能改变物品或金币。"; return false; }
            _hubInventoryId = "account." + copy.PlayerId; // 仅Profile冷边界拼接，站点查询直接借出同一个字符串。
            _owner = owner; _snapshot = copy; _fingerprint = fingerprint; Publish(); return true;
        }

        public static bool TryGetSummaryForScene(int sceneHandle, out OnlineAccountHubSummary summary)
        {
            summary = default;
            if (_owner == 0 || _snapshot == null || _hubInventoryId == null || !IsRequiredForScene(sceneHandle)) return false;
            summary = new OnlineAccountHubSummary(_snapshot.PlayerId, _hubInventoryId, _snapshot.Version,
                _snapshot.AssetsLocked, _snapshot.ActiveRunId);
            return true;
        }

        /// <summary>确认租约只撤销访问，不替换任何物品/金币/版本，也不提供客户端解锁开关。</summary>
        public static bool TryMarkAssetsLocked(int owner, string playerId, string activeRunId)
        {
            if (owner == 0 || _owner != owner || _snapshot == null || _snapshot.PlayerId != playerId) return false;
            _snapshot.AssetsLocked = true; _snapshot.ActiveRunId = activeRunId ?? "";
            Publish(); return true;
        }

        public static bool TryRequireScene(int owner, int sceneHandle, out string error)
        {
            error = null;
            if (_owner != owner || _snapshot == null || _snapshot.AssetsLocked || !string.IsNullOrEmpty(_snapshot.ActiveRunId))
            { error = "账号资产仍被副本占用或尚未确认。"; return false; }
            // Unity公开API按已加载场景索引查询；不能调用Editor/引擎内部的句柄查找方法。
            // 这里是一次性返回Hub准备，遍历少量场景不引入常驻逐帧查找。
            Scene scene = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var candidate = SceneManager.GetSceneAt(i);
                if (candidate.handle != sceneHandle) continue;
                scene = candidate; break;
            }
            if (!scene.IsValid() || !scene.isLoaded) { error = "原Hub尚未加载。"; return false; }
            if (_pendingHubTarget != null && !MatchesHubTarget(scene, _pendingHubTarget))
            { error = "已加载场景不是本次确认的据点目标。"; return false; }
            BindRequiredScene(scene);
            _pendingHubTarget = null; _pendingHubOwner = 0;
            Publish(); return true;
        }

        public static bool TryCopyForScene(int sceneHandle, out OnlineAccountInventorySnapshot snapshot)
        {
            snapshot = null;
            if (!IsRequiredForScene(sceneHandle) || _snapshot == null) return false;
            snapshot = _snapshot.CopyValue(); return true;
        }

        public static void Clear(int owner)
        {
            if (_owner != owner) return;
            _owner = 0; _snapshot = null; _fingerprint = null; _hubInventoryId = null; Publish();
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (scene.handle != _sceneHandle) return;
            _sceneHandle = 0;
            if (_pendingHubTarget == null) SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }
        private static void Publish()
        {
            if (_sceneHandle == 0) return;
            EventBus<OnlineAccountInventoryHubChangedEvent>.Publish(new OnlineAccountInventoryHubChangedEvent
            { SceneHandle = _sceneHandle, PlayerId = _snapshot?.PlayerId, Version = _snapshot?.Version ?? 0 });
        }
    }
}
