using Game.Combat;
using Game.Core;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineCombatWorld
    {
        /// <summary>
        /// 反应在DS完成材料消费和伤害裁决后才发可靠表现事实。环境没有Actor，不能伪造玩家身份。
        /// 复用现有战斗通道和全局可靠序号，客户端只还原原Rendering消费者需要的值。
        /// </summary>
        private void OnElementReaction(ElementReactionEvent reaction)
        {
            if (_disposed || !_world.IsAuthority || !_world.CanAdvance || !_networkingAttached
                || reaction.Reaction != ElementReactionId.ToxicCombustion
                || reaction.Phase != ElementReactionPhase.Resolved) return;
            var fact = NewFact(12, 0);
            if (_instanceActors.TryGetValue(reaction.TargetId, out ulong actor)) fact.TargetActorId = actor;
            fact.Result = (uint)reaction.Reaction;
            fact.Phase = (uint)reaction.Phase;
            fact.PositionX = reaction.WorldPosition.x;
            fact.PositionY = reaction.WorldPosition.y;
            fact.PositionZ = reaction.WorldPosition.z;
            fact.Amount = reaction.NormalizedStrength;
            fact.Cooldown = reaction.ExpectedDuration;
            _channel.SendFact(fact);
        }

        private void ApplyReactionBurst(CombatFactMessage fact)
        {
            if (_disposed || _world.IsAuthority || fact.ActorId != 0 || fact.Result != 1 || fact.Phase != 3
                || fact.TargetActorId != 0 && !_world.IsConfiguredActor(fact.TargetActorId)
                || _lastReliable != 0 && unchecked((int)(fact.Sequence - _lastReliable)) <= 0) return;
            _lastReliable = fact.Sequence;
            int targetId = 0;
            if (fact.TargetActorId != 0)
            {
                var body = _world.Registry[(int)fact.TargetActorId - 1].Body;
                if (body != null) targetId = body.GetInstanceID();
            }
            // 只发布原表现事件，不重算反应、扣材料或调用DamagePipeline。
            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = targetId,
                Reaction = ElementReactionId.ToxicCombustion,
                Phase = ElementReactionPhase.Resolved,
                WorldPosition = new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ),
                NormalizedStrength = fact.Amount,
                ExpectedDuration = fact.Cooldown
            });
        }
    }
}
