using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class UserSettingsValuesTests
    {
        [Test]
        public void Sanitize_ClampsVolumeAndSensitivityToSupportedRanges()
        {
            var below = new UserSettingsValues(-1f, -10f, false).Sanitize();
            var above = new UserSettingsValues(5f, 100f, true).Sanitize();

            Assert.That(below.MasterVolume, Is.EqualTo(UserSettingsValues.MinMasterVolume));
            Assert.That(below.MouseSensitivity, Is.EqualTo(UserSettingsValues.MinMouseSensitivity));
            Assert.That(below.Fullscreen, Is.False);
            Assert.That(above.MasterVolume, Is.EqualTo(UserSettingsValues.MaxMasterVolume));
            Assert.That(above.MouseSensitivity, Is.EqualTo(UserSettingsValues.MaxMouseSensitivity));
            Assert.That(above.Fullscreen, Is.True);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Sanitize_NonFiniteValuesFallBackToDefaults(float invalid)
        {
            var values = new UserSettingsValues(invalid, invalid, false).Sanitize();

            Assert.That(values.MasterVolume, Is.EqualTo(UserSettingsValues.DefaultMasterVolume));
            Assert.That(values.MouseSensitivity, Is.EqualTo(UserSettingsValues.DefaultMouseSensitivity));
            Assert.That(values.Fullscreen, Is.False);
        }

        [Test]
        public void Equality_ComparesTheWholeSnapshot()
        {
            var left = new UserSettingsValues(0.5f, 1.25f, true);
            var same = new UserSettingsValues(0.5f, 1.25f, true);
            var different = new UserSettingsValues(0.5f, 1.25f, false);

            Assert.That(left, Is.EqualTo(same));
            Assert.That(left == same, Is.True);
            Assert.That(left != different, Is.True);
        }
    }
}
