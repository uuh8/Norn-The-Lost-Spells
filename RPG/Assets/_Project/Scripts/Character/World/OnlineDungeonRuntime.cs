using System;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 正式副本的纯C#遭遇Authority/Replica。角色移动、伤害与Health仍由原Character/Combat推进；
    /// 这里仅裁定当前区域空间准入、NPC激活、全员清场与门，不创建物品、存档、Checkpoint或队伍失败。
    /// Replica只安装完整进度并驱动原门/安全区/HUD，不扫描死亡，不反向请求Authority开战。
    /// </summary>
    public sealed class OnlineDungeonRuntime : IDisposable, IDungeonPresentationReadOnly, IDungeonHudPresentationPolicy
    {
        private readonly OnlineDungeonDefinition _definition;
        private readonly OnlineDungeonWorldBindings _bindings;
        private readonly ICombatActorRosterReadOnly _players;
        private readonly IOnlineDungeonNpcReadOnly _npcs;
        private readonly bool _authority;
        private readonly Action<ulong, bool> _setNpcActive;
        private readonly Action<ulong> _activateBoss;
        private readonly uint[] _areaNpcMasks;
        private readonly int[] _areaNpcCounts;
        private readonly int _presentationOwner, _worldSceneHandle;
        private readonly Transform[] _playerTransforms = new Transform[4];
        private readonly CharacterController[] _playerControllers = new CharacterController[4];
        private uint _revision, _serverTick, _deadNpcMask, _insidePlayers, _armedPlayers;
        private int _currentArea;
        private uint _remaining;
        private OnlineDungeonPhase _phase = OnlineDungeonPhase.Armed;
        private bool _hasTick, _disposed;

        public OnlineDungeonRuntime(OnlineDungeonDefinition definition, OnlineDungeonWorldBindings bindings,
            ICombatActorRosterReadOnly players, bool authority, Action<ulong, bool> setNpcActive, Action<ulong> activateBoss)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (!definition.TryValidate(out var error)) throw new ArgumentException("副本Definition非法：" + error, nameof(definition));
            if (bindings == null || !bindings.TryValidate(definition.EncounterCount, out error)) throw new ArgumentException("副本Scene接线非法：" + error, nameof(bindings));
            if (players == null || !(players is IOnlineDungeonNpcReadOnly npcReader)) throw new ArgumentException("Roster必须同时提供NPC只读状态。", nameof(players));
            if (authority && (setNpcActive == null || activateBoss == null)) throw new ArgumentException("Authority缺少受控NPC激活回调。");
            _definition = definition; _bindings = bindings; _players = players; _npcs = npcReader;
            _presentationOwner = bindings.GetInstanceID(); _worldSceneHandle = bindings.gameObject.scene.handle;
            _authority = authority; _setNpcActive = setNpcActive; _activateBoss = activateBoss;
            _areaNpcMasks = new uint[definition.EncounterCount]; _areaNpcCounts = new int[definition.EncounterCount];
            for (int i = 0; i < definition.NpcCount; i++)
            {
                var npc = definition.GetNpc(i); _areaNpcMasks[npc.EncounterIndex] |= 1u << i; _areaNpcCounts[npc.EncounterIndex]++;
                if (authority) _setNpcActive(npc.ActorId, npc.InitiallyVisible);
            }
            _remaining = (uint)_areaNpcCounts[0]; _revision = authority ? 1u : 0u;
            _bindings.Prepare(); _bindings.OpenAll();
        }

        /// <summary>
        /// 仅Authority由唯一逻辑Tick Owner推进。已消费Tick不得重复；固定最多12 NPC/4玩家扫描不分配，
        /// 不订阅全局Death作为第二个计数器，因此场外死亡、重复事件或其他副本都不能清场。
        /// </summary>
        public void Step(float deltaTime, uint tick)
        {
            if (_disposed || !_authority || tick == 0 || !float.IsFinite(deltaTime) || deltaTime <= 0 ||
                _hasTick && unchecked((int)(tick - _serverTick)) <= 0) return;
            _hasTick = true; _serverTick = tick;
            if (_phase == OnlineDungeonPhase.StageCleared) return;
            if (_phase == OnlineDungeonPhase.Armed)
            {
                if (CanStartCurrentArea()) BeginArea();
                return;
            }
            int remaining = 0; uint mask = _areaNpcMasks[_currentArea];
            for (int i = 0; i < _definition.NpcCount; i++)
            {
                uint bit = 1u << i; if ((mask & bit) == 0) continue;
                var npc = _definition.GetNpc(i);
                if (_npcs.TryGetNpcState(npc.ActorId, out bool alive, out _) && !alive) _deadNpcMask |= bit;
                if ((_deadNpcMask & bit) == 0) remaining++;
            }
            if (_remaining != (uint)remaining) { _remaining = (uint)remaining; AdvanceRevision(); }
            if (remaining != 0) return;
            _bindings.SetClosed(_currentArea, false); _insidePlayers = _armedPlayers = 0;
            if (_currentArea + 1 == _definition.EncounterCount) _phase = OnlineDungeonPhase.StageCleared;
            else { _currentArea++; _phase = OnlineDungeonPhase.Armed; _remaining = (uint)_areaNpcCounts[_currentArea]; }
            AdvanceRevision();
        }

        private bool CanStartCurrentArea()
        {
            bool exitToStart = _definition.GetEncounter(_currentArea).ExitToStart;
            int count = Math.Min(4, _players.Count);
            for (int i = 0; i < count; i++)
            {
                uint bit = 1u << i;
                if (!_players.TryGetActor(i, out var actor) || !actor.IsAlive || !actor.IsConnected || !actor.Targetable)
                {
                    // 断线/死亡是权限丢失，不是跨出安全区；重连后需重新获得inside资格。
                    _insidePlayers &= ~bit; _armedPlayers &= ~bit; continue;
                }
                Vector3 position = actor.Position; float radius = 0;
                if (_playerTransforms[i] != actor.Transform)
                {
                    _playerTransforms[i] = actor.Transform;
                    _playerControllers[i] = actor.Transform != null ? actor.Transform.GetComponent<CharacterController>() : null;
                    _insidePlayers &= ~bit; _armedPlayers &= ~bit;
                }
                var controller = _playerControllers[i];
                if (controller != null)
                {
                    position = controller.transform.TransformPoint(controller.center);
                    var scale = controller.transform.lossyScale;
                    radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                }
                bool inside = _bindings.ContainsEntry(_currentArea, position, radius);
                if (!exitToStart) { if (inside) return true; continue; }
                bool wasInside = (_insidePlayers & bit) != 0;
                if (inside) { _insidePlayers |= bit; _armedPlayers |= bit; }
                else
                {
                    _insidePlayers &= ~bit;
                    if (wasInside && (_armedPlayers & bit) != 0) return true;
                }
            }
            return false;
        }
        private void BeginArea()
        {
            _phase = OnlineDungeonPhase.Active; _bindings.SetClosed(_currentArea, true);
            uint mask = _areaNpcMasks[_currentArea];
            for (int i = 0; i < _definition.NpcCount; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                var npc = _definition.GetNpc(i); _setNpcActive(npc.ActorId, true);
                if (npc.Prefab.GetComponent<WizardBossController>() != null) _activateBoss(npc.ActorId);
            }
            if (_definition.GetEncounter(_currentArea).ExitToStart) _bindings.FinishSafeZone(_currentArea);
            _insidePlayers = _armedPlayers = 0; AdvanceRevision();
        }
        private void AdvanceRevision()
        {
            // 不允许把修订绕回0伪装成新Run。达到上限是确定故障，需要更换副本实例。
            if (_revision == uint.MaxValue) throw new InvalidOperationException("副本进度修订已耗尽。");
            _revision++;
        }

        public bool TryCapture(out OnlineDungeonProgress progress)
        {
            progress = default;
            if (_disposed || _revision == 0 || _serverTick == 0) return false;
            progress = new OnlineDungeonProgress { Revision = _revision, ServerTick = _serverTick,
                EncounterIndexPlusOne = (uint)_currentArea + 1, RemainingEnemies = _remaining, Phase = _phase };
            return true;
        }

        // UI只能看到这个Core接口，不会沿它取得Character Definition或NPC激活回调。
        // 仅转发导出配置给公共 UI；Runtime 不查找或操纵 Canvas，也不根据当前 HP 猜测 HUD 模式。
        // 失效来源返回 false，配合 TryCaptureProgress 的失败使已退出世界不能重新开启旧遭遇文本。
        public bool ShowEncounterHud => !_disposed && _definition != null && _definition.ShowEncounterHud;
        public int EncounterCount => !_disposed && _definition != null ? _definition.EncounterCount : 0;
        public string GetDisplayName(int index) => index >= 0 && index < EncounterCount ? _definition.GetEncounter(index).DisplayName : string.Empty;
        public string GetObjectiveText(int index) => index >= 0 && index < EncounterCount ? _definition.GetEncounter(index).ObjectiveText : string.Empty;
        public bool TryCaptureProgress(out OnlineDungeonProgress progress) => TryCapture(out progress);

        /// <summary>
        /// 只能由已认证上层传入同Run/同Dungeon的完整进度。先验证全部值再改变门与本地读数；
        /// 新Revision可与上一份处于同Tick，较低Tick或重复Revision不能重放HUD通知/安全区收尾。
        /// </summary>
        public bool ApplyReplica(in OnlineDungeonProgress progress)
        {
            if (_disposed || _authority || progress.Revision == 0 || progress.Revision <= _revision || progress.ServerTick == 0 ||
                _serverTick != 0 && unchecked((int)(progress.ServerTick - _serverTick)) < 0 ||
                progress.EncounterIndexPlusOne < 1 || progress.EncounterIndexPlusOne > _definition.EncounterCount ||
                progress.Phase < OnlineDungeonPhase.Armed || progress.Phase > OnlineDungeonPhase.StageCleared) return false;
            int index = (int)progress.EncounterIndexPlusOne - 1; uint maximum = (uint)_areaNpcCounts[index];
            if (progress.Phase == OnlineDungeonPhase.StageCleared)
            { if (index + 1 != _definition.EncounterCount || progress.RemainingEnemies != 0) return false; }
            else if (progress.RemainingEnemies < 1 || progress.RemainingEnemies > maximum ||
                progress.Phase == OnlineDungeonPhase.Armed && progress.RemainingEnemies != maximum) return false;
            if (_revision != 0 && (index < _currentArea || _phase == OnlineDungeonPhase.StageCleared ||
                index == _currentArea && _phase == OnlineDungeonPhase.Active && progress.Phase == OnlineDungeonPhase.Armed)) return false;
            // 同一场遭遇里的死亡会保留在Authority的dead-mask中，因此剩余数只能下降。
            // 即使网络包Revision更新，也不能用“合法范围内的回升”让已清除NPC重新进入HUD统计；
            // 必须在安装Revision之前拒绝，下一份正确的同Revision消息才仍有机会被接受。
            if (_revision != 0 && index == _currentArea && _phase == OnlineDungeonPhase.Active &&
                progress.Phase == OnlineDungeonPhase.Active && progress.RemainingEnemies > _remaining) return false;

            bool first = _revision == 0; int previousArea = _currentArea; var previousPhase = _phase;
            _revision = progress.Revision; _serverTick = progress.ServerTick; _hasTick = true;
            _currentArea = index; _phase = progress.Phase; _remaining = progress.RemainingEnemies;
            _bindings.OpenAll();
            if (_phase == OnlineDungeonPhase.Active) _bindings.SetClosed(index, true);
            for (int i = 0; i <= index; i++)
                if (_definition.GetEncounter(i).ExitToStart && (i < index || _phase != OnlineDungeonPhase.Armed)) _bindings.FinishSafeZone(i);
            // 只有Replica已接受完整获授权进度后才公布读源。Core缓存不认证，不能在构造时用空默认值宣称Ready。
            DungeonPresentation.Bind(_presentationOwner, _worldSceneHandle, this);
            PublishReplicaHud(first, previousArea, previousPhase);
            return true;
        }
        private void PublishReplicaHud(bool first, int previousArea, OnlineDungeonPhase previousPhase)
        {
            // 这些事件仅供Client复用原HUD。Authority绝不发奖励/单机结算事件；Replica也只给null奖励引用，
            // 真正线上掉落/持久化仍需要独立业务授权，不能把UI通知当作资产授予。
            if (!first && _currentArea > previousArea)
                EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
                    { EncounterIndex = previousArea, EncounterCount = _definition.EncounterCount });
            if (first || _currentArea != previousArea || previousPhase != _phase)
                EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent { PreviousState = first ? RunState.NotStarted : RunState.Running,
                    CurrentState = _phase == OnlineDungeonPhase.StageCleared ? RunState.StageCleared : RunState.Running, CurrentEncounterIndex = _currentArea });
            if (_phase == OnlineDungeonPhase.Active && (first || _currentArea != previousArea || previousPhase != OnlineDungeonPhase.Active))
                EventBus<EncounterStartedEvent>.Publish(new EncounterStartedEvent
                    { EncounterIndex = _currentArea, EncounterCount = _definition.EncounterCount, EnemyCount = _areaNpcCounts[_currentArea] });
            EventBus<EncounterProgressChangedEvent>.Publish(new EncounterProgressChangedEvent
                { EncounterIndex = _currentArea, EncounterCount = _definition.EncounterCount, RemainingEnemyCount = (int)_remaining });
            if (_phase == OnlineDungeonPhase.StageCleared && (first || previousPhase != OnlineDungeonPhase.StageCleared))
                EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
                    { EncounterIndex = _currentArea, EncounterCount = _definition.EncounterCount });
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (!_authority) DungeonPresentation.Clear(_presentationOwner, this);
            // Scene/Registry仍拥有Actor，不在这里销毁或重置其生命/资源；只结束自己的门表现责任。
            if (_bindings != null) _bindings.OpenAll();
        }
    }
}
