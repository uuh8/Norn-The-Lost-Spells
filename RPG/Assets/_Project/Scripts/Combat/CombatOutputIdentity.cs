using System;
using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 本进程中传递根施法身份的标记。它不拥有网络连接或伤害权限，也不把 Unity InstanceID 当跨端身份。
    /// 投射物可能在下一次施法之后才命中；共享根命令的 Scope，才能让爆炸/火场归属原来的 Actor/Command。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatOutputIdentity : MonoBehaviour
    {
        /// <summary>同一根命令的所有层级共用单调 Ordinal；只在离散施法创建，不在每帧分配。</summary>
        public sealed class CommandScope
        {
            public ulong ActorId { get; }
            public ulong CommandId { get; }
            private uint _ordinal;

            public CommandScope(ulong actorId, ulong commandId)
            {
                if (actorId == 0 || commandId == 0) throw new ArgumentException("战斗产出需要非零 Actor/Command。");
                ActorId = actorId; CommandId = commandId;
            }

            internal uint NextOrdinal()
            {
                // 宁可拒绝不可能的超长命令，也不回绕并与早先仍在飞行的产出重名。
                if (_ordinal == uint.MaxValue) throw new InvalidOperationException("单次命令的产出序号已耗尽。");
                return ++_ordinal;
            }
        }

        private CommandScope _scope;
        public ulong ActorId => _scope != null ? _scope.ActorId : 0;
        public ulong CommandId => _scope != null ? _scope.CommandId : 0;

        public static CombatOutputIdentity ConfigureRoot(GameObject output, ulong actorId, ulong commandId) =>
            Configure(output, new CommandScope(actorId, commandId));

        public static CombatOutputIdentity Configure(GameObject output, CommandScope scope)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            CombatOutputIdentity identity = output.GetComponent<CombatOutputIdentity>();
            if (identity == null) identity = output.AddComponent<CombatOutputIdentity>();
            identity._scope = scope;
            return identity;
        }

        public CombatOutputSpawnedEvent CreateSpawnFact(GameObject definitionPrefab)
        {
            if (_scope == null || definitionPrefab == null) throw new InvalidOperationException("未配置的产出不能发布出生事实。");
            return new CombatOutputSpawnedEvent { Output = gameObject, DefinitionPrefab = definitionPrefab,
                ActorId = _scope.ActorId, CommandId = _scope.CommandId, OutputOrdinal = _scope.NextOrdinal() };
        }

        public CombatOutputSpawnedEvent CreateChildFact(GameObject output, GameObject definitionPrefab) =>
            Configure(output, _scope).CreateSpawnFact(definitionPrefab);
    }
}
