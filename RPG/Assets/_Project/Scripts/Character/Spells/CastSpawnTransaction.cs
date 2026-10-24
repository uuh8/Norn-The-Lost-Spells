using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Character
{
    // 提交后清空引用；失败先禁用再销毁，阻止同帧留下伤害。只在离散施法分配。
    internal sealed class CastSpawnTransaction
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Coroutine> _coroutines = new List<Coroutine>();
        private struct PendingAuthorityHandle { internal AuthorityCastScheduler Scheduler; internal uint Handle; }
        private readonly List<PendingAuthorityHandle> _authorityHandles = new List<PendingAuthorityHandle>();
        private readonly List<CombatOutputSpawnedEvent> _combatOutputs = new List<CombatOutputSpawnedEvent>();
        private readonly List<CombatCastSoundEvent> _combatSounds = new List<CombatCastSoundEvent>();
        private bool _finished;
        private bool _committed;
        private struct PendingOutput { internal OwnedWandCastContext Context;internal int Command; }
        private readonly List<PendingOutput> _outputs=new List<PendingOutput>();
        internal void Observe(in OwnedWandCastContext context,int command)
        {if(_finished){if(_committed)context.ReportOutput(command);}else _outputs.Add(new PendingOutput{Context=context,Command=command});}
        internal void Track(GameObject go) { if (!_finished) _objects.Add(go); }
        internal void Track(Coroutine coroutine) { if (!_finished) _coroutines.Add(coroutine); }
        internal void Track(AuthorityCastScheduler scheduler, uint handle)
        { if (!_finished) _authorityHandles.Add(new PendingAuthorityHandle { Scheduler = scheduler, Handle = handle }); }
        internal void PublishOutput(in CombatOutputSpawnedEvent output)
        {
            if (_finished) { if (_committed) EventBus<CombatOutputSpawnedEvent>.Publish(output); }
            else _combatOutputs.Add(output);
        }
        internal void PublishSound(in CombatCastSoundEvent sound)
        {
            // 声音与出生事实服从同一个资源提交边界；回滚的事务在未来 Payload 回调中也不能再发声。
            if (_finished) { if (_committed) EventBus<CombatCastSoundEvent>.Publish(sound); }
            else _combatSounds.Add(sound);
        }
        internal void Complete()
        {
            _finished = _committed = true; _objects.Clear(); _coroutines.Clear(); _authorityHandles.Clear();
            for (int i=0;i<_outputs.Count;i++) _outputs[i].Context.ReportOutput(_outputs[i].Command);
            _outputs.Clear();
            // 网络出生事实在整个组合资源提交后才可见；失败路径从不发布这些暂存事实。
            for (int i=0;i<_combatOutputs.Count;i++) EventBus<CombatOutputSpawnedEvent>.Publish(_combatOutputs[i]);
            _combatOutputs.Clear();
            for (int i=0;i<_combatSounds.Count;i++) EventBus<CombatCastSoundEvent>.Publish(_combatSounds[i]);
            _combatSounds.Clear();
        }
        internal void Rollback(MonoBehaviour owner)
        {
            _finished = true;
            _outputs.Clear();
            _combatOutputs.Clear();
            _combatSounds.Clear();
            for (int i = 0; i < _authorityHandles.Count; i++) _authorityHandles[i].Scheduler.Cancel(_authorityHandles[i].Handle);
            _authorityHandles.Clear();
            for (int i = 0; i < _coroutines.Count; i++) if (_coroutines[i] != null) owner.StopCoroutine(_coroutines[i]);
            for (int i = 0; i < _objects.Count; i++) if (_objects[i] != null)
            { _objects[i].SetActive(false); Object.Destroy(_objects[i]); }
            _objects.Clear(); _coroutines.Clear();
        }
    }
}
