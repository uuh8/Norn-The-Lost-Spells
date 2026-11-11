using System;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 原P8胜利返回门的离散位置查询。旧单机Session与DS共用同一规则，避免两端各写一份近似算法，
    /// 令Client看到的门与服务器实际可交互区域不同。此方法只返回Pose，不移动玩家、不创建门或授予成功奖励。
    /// </summary>
    public static class ReturnPortalPlacement
    {
        public static Pose Resolve(Transform actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            Vector3 position = actor.position;
            // 原规则优先脚边四米的可站立地面，八方向、每次45度；有界查询只在胜利放门时执行一次。
            for (int i = 0; i < 8; i++)
            {
                Vector3 offset = Quaternion.Euler(0, i * 45, 0) * actor.right * 4;
                Vector3 point = actor.position + offset;
                if (!Physics.Raycast(point + Vector3.up * 5, Vector3.down, out var ground, 12, ~0, QueryTriggerInteraction.Ignore)
                    || ground.normal.y < .6f) continue;
                point.y = ground.point.y;
                if (Physics.CheckCapsule(point + Vector3.up, point + Vector3.up * 3, .8f, ~0, QueryTriggerInteraction.Ignore)) continue;
                position = point; break;
            }
            // 无候选时保留原玩家位置fallback；不凭空改成原点、Boss位置或新增一种寻找地面政策。
            Vector3 direction = actor.position - position; direction.y = 0;
            var rotation = direction.sqrMagnitude > .01f ? Quaternion.LookRotation(direction) : actor.rotation;
            return new Pose(position, rotation);
        }
    }
}
