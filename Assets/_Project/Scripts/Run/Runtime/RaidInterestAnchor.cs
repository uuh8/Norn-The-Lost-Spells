using UnityEngine;
namespace Game.Run
{
    /// <summary>地图出生前已存在的活动范围中心；跟随真实玩家，Run无需反向读取元素后端。</summary>
    public sealed class RaidInterestAnchor : MonoBehaviour
    {
        private Transform _player;
        public void Configure(Transform player) { _player=player; if(_player!=null)transform.position=_player.position; }
        private void LateUpdate() { if(_player!=null && Time.timeScale>0)transform.position=_player.position; }
    }
}
