using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Game.Combat;

namespace Game.Combat.Tests
{
    public class ManaComponentTests
    {
        [UnityTest]
        public IEnumerator Spend_WhenEnoughMana_DecreasesCurrentMana()
        {
            yield return new EnterPlayMode();

            GameObject go = new GameObject("mana-test");
            ManaComponent mana = go.AddComponent<ManaComponent>();

            float initialMana = mana.CurrentMana;
            bool spent = mana.Spend(30f);
            float remainingMana = mana.CurrentMana;

            Object.Destroy(go);
            yield return null;
            yield return new ExitPlayMode();

            Assert.AreEqual(100f, initialMana, 1e-4f);
            Assert.IsTrue(spent);
            Assert.AreEqual(70f, remainingMana, 1e-4f);
        }

        [UnityTest]
        public IEnumerator Spend_WhenInsufficientMana_ReturnsFalseAndKeepsCurrentMana()
        {
            yield return new EnterPlayMode();

            GameObject go = new GameObject("mana-test");
            ManaComponent mana = go.AddComponent<ManaComponent>();

            bool spent = mana.Spend(130f);
            float remainingMana = mana.CurrentMana;

            Object.Destroy(go);
            yield return null;
            yield return new ExitPlayMode();

            Assert.IsFalse(spent);
            Assert.AreEqual(100f, remainingMana, 1e-4f);
        }
    }
}
