using System;
using System.IO;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WandV2UpgradeBackupTests : InventoryTestFixture
    {
        [TestCase(false)][TestCase(true)] public void SavingUpgradeRetainsOriginalSchema13BeyondRollingBackup(bool recovered)
        {
            string root = Path.GetFullPath("Temp/Spells/UpgradeTests") + Path.DirectorySeparatorChar;
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var store = Store(Define("wand", ItemKind.Wand));
                Assert.That(store.TryAdd(new RuntimeItemState("original", "wand", 0, rolledWandStats: new WandStats(222, 17, .42f, 6, 2)), ItemLocation.Warehouse), Is.True);
                var dto = ProfileSaveMapper.ToDto(new SinglePlayerProfile("player", store), 1); dto.SchemaVersion = 13;
                string original = JsonUtility.ToJson(dto); File.WriteAllText(Path.Combine(directory, recovered?"profile.backup.json":"profile.json"), original);
                if(recovered)File.WriteAllText(Path.Combine(directory,"profile.json"),"{ corrupt");
                var repository = new ProfileSaveRepository(directory, store.Catalog);
                Assert.That(repository.TryLoad(out var profile).Success, Is.True); Assert.That(repository.TrySave(profile).Success, Is.True);
                Assert.That(repository.TrySave(profile).Success, Is.True);
                string backup = Path.Combine(directory, "profile.schema13.original.json");
                Assert.That(File.Exists(backup), Is.True, "滚动backup会被第二次保存覆盖，迁移前原件需要独立保留");
                Assert.That(File.ReadAllText(backup), Is.EqualTo(original));
                Assert.That(JsonUtility.FromJson<SinglePlayerSaveDto>(File.ReadAllText(repository.MainPath)).SchemaVersion, Is.EqualTo(14));
            }
            finally { if (directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
