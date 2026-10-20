using System.Collections.Generic;
using UnityEngine;

namespace Game.Character
{
    // 提交后清空引用；失败先禁用再销毁，阻止同帧留下伤害。只在离散施法分配。
    internal sealed class CastSpawnTransaction
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Coroutine> _coroutines = new List<Coroutine>();
        private bool _finished;
        private bool _committed;
        private struct PendingOutput { internal OwnedWandCastContext Context;internal int Command; }
        private readonly List<PendingOutput> _outputs=new List<PendingOutput>();
        internal void Observe(in OwnedWandCastContext context,int command)
        {if(_finished){if(_committed)context.ReportOutput(command);}else _outputs.Add(new PendingOutput{Context=context,Command=command});}
        internal void Track(GameObject go) { if (!_finished) _objects.Add(go); }
        internal void Track(Coroutine coroutine) { if (!_finished) _coroutines.Add(coroutine); }
        internal void Complete()
        { _finished = _committed = true; _objects.Clear(); _coroutines.Clear();for(int i=0;i<_outputs.Count;i++)_outputs[i].Context.ReportOutput(_outputs[i].Command);_outputs.Clear(); }
        internal void Rollback(MonoBehaviour owner)
        {
            _finished = true;
            _outputs.Clear();
            for (int i = 0; i < _coroutines.Count; i++) if (_coroutines[i] != null) owner.StopCoroutine(_coroutines[i]);
            for (int i = 0; i < _objects.Count; i++) if (_objects[i] != null)
            { _objects[i].SetActive(false); Object.Destroy(_objects[i]); }
            _objects.Clear(); _coroutines.Clear();
        }
    }
}
