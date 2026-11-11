using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.NetworkProtocol;
using Game.Run;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>从正式物品定义导出身份清单；不读取玩家 Profile，业务服也不需要加载 Unity SO。</summary>
    public sealed class ContentManifestExporter : IPreprocessBuildWithReport
    {
        public const string Output = "Assets/_Project/Resources/Online/ContentManifest.json";
        private const string ShopSource = "Assets/_Project/ScriptableObjects/World/Hub/Trading/Shop.asset";
        public int callbackOrder => 0;

        [MenuItem("Tools/Norn/Online/Export Content Manifest")]
        public static void Export()
        {
            OnlineSceneSetup.RequireCleanEditor();
            ContentManifest manifest = CreateManifest();
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            File.WriteAllText(Output, JsonUtility.ToJson(manifest, true));
            Directory.CreateDirectory("Tools/Networking/.artifacts");
            File.WriteAllText("Tools/Networking/.artifacts/content-manifest.json", JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(Output);
            GameLog.Info("物品身份清单已导出：" + manifest.Items.Length + " 项，Hash=" + manifest.Hash, "OnlineContent");
        }

        public static ContentManifest CreateManifest()
        {
            // Compatibility/Tests 不属于正式物品输入；不能用全工程 FindAssets 把旧 GUID 副本算成新配置。
            string[] roots = { "Assets/_Project/ScriptableObjects/Spell/Items", "Assets/_Project/ScriptableObjects/Spell/Wands/Items", "Assets/_Project/ScriptableObjects/Items" };
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition", roots);
            var entries = new List<ContentManifestEntry>(guids.Length);
            var definitions = new List<ItemDefinition>(guids.Length);
            foreach (string guid in guids)
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                definitions.Add(definition);
            }
            // 复用 Run 的公开 Catalog 冻结入口，不能在 Editor 另抄一套 SO 解释规则。
            // 临时目录只在内存中存在，既不保存资产，也不修改作者的正式 Catalog/ContentId。
            var catalog = ScriptableObject.CreateInstance<ItemCatalog>();
            ContentShopDefinition shop = null;
            try
            {
                var serialized = new SerializedObject(catalog);
                var array = serialized.FindProperty("_definitions"); array.arraySize = definitions.Count;
                for (int i = 0; i < definitions.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!catalog.TryCreateIndex(out var index, out string error)) throw new InvalidOperationException(error);
                foreach (var definition in definitions)
                {
                    if (!index.TryGetRules(definition.ContentId, out var rules)) throw new InvalidOperationException("目录缺少真实物品规则。");
                    var cells = new ContentShapeCell[rules.Shape.CellCount];
                    for (int i = 0; i < cells.Length; i++)
                    {
                        Vector2Int cell = rules.Shape.GetCell(i, 0);
                        cells[i] = new ContentShapeCell { X = cell.x, Y = cell.y };
                    }
                    entries.Add(new ContentManifestEntry
                    {
                        ContentId = definition.ContentId, Kind = (int)rules.Kind, DisplayName = definition.DisplayName,
                        RulesSchemaVersion = 1, UseKind = (int)rules.UseKind,
                        ShapeWidth = rules.Shape.Width, ShapeHeight = rules.Shape.Height, MaxStack = rules.MaxStack, OccupiedCells = cells,
                        BackpackWidth = rules.BackpackWidth, BackpackHeight = rules.BackpackHeight, WandProgramCapacity = rules.WandProgramCapacity,
                        ContainerCapacityGmu = rules.ContainerCapacity, ContainerMaterialsMask = rules.ContainerMaterials,
                        MaterialKey = rules.MaterialKey, MaterialItemCapacityGmu = rules.MaterialItemCapacity
                    });
                }
                // 复用正式商店的冻结入口验证原报价；只导出纯值，不把Sprite/Unity SO交给业务服。
                // 商店规则与物品身份进入同一个版本摘要，单改价格或初始次数也会拒绝旧端混用。
                var originalShop = AssetDatabase.LoadAssetAtPath<ShopDefinition>(ShopSource);
                if (originalShop == null || !originalShop.TryFreeze(index, out _))
                    throw new InvalidOperationException("正式商店报价未能按真实Catalog冻结，保留已有导出。");
                shop = new ContentShopDefinition { ShopId = originalShop.ShopId, PurchaseBudget = originalShop.PurchaseBudget,
                    RefreshEveryProgress = originalShop.RefreshEveryProgress, Offers = new ContentShopOffer[originalShop.Offers.Length] };
                for (int i = 0; i < shop.Offers.Length; i++)
                {
                    var offer = originalShop.Offers[i];
                    shop.Offers[i] = new ContentShopOffer { OfferId = offer.OfferId, ContentId = offer.ContentId ?? string.Empty,
                        MaterialKey = offer.MaterialKey, BuyPrice = offer.BuyPrice, SellPrice = offer.SellPrice,
                        InitialStock = offer.Stock, InitialCharges = offer.InitialCharges, Unlimited = offer.Unlimited,
                        InitialMaterialQuantity = offer.InitialMaterialQuantity };
                }
                shop.Hash = ContentShopRules.CalculateHash(shop);
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); }
            entries.Sort((a, b) => string.CompareOrdinal(a.ContentId, b.ContentId));
            var manifest = new ContentManifest { SchemaVersion = 3, BuildVersion = Application.version,
                ProtocolVersion = ProtocolCompatibility.CurrentVersion, Items = entries.ToArray(), Shop = shop };
            manifest.Hash = ContentManifestRules.CalculateHash(manifest);
            return manifest;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!File.Exists(Output)) return; // N0 老实验没有业务内容依赖；导出后所有 Client/Server 构建都核对。
            var saved = JsonUtility.FromJson<ContentManifest>(File.ReadAllText(Output));
            if (saved == null || saved.Hash != ContentManifestRules.CalculateHash(saved) || saved.Hash != CreateManifest().Hash)
                throw new BuildFailedException("ContentManifest 已过期或损坏，请先 Export Content Manifest。");
        }
    }
}
