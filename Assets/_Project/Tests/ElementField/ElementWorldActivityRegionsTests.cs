using NUnit.Framework;
using Game.Core;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldActivityRegionsTests
    {
        [Test]
        public void SeparateSourcesDoNotActivateTheGap()
        {
            var regions = new ElementWorldActivityRegions(4);
            Assert.That(regions.TryAdd(new Bounds(Vector3.zero, Vector3.one * 4)), Is.True);
            Assert.That(regions.TryAdd(new Bounds(Vector3.right * 100, Vector3.one * 4)), Is.True);
            Assert.That(regions.Contains(Vector3.zero), Is.True);
            Assert.That(regions.Contains(Vector3.right * 100), Is.True);
            Assert.That(regions.Contains(Vector3.right * 50), Is.False);
            Assert.That(regions.Envelope.Contains(Vector3.right * 50), Is.True);
        }

        [Test]
        public void HalfOpenBoundsExcludeMaximumButIncludeMinimum()
        {
            var regions = new ElementWorldActivityRegions(2);
            regions.TryAdd(new Bounds(Vector3.zero, Vector3.one * 4));
            Assert.That(regions.Contains(new Vector3(-2, 0, 0)), Is.True);
            Assert.That(regions.Contains(new Vector3(2, 0, 0)), Is.False);
        }

        [Test]
        public void DuplicateDoesNotSpendCapacityAndOverflowPreservesExistingRegions()
        {
            var regions = new ElementWorldActivityRegions(1);
            var first = new Bounds(Vector3.zero, Vector3.one);
            Assert.That(regions.TryAdd(first), Is.True);
            Assert.That(regions.TryAdd(first), Is.True);
            Assert.That(regions.TryAdd(new Bounds(Vector3.right * 10, Vector3.one)), Is.False);
            Assert.That(regions.Count, Is.EqualTo(1));
            Assert.That(regions.RejectedCount, Is.EqualTo(1));
            Assert.That(regions.Contains(Vector3.zero), Is.True);
            regions.Clear();
            Assert.That(regions.Count, Is.Zero);
            Assert.That(regions.Contains(Vector3.zero), Is.False);
        }

        [Test]
        public void FrozenCopySurvivesSourceMovement()
        {
            var source = new ElementWorldActivityRegions(2);
            var frozen = new ElementWorldActivityRegions(2);
            source.TryAdd(new Bounds(Vector3.zero, Vector3.one * 4));
            frozen.CopyFrom(source);
            source.Clear();
            source.TryAdd(new Bounds(Vector3.right * 100, Vector3.one * 4));
            Assert.That(frozen.Contains(Vector3.zero), Is.True);
            Assert.That(frozen.Contains(Vector3.right * 100), Is.False);
        }

        [Test]
        public void InvalidCoordinatesAreRejectedBeforePublishing()
        {
            var regions = new ElementWorldActivityRegions(2);
            Assert.That(regions.TryAdd(new Bounds(new Vector3(float.NaN, 0, 0), Vector3.one)), Is.False);
            Assert.That(regions.TryAdd(new Bounds(Vector3.zero, Vector3.zero)), Is.False);
            Assert.That(regions.Count, Is.Zero);
        }

        [Test]
        public void SourceLifecycleIsIdempotentAndDestroyedTransformIsPruned()
        {
            var sources = new ElementWorldActivitySources();
            var regions = new ElementWorldActivityRegions(4);
            var source = new GameObject("Activity Source");
            try
            {
                var enabled = new WorldActivitySourceEvent
                {
                    Source = source.transform,
                    Kind = WorldActivitySourceKind.Unit,
                    Enabled = true
                };
                sources.Apply(enabled);
                sources.Apply(enabled);
                Assert.That(sources.Count, Is.EqualTo(1));

                enabled.Enabled = false;
                sources.Apply(enabled);
                Assert.That(sources.Count, Is.Zero);

                enabled.Enabled = true;
                sources.Apply(enabled);
                Object.DestroyImmediate(source);
                source = null;
                Assert.That(sources.AppendRegions(regions, WorldActivitySourceKind.Unit,
                    2f, 4, Vector3.zero, 2f), Is.Zero);
                Assert.That(sources.Count, Is.Zero);
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void SourceBudget_RotatesStartSoLaterSourcesAreNotPermanentlyStarved()
        {
            var sources = new ElementWorldActivitySources();
            var objects = new GameObject[3];
            try
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    objects[i] = new GameObject("source-" + i);
                    objects[i].transform.position = Vector3.right * (i * 16f);
                    sources.Apply(new WorldActivitySourceEvent
                    {
                        Source = objects[i].transform,
                        Kind = WorldActivitySourceKind.Unit,
                        Enabled = true,
                    });
                }
                var first = new ElementWorldActivityRegions();
                var second = new ElementWorldActivityRegions();

                sources.AppendRegions(first, WorldActivitySourceKind.Unit, 1f, 1,
                    Vector3.zero, 1f);
                sources.AppendRegions(second, WorldActivitySourceKind.Unit, 1f, 1,
                    Vector3.zero, 1f);

                Assert.That(first.Contains(objects[0].transform.position), Is.True);
                Assert.That(second.Contains(objects[1].transform.position), Is.True);
            }
            finally
            {
                for (int i = 0; i < objects.Length; i++)
                    if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            }
        }

        [Test]
        public void SourceBudget_ReportsCurrentlySkippedSourcesPerKind()
        {
            var sources = new ElementWorldActivitySources();
            var objects = new GameObject[5];
            try
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    objects[i] = new GameObject("source-" + i);
                    objects[i].transform.position = Vector3.right * (i * 16f);
                    sources.Apply(new WorldActivitySourceEvent
                    {
                        Source = objects[i].transform,
                        Kind = i < 3 ? WorldActivitySourceKind.Unit : WorldActivitySourceKind.Projectile,
                        Enabled = true,
                    });
                }

                var regions = new ElementWorldActivityRegions();
                sources.AppendRegions(regions, WorldActivitySourceKind.Unit, 1f, 2,
                    Vector3.zero, 1f);
                sources.AppendRegions(regions, WorldActivitySourceKind.Projectile, 1f, 1,
                    Vector3.zero, 1f);

                Assert.That(sources.UnitBudgetSkippedCount, Is.EqualTo(1));
                Assert.That(sources.ProjectileBudgetSkippedCount, Is.EqualTo(1));
            }
            finally
            {
                for (int i = 0; i < objects.Length; i++)
                    if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            }
        }
    }
}
