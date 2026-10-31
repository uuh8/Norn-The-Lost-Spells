using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Core.Tests
{
    /// <summary>只测试生产版本栅栏，不用一份测试实现代替它；Reflection让缺失契约先成为明确RED。</summary>
    public sealed class WorldReplicaRevisionStateTests
    {
        private object _state;
        private Type _type;
        [SetUp] public void Create()
        {
            _type = Type.GetType("Game.Core.WorldReplicaRevisionState, Game.Core");
            Assert.That(_type, Is.Not.Null, "N5需要独立于渲染Dirty版本的接收栅栏。");
            _state = Activator.CreateInstance(_type, new object[] { 7UL });
        }
        private bool Baseline(ulong epoch, ulong generation, ulong revision) => Call("TryAcceptBaselineVersion", epoch, generation, revision);
        private bool Delta(ulong epoch, ulong generation, ulong before, ulong after) => Call("TryAcceptDeltaVersion", epoch, generation, before, after);
        private bool Call(string method, params object[] args) => (bool)_type.GetMethod(method).Invoke(_state, args);
        private ulong Number(string property) => (ulong)_type.GetProperty(property).GetValue(_state);
        private string Failure => _type.GetProperty("LastFailure").GetValue(_state).ToString();

        [Test] public void MissingBaselineCannotBeInventedByDelta()
        { Assert.False(Delta(7, 1, 1, 2)); Assert.That(Failure, Is.EqualTo("BaselineRequired")); Assert.That(Number("Revision"), Is.Zero); }
        [Test] public void MissingRevisionPreservesStateAndLegitimateNextDeltaStillApplies()
        {
            Assert.True(Baseline(7, 1, 5)); Assert.False(Delta(7, 1, 9, 1000));
            Assert.That(Failure, Is.EqualTo("MissingRevision")); Assert.That(Number("Revision"), Is.EqualTo(5));
            Assert.True(Delta(7, 1, 5, 6)); Assert.That(Number("Revision"), Is.EqualTo(6));
        }
        [Test] public void OldChunkGenerationIsDiscardedWithoutRewindingRevision()
        { Assert.True(Baseline(7, 3, 10)); Assert.False(Baseline(7, 2, 10000)); Assert.False(Delta(7, 2, 10, 11)); Assert.That(Number("Generation"), Is.EqualTo(3)); Assert.That(Number("Revision"), Is.EqualTo(10)); }
        [Test] public void NewGenerationRequiresBaselineAndMayRestartItsOwnRevision()
        {
            Assert.True(Baseline(7, 2, 100)); Assert.False(Delta(7, 3, 100, 101)); Assert.That(Failure, Is.EqualTo("BaselineRequired"));
            Assert.True(Baseline(7, 3, 1)); Assert.That(Number("Generation"), Is.EqualTo(3)); Assert.True(Delta(7, 3, 1, 2));
        }
        [Test] public void WrongWorldEpochCannotReplaceBoundWorldEvenWithHigherNumbers()
        { Assert.True(Baseline(7, 1, 5)); Assert.False(Baseline(8, ulong.MaxValue, ulong.MaxValue)); Assert.False(Delta(8, 1, 5, 6)); Assert.That(Failure, Is.EqualTo("WrongWorldEpoch")); Assert.True(Delta(7, 1, 5, 6)); }
        [Test] public void DuplicateBaselineIsIdempotentButOldBaselineAndDeltaDoNotReapply()
        { Assert.True(Baseline(7, 1, 5)); Assert.True(Baseline(7, 1, 5)); Assert.False(Baseline(7, 1, 4)); Assert.False(Delta(7, 1, 4, 5)); Assert.That(Number("Revision"), Is.EqualTo(5)); }
        [Test] public void OverlappingDeltaCannotPartiallyOverwriteInstalledState()
        { Assert.True(Baseline(7, 1, 5)); Assert.False(Delta(7, 1, 3, 9)); Assert.That(Failure, Is.EqualTo("MissingRevision")); Assert.That(Number("Revision"), Is.EqualTo(5)); }
        [Test] public void ZeroAndDescendingVersionsDoNotConsumeLegitimateVersion()
        { Assert.False(Baseline(7, 0, 1)); Assert.False(Baseline(7, 1, 0)); Assert.True(Baseline(7, 1, 5)); Assert.False(Delta(7, 1, 5, 0)); Assert.False(Delta(7, 1, 5, 4)); Assert.True(Delta(7, 1, 5, 6)); }
        [Test] public void ExhaustedCountersNeverWrapIntoAnApparentlyNewWorld()
        { Assert.True(Baseline(7, ulong.MaxValue, ulong.MaxValue)); Assert.False(Baseline(7, 1, 1)); Assert.False(Delta(7, ulong.MaxValue, ulong.MaxValue, 1)); Assert.That(Number("Revision"), Is.EqualTo(ulong.MaxValue)); }
    }
}
