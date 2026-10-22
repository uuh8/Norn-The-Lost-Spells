using System;
using Game.NetworkProtocol;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>本机允许 HTTP 不应变成任意远端明文传密码；清单摘要要跨端/顺序稳定。</summary>
    public sealed class BusinessHttpPolicyTests
    {
        [TestCase("http://127.0.0.1:5080/", true)]
        [TestCase("http://localhost:5080/", true)]
        [TestCase("https://example.com/", true)]
        [TestCase("http://example.com/", false)]
        [TestCase("https://user:password@example.com/", false)]
        [TestCase("https://example.com/?target=evil", false)]
        [TestCase("file:///tmp/data", false)]
        public void CredentialEndpointPolicy(string endpoint, bool allowed) => Assert.That(BusinessHttpClient.IsAllowedBaseUrl(endpoint), Is.EqualTo(allowed));

        [Test]
        public void ManifestHash_IsOrderIndependentAndDetectsContentChanges()
        {
            var a = new ContentManifestEntry { ContentId = "spell.a", Kind = 0, DisplayName = "法术" };
            var b = new ContentManifestEntry { ContentId = "wand.b", Kind = 1, DisplayName = "法杖" };
            var manifest = new ContentManifest { BuildVersion = "test", Items = new[] { b, a } };
            string hash = ContentManifestRules.CalculateHash(manifest);
            manifest.Items = new[] { a, b };
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.EqualTo(hash));
            b.DisplayName = "新法杖";
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.Not.EqualTo(hash));
            manifest.Items = new[] { a, a };
            Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(manifest));
        }
    }
}
