using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Game.Core.Tests
{
    /// <summary>使用本 fixture 私有事件，不清生产事件。命令原语与确定事实的异常语义分开验证。</summary>
    public sealed class EventBusFactDeliveryTests
    {
        private struct Fact : IGameEvent { internal int Value; }
        [SetUp] public void StartOwnedEvent() => EventBus<Fact>.Clear();
        [TearDown] public void ReleaseOwnedEvent() => EventBus<Fact>.Clear();

        [Test] public void NoConsumerIsAValidFactNotification()
        { Assert.DoesNotThrow(() => EventBus<Fact>.PublishFact(new Fact { Value = 1 })); }

        [Test] public void AFailedConsumerDoesNotCutOffLaterConsumersOrChangeTheFact()
        {
            var seen = new List<int>();
            EventBus<Fact>.Subscribe(fact => seen.Add(fact.Value));
            EventBus<Fact>.Subscribe(fact => throw new InvalidOperationException("Injected fact consumer failure"));
            EventBus<Fact>.Subscribe(fact => seen.Add(fact.Value + 1));
            EventBus<Fact>.PublishFact(new Fact { Value = 8 });
            CollectionAssert.AreEqual(new[] { 8, 9 }, seen);
        }

        [Test] public void OriginalCommandPublishStillFailsAtTheThrowingConsumer()
        {
            int after = 0;
            EventBus<Fact>.Subscribe(fact => throw new InvalidOperationException("Expected command failure"));
            EventBus<Fact>.Subscribe(fact => after++);
            Assert.Throws<InvalidOperationException>(() => EventBus<Fact>.Publish(default));
            Assert.That(after, Is.Zero);
        }

        [Test] public void DuplicateSubscriptionsAndLastOccurrenceRemovalKeepTheirOriginalMeaning()
        {
            int count = 0; Action<Fact> handler = fact => count++;
            EventBus<Fact>.Subscribe(handler); EventBus<Fact>.Subscribe(handler);
            EventBus<Fact>.PublishFact(default); Assert.That(count, Is.EqualTo(2));
            EventBus<Fact>.Unsubscribe(handler);
            EventBus<Fact>.PublishFact(default); Assert.That(count, Is.EqualTo(3));
            EventBus<Fact>.Unsubscribe(handler);
            EventBus<Fact>.PublishFact(default); Assert.That(count, Is.EqualTo(3));
            Assert.That(EventBus<Fact>.HasSubscribers, Is.False);
        }

        [Test] public void SubscriptionChangesAffectTheNextDeliveryRatherThanTheCurrentSnapshot()
        {
            var seen = new List<string>(); bool changed = false;
            Action<Fact> second = fact => seen.Add("B");
            Action<Fact> third = fact => seen.Add("C");
            EventBus<Fact>.Subscribe(fact =>
            {
                seen.Add("A");
                if (changed) return;
                changed = true; EventBus<Fact>.Unsubscribe(second); EventBus<Fact>.Subscribe(third);
            });
            EventBus<Fact>.Subscribe(second);
            EventBus<Fact>.PublishFact(default);
            CollectionAssert.AreEqual(new[] { "A", "B" }, seen);
            seen.Clear(); EventBus<Fact>.PublishFact(default);
            CollectionAssert.AreEqual(new[] { "A", "C" }, seen);
        }

        [Test] public void ClearingAnOwnedEventReleasesConsumersAndTheirCachedSnapshot()
        {
            int count = 0;
            EventBus<Fact>.Subscribe(fact => count++); EventBus<Fact>.PublishFact(default);
            EventBus<Fact>.Clear(); EventBus<Fact>.PublishFact(default);
            Assert.That(count, Is.EqualTo(1)); Assert.That(EventBus<Fact>.HasSubscribers, Is.False);
            EventBus<Fact>.Subscribe(fact => count += 10); EventBus<Fact>.PublishFact(default);
            Assert.That(count, Is.EqualTo(11));
        }

        [Test] public void ReentrantFactDeliveryKeepsTheCapturedOrder()
        {
            var seen = new List<int>();
            EventBus<Fact>.Subscribe(fact => { seen.Add(fact.Value); if (fact.Value == 1) EventBus<Fact>.PublishFact(new Fact { Value = 2 }); });
            EventBus<Fact>.Subscribe(fact => seen.Add(fact.Value * 10));
            EventBus<Fact>.PublishFact(new Fact { Value = 1 });
            CollectionAssert.AreEqual(new[] { 1, 2, 20, 10 }, seen);
        }
    }
}
