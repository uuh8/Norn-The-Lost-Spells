using UnityEngine;

namespace Game.Character
{
    /// <summary>网络坐标只表达意图。实际出生位置由 Server 自己的动作/角色决定，不采用 Client 原点。</summary>
    public static class OnlineCastIntentRules
    {
        public static bool ValidateAim(Vector3 authorityOrigin, Vector3 claimedOrigin, Vector3 aimPoint,
            float maxOriginDistance, float maxAimDistance)
        {
            if (!Finite(authorityOrigin) || !Finite(claimedOrigin) || !Finite(aimPoint)
                || !float.IsFinite(maxOriginDistance) || maxOriginDistance < 0
                || !float.IsFinite(maxAimDistance) || maxAimDistance <= 0) return false;
            // 原点容差吸收有限网络延迟内的移动；容差绝不授予从该位置生成投射物的权限。
            if ((claimedOrigin - authorityOrigin).sqrMagnitude > maxOriginDistance * maxOriginDistance) return false;
            float distanceSquared = (aimPoint - authorityOrigin).sqrMagnitude;
            return distanceSquared >= .0001f && distanceSquared <= maxAimDistance * maxAimDistance;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
