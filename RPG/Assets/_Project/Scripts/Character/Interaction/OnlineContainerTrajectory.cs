using Game.Core;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 原容器倾倒的唯一几何算法。Client只把点写入预览，DS只取目标点交权威事务；
    /// 二者共享原起点、采样步长、重力和碰撞半径，均不改变Actor朝向或移动。
    /// </summary>
    internal static class OnlineContainerTrajectory
    {
        internal const int Samples = 48;

        /// <summary>
        /// 喷口来自可信Actor与完整aim，预览终点不再作为真实材料的生成点。先检查Actor到喷口的短段，
        /// 避免隔着贴身墙直接生成另一侧的水；忽略自身Collider，采样溢出明确拒绝而不是猜一个落点。
        /// </summary>
        internal static MaterialTransferFailure PrepareEmission(Transform actor,Vector3 direction,float speed,float lift,
            RaycastHit[] hits,out Vector3 origin,out Vector3 velocity)
        {
            origin=velocity=default;
            if(actor==null || hits==null || hits.Length==0 || !Finite(actor.position) || !Finite(direction)
                || !float.IsFinite(direction.sqrMagnitude) || direction.sqrMagnitude<.001f || !float.IsFinite(speed) || speed<=0
                || !float.IsFinite(lift) || lift<0)return MaterialTransferFailure.InvalidRequest;
            direction.Normalize();Vector3 horizontal=direction;horizontal.y=0;
            horizontal=horizontal.sqrMagnitude>=.001f?horizontal.normalized:actor.forward;
            Vector3 start=actor.position+Vector3.up*1.2f;
            origin=start+horizontal*.8f;velocity=direction*speed+Vector3.up*lift;
            if(!Finite(origin) || !Finite(velocity) || velocity.sqrMagnitude>4096f)return MaterialTransferFailure.InvalidRequest;
            int count=Physics.SphereCastNonAlloc(start,.18f,horizontal,hits,.8f,~0,QueryTriggerInteraction.Ignore);
            if(count==hits.Length)return MaterialTransferFailure.SamplingLimit;
            for(int i=0;i<count;i++)
                if(hits[i].collider!=null && !hits[i].collider.transform.IsChildOf(actor))return MaterialTransferFailure.GeometryRejected;
            return MaterialTransferFailure.None;
        }

        internal static MaterialTransferFailure Prepare(Transform actor, Vector3 direction, float speed, float lift,
            RaycastHit[] hits, Vector3[] points, out int pointCount, out Vector3 target)
        {
            pointCount = 0; target = actor != null ? actor.position : default;
            if (actor == null || hits == null || hits.Length == 0 || points != null && points.Length < Samples
                || !Finite(direction) || !float.IsFinite(direction.sqrMagnitude) || direction.sqrMagnitude < .001f
                || !float.IsFinite(speed) || speed <= 0 || !float.IsFinite(lift) || lift < 0)
                return MaterialTransferFailure.InvalidRequest;
            direction.Normalize();
            // 原Controller先AlignWithCamera；此处等效计算水平起点，垂直瞄准保留原Actor forward。
            Vector3 horizontal = direction; horizontal.y = 0;
            horizontal = horizontal.sqrMagnitude >= .001f ? horizontal.normalized : actor.forward;
            Vector3 origin = actor.position + horizontal * .8f + Vector3.up * 1.2f;
            Vector3 velocity = direction * speed + Vector3.up * lift;
            if (!Finite(origin) || !Finite(velocity)) return MaterialTransferFailure.InvalidRequest;
            if (points != null) points[0] = origin;
            Vector3 previous = origin; target = origin;
            for (int i = 1; i < Samples; i++)
            {
                float time = i * .065f;
                Vector3 point = origin + velocity * time + Physics.gravity * (.5f * time * time);
                if (!Finite(point)) return MaterialTransferFailure.InvalidRequest;
                Vector3 delta = point - previous; float distance = delta.magnitude;
                if (!float.IsFinite(distance)) return MaterialTransferFailure.InvalidRequest;
                if (distance > .000001f)
                {
                    int count = Physics.SphereCastNonAlloc(previous, .18f, delta / distance, hits, distance, ~0, QueryTriggerInteraction.Ignore);
                    if (count == hits.Length) return MaterialTransferFailure.SamplingLimit;
                    float nearest = distance; bool hit = false;
                    for (int j = 0; j < count; j++)
                        if (!hits[j].collider.transform.IsChildOf(actor) && hits[j].distance < nearest)
                        { nearest = hits[j].distance; point = hits[j].point; hit = true; }
                    if (points != null) points[i] = point;
                    if (hit) { pointCount = i + 1; target = point; return MaterialTransferFailure.None; }
                }
                else if (points != null) points[i] = point;
                previous = point;
            }
            pointCount = Samples; target = previous;
            return MaterialTransferFailure.None;
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}

