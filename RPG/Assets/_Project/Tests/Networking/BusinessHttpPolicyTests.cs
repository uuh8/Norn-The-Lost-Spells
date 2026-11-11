using System;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Networking.Tests
{
    /// <summary>本机允许 HTTP 不应变成任意远端明文传密码；清单摘要要跨端/顺序稳定。</summary>
    public sealed class BusinessHttpPolicyTests
    {
        [Test] public void PracticeContinuityRejectsInvalidGzipWithoutThrowing()
        {
            var value = new Game.NetworkProtocol.OnlinePracticeContinuityEnvelope { PlayerId = new string('a', 32),
                GameplayConfigHash = new string('b', 64), UncompressedBytes = 4,
                Data = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) };
            Assert.That(Game.NetworkProtocol.PracticeContinuityEnvelopeRules.TryExpand(value, value.PlayerId,
                value.GameplayConfigHash, out var raw, out var error), Is.False);
            Assert.That(raw, Is.Null); Assert.That(error, Is.Not.Empty);
        }
        [TestCase("http://127.0.0.1:5080/", true)]
        [TestCase("http://localhost:5080/", true)]
        [TestCase("https://example.com/", true)]
        [TestCase("http://example.com/", false)]
        [TestCase("https://user:password@example.com/", false)]
        [TestCase("https://example.com/?target=evil", false)]
        [TestCase("file:///tmp/data", false)]
        public void CredentialEndpointPolicy(string endpoint, bool allowed) => Assert.That(BusinessHttpClient.IsAllowedBaseUrl(endpoint), Is.EqualTo(allowed));

        [Test]
        public void BusinessHeaderUsesSameApplicationVersionAsJoinHandshake()
        {
            // 只检查真实 Request 构造结果，不发网络请求；避免 HTTP 仍发旧版而 KCP 握手已升新版。
            using var request = BusinessHttpClient.Create("http://127.0.0.1:5080/", "GET", "/api/online/profile", null, null, "manifest");
            Assert.That(request.GetRequestHeader("X-Norn-Protocol"),
                Is.EqualTo(ProtocolCompatibility.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Test]
        public void ManifestHash_IsOrderIndependentAndDetectsContentChanges()
        {
            var a = new ContentManifestEntry { ContentId = "spell.a", Kind = 0, DisplayName = "法术" };
            var b = new ContentManifestEntry { ContentId = "wand.b", Kind = 1, DisplayName = "法杖" };
            Rules(a); Rules(b);
            var manifest = new ContentManifest { BuildVersion = "test", Items = new[] { b, a } };
            string hash = ContentManifestRules.CalculateHash(manifest);
            manifest.Items = new[] { a, b };
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.EqualTo(hash));
            b.DisplayName = "新法杖";
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.Not.EqualTo(hash));
            manifest.Items = new[] { a, a };
            Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(manifest));
        }

        [Test]
        public void Schema3ManifestProtectsTheOriginalShopPricesAndInitialStock()
        {
            var field = typeof(ContentManifest).GetField("Shop");
            Assert.That(field, Is.Not.Null, "受控商店报价必须进入三端内容合同，不能只在客户端本地读取。");
            var item = new ContentManifestEntry { ContentId = "spell.a", Kind = 0, DisplayName = "法术" };
            Rules(item);
            var shop = new ContentShopDefinition { ShopId = "test.shop", PurchaseBudget = 100, RefreshEveryProgress = 1,
                Offers = new[] { new ContentShopOffer { OfferId = "spell.a", ContentId = "spell.a", BuyPrice = 10,
                    SellPrice = 4, InitialStock = 8, InitialCharges = 30, InitialMaterialQuantity = 1 } } };
            shop.Hash = ContentShopRules.CalculateHash(shop);
            var manifest = new ContentManifest { SchemaVersion = 3, BuildVersion = "test", Items = new[] { item } };
            field.SetValue(manifest, shop);
            string original = ContentManifestRules.CalculateHash(manifest);
            shop.Offers[0].BuyPrice = 11; shop.Hash = ContentShopRules.CalculateHash(shop);
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.Not.EqualTo(original), "改价必须使旧端失配，不能沿用旧内容身份。");
            string changedPrice = ContentManifestRules.CalculateHash(manifest);
            shop.Offers[0].InitialStock = 7; shop.Hash = ContentShopRules.CalculateHash(shop);
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.Not.EqualTo(changedPrice));
            shop.Hash = original;
            Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(manifest), "不能接受报价正文与其Hash不一致的配置。");
        }

        [Test]
        public void Schema2ManifestCannotSmuggleAnUnhashedShopSection()
        {
            var field = typeof(ContentManifest).GetField("Shop");
            Assert.That(field, Is.Not.Null);
            var item = new ContentManifestEntry { ContentId = "spell.a", Kind = 0, DisplayName = "法术" };
            Rules(item);
            var manifest = new ContentManifest { SchemaVersion = 2, BuildVersion = "test", Items = new[] { item } };
            string original = ContentManifestRules.CalculateHash(manifest);
            field.SetValue(manifest, new ContentShopDefinition());
            Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(manifest));
            field.SetValue(manifest, null);
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.EqualTo(original), "无商店的历史Schema2字节语义保持不变。");
        }

        [Test]
        public void PracticeContinuityEnvelopePreservesBytesAndRejectsForeignOrFalselySizedState()
        {
            var type = typeof(ContentManifest).Assembly.GetType("Game.NetworkProtocol.PracticeContinuityEnvelopeRules");
            Assert.That(type, Is.Not.Null, "和平连续性须有独立有界压缩壳，不能把Client资产整档上传当恢复。");
            var pack = type.GetMethod("TryPack"); var expand = type.GetMethod("TryExpand");
            Assert.That(pack, Is.Not.Null); Assert.That(expand, Is.Not.Null);
            string player = new string('1', 32), hash = new string('a', 64);
            string canonical = "{\"SchemaVersion\":1,\"Marker\":\"研究原值\",\"Data\":\"" + new string('x', 16384) + "\"}";
            var encoded = new object[] { player, hash, canonical, null, null };
            Assert.That((bool)pack.Invoke(null, encoded), Is.True, encoded[4] as string);
            var envelope = encoded[3];
            var decoded = new object[] { envelope, player, hash, null, null };
            Assert.That((bool)expand.Invoke(null, decoded), Is.True, decoded[4] as string);
            Assert.That(decoded[3], Is.EqualTo(canonical));
            decoded[1] = new string('2', 32);
            Assert.That((bool)expand.Invoke(null, decoded), Is.False, "别人的压缩状态不能成为当前玩家的资源起点。");
            decoded[1] = player;
            envelope.GetType().GetField("UncompressedBytes").SetValue(envelope, 2);
            Assert.That((bool)expand.Invoke(null, decoded), Is.False, "实际流大于声明时必须在写入前停止，不能信任gzip声明分配巨量内存。");
            Assert.That(decoded[3], Is.Null);
        }

        private static void Rules(ContentManifestEntry item)
        {
            // 这是完整的测试目录，不为生产的旧身份清单隐式补权威规则。
            item.RulesSchemaVersion = 1; item.ShapeWidth = item.ShapeHeight = item.MaxStack = 1;
            item.OccupiedCells = new[] { new ContentShapeCell { X = 0, Y = 0 } };
            item.BackpackWidth = item.BackpackHeight = item.WandProgramCapacity = 1;
            item.ContainerCapacityGmu = item.MaterialItemCapacityGmu = 1024;
            item.ContainerMaterialsMask = 410; item.MaterialKey = 5;
        }

        [Test]
        public void ExportedRulesMatchExistingAuthoringShapesAndContainerCapacity()
        {
            var exporter = Type.GetType("Game.Project.Editor.ContentManifestExporter, Game.Project.Editor");
            Assert.That(exporter, Is.Not.Null);
            var manifest = (ContentManifest)exporter.GetMethod("CreateManifest").Invoke(null, null);
            Assert.That(manifest.SchemaVersion, Is.EqualTo(3));
            Assert.That(manifest.Shop, Is.Not.Null, "正式商店规则必须与物品规则一起受控导出。");
            Assert.That(manifest.Shop.Hash, Is.EqualTo(ContentShopRules.CalculateHash(manifest.Shop)));
            string[] roots = { "Assets/_Project/ScriptableObjects/Spell/Items", "Assets/_Project/ScriptableObjects/Spell/Wands/Items", "Assets/_Project/ScriptableObjects/Items" };
            int matched = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDefinition", roots))
            {
                var source = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
                var data = new SerializedObject(source);
                string id = data.FindProperty("_contentId").stringValue;
                ContentManifestEntry entry = Array.Find(manifest.Items, x => x.ContentId == id);
                Assert.That(entry, Is.Not.Null, id);
                Assert.That(entry.RulesSchemaVersion, Is.EqualTo(1), id);
                Assert.That(entry.ShapeWidth, Is.EqualTo(data.FindProperty("_shapeWidth").intValue), id);
                Assert.That(entry.ShapeHeight, Is.EqualTo(data.FindProperty("_shapeHeight").intValue), id);
                var cells = data.FindProperty("_occupiedCells");
                Assert.That(entry.OccupiedCells.Length, Is.EqualTo(cells.arraySize), id);
                for (int i = 0; i < cells.arraySize; i++)
                {
                    Vector2Int cell = cells.GetArrayElementAtIndex(i).vector2IntValue;
                    Assert.That(Array.Exists(entry.OccupiedCells, p => p.X == cell.x && p.Y == cell.y), Is.True, id);
                }
                Assert.That(entry.ContainerCapacityGmu, Is.EqualTo((ulong)data.FindProperty("_containerCapacityGmu").intValue), id);
                matched++;
            }
            Assert.That(matched, Is.EqualTo(manifest.Items.Length).And.GreaterThan(0));
            Assert.That(ContentManifestRules.CalculateHash(manifest), Is.EqualTo(manifest.Hash));
        }
    }
}
