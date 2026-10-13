#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WorldItemSpawnTests
    {
        [Test] public void SceneAuthoringHasDefinitionAndStableSpawnIdentity()
        {
            var type=typeof(WorldItemPickup).Assembly.GetType("Game.Run.WorldItemSpawnPoint");
            Assert.That(type,Is.Not.Null,"场景物品必须注册真实实例，不能只是美术Prefab。");
            Assert.That(type.GetProperty("SpawnId"),Is.Not.Null);
            Assert.That(typeof(ItemDefinition).GetProperty("WorldModel"),Is.Not.Null);
            Assert.That(typeof(SinglePlayerSaveDto).GetField("HubWorldItems"),Is.Not.Null,"生成记录必须和Profile一起可靠保存。");
        }
    }
}
#endif
