using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class BossTeleportCandidateGeneratorTests
    {
        [Test]
        public void Generate_FillsRingWithinRadiusAndPreservesCenterHeight()
        {
            var output = new Vector3[12];
            var center = new Vector3(3f, 7f, -4f);

            int count = BossTeleportCandidateGenerator.Generate(
                center,
                6f,
                12f,
                15f,
                output);

            Assert.That(count, Is.EqualTo(output.Length));
            for (int i = 0; i < count; i++)
            {
                Vector3 horizontal = output[i] - center;
                Assert.That(output[i].y, Is.EqualTo(center.y).Within(0.0001f));
                Assert.That(horizontal.y, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(horizontal.magnitude, Is.GreaterThanOrEqualTo(6f));
                Assert.That(horizontal.magnitude, Is.LessThanOrEqualTo(12f));
            }
        }

        [Test]
        public void Generate_WithSameInputs_IsDeterministic()
        {
            var first = new Vector3[8];
            var second = new Vector3[8];
            var center = new Vector3(-2f, 1f, 5f);

            BossTeleportCandidateGenerator.Generate(
                center,
                4f,
                9f,
                73f,
                first);
            BossTeleportCandidateGenerator.Generate(
                center,
                4f,
                9f,
                73f,
                second);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(second[i], Is.EqualTo(first[i]));
            }
        }

        [Test]
        public void Generate_WithDifferentAngleOffset_RotatesFirstCandidate()
        {
            var first = new Vector3[4];
            var rotated = new Vector3[4];

            BossTeleportCandidateGenerator.Generate(
                Vector3.zero,
                5f,
                5f,
                0f,
                first);
            BossTeleportCandidateGenerator.Generate(
                Vector3.zero,
                5f,
                5f,
                90f,
                rotated);

            Assert.That(first[0].x, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(first[0].z, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(rotated[0].x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(rotated[0].z, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void Generate_WithMissingOutput_ReturnsZero()
        {
            Assert.That(
                BossTeleportCandidateGenerator.Generate(
                    Vector3.zero,
                    1f,
                    2f,
                    0f,
                    null),
                Is.Zero);
            Assert.That(
                BossTeleportCandidateGenerator.Generate(
                    Vector3.zero,
                    1f,
                    2f,
                    0f,
                    System.Array.Empty<Vector3>()),
                Is.Zero);
        }
    }
}
