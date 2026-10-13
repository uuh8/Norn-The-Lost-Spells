using System;
using System.IO;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>真实临时文件与故障边界；不读写正式玩家存档或 Unity Scene。</summary>
    public sealed class SinglePlayerProfileSaveTests : InventoryTestFixture
    {
        private string _directory;
        private ItemOwnershipStore _store;
        private SinglePlayerProfile _profile;

        [SetUp]
        public void SetupProfile()
        {
            _directory = Path.GetFullPath("Temp/SinglePlayer/ProfileTests/" + Guid.NewGuid().ToString("N"));
            _store = Store(Define("bag", ItemKind.Backpack, bagWidth: 4, bagHeight: 3),
                Define("spell", ItemKind.Spell, maxStack: 2), Define("bar", ItemKind.Container, 2, 1));
            Add(_store, "bag", "bag"); Add(_store, "a", "spell", 3); Add(_store, "b", "spell", 8); Add(_store, "bar", "bar");
            Assert.That(_store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(_store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(_store.TryMove("bar", Bag(2, 1, 1)).Success, Is.True);
            _profile = new SinglePlayerProfile("player", _store);
        }

        [TestCase(false,false)]
        [TestCase(true,false)]
        [TestCase(false,true)]
        [TestCase(true,true)]
        public void LegacyContainerStackRestoresEveryIdentityAndComposition(bool full,bool filled)
        {
            Add(_store,"bar.extra","bar");
            if(filled)
            {
                _store.TryGet("bar",out var firstBottle);_store.TryGet("bar.extra",out var secondBottle);
                var water=new[]{new MaterialAmount(1,27)};
                firstBottle.Composition.AddReserved(water,1);secondBottle.Composition.AddReserved(water,1);
            }
            if(full)for(int y=0;y<3;y++)for(int x=0;x<4;x++)
                _store.TryAdd(new RuntimeItemState("fill."+x+"."+y,"spell",1),Bag(x,y));
            var dto=ProfileSaveMapper.ToDto(_profile,1);dto.SchemaVersion=9;
            SinglePlayerItemDto original=null,extra=null;
            foreach(var item in dto.Items){if(item.InstanceId=="bar")original=item;if(item.InstanceId=="bar.extra")extra=item;}
            extra.LocationKind=original.LocationKind;extra.OwnerId=original.OwnerId;extra.X=original.X;extra.Y=original.Y;extra.QuarterTurns=original.QuarterTurns;extra.SlotIndex=original.SlotIndex;
            Assert.That(ProfileSaveMapper.TryRestore(dto,_store.Catalog,out var restored).Success,Is.True);
            restored.Items.TryGetLocation("bar",out var first);restored.Items.TryGetLocation("bar.extra",out var second);
            Assert.That(first,Is.EqualTo(Bag(2,1,1)));
            Assert.That(second,Is.Not.EqualTo(first));
            Assert.That(second.Kind,Is.EqualTo(full?ItemLocationKind.Warehouse:ItemLocationKind.Backpack));
            Assert.That(restored.Items.Count,Is.EqualTo(_store.Count));
            restored.Items.TryGet("bar.extra",out var bottle);Assert.That(bottle.Composition.Total,Is.EqualTo(filled?27:0));
            restored.Items.TryGet("bar",out var originalBottle);Assert.That(originalBottle.Composition.Total,Is.EqualTo(filled?27:0));
        }

        [Test] public void CurrentSchemaDoesNotRepairNewInvalidContainerOverlap()
        {
            Assert.That(ProfileSaveMapper.CurrentSchema,Is.GreaterThan(9));
            Add(_store,"bar.extra","bar");var dto=ProfileSaveMapper.ToDto(_profile,1);
            SinglePlayerItemDto original=null,extra=null;
            foreach(var item in dto.Items){if(item.InstanceId=="bar")original=item;if(item.InstanceId=="bar.extra")extra=item;}
            extra.LocationKind=original.LocationKind;extra.OwnerId=original.OwnerId;extra.X=original.X;extra.Y=original.Y;extra.QuarterTurns=original.QuarterTurns;extra.SlotIndex=original.SlotIndex;
            Assert.That(ProfileSaveMapper.TryRestore(dto,_store.Catalog,out _).Success,Is.False);
        }

        [TearDown]
        public void CleanupFiles()
        {
            // 只删除本测试 Guid 目录；完整绝对路径必须仍在工作区的专用测试根下。
            string root = Path.GetFullPath("Temp/SinglePlayer/ProfileTests") + Path.DirectorySeparatorChar;
            if (_directory != null && _directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        [Test]
        public void SaveReload_PreservesDuplicateIdentitiesChargesAndRotation()
        {
            var repository = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            var reopen = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(reopen.TryLoad(out SinglePlayerProfile restored).Success, Is.True);
            Assert.That(restored.SaveGeneration, Is.EqualTo(1));
            Assert.That(restored.Items.Count, Is.EqualTo(4));
            restored.Items.TryGet("a", out RuntimeItemState a); restored.Items.TryGet("b", out RuntimeItemState b);
            Assert.That(a.RemainingCharges, Is.EqualTo(3)); Assert.That(b.RemainingCharges, Is.EqualTo(8));
            At(restored.Items, "bar", Bag(2, 1, 1)); At(restored.Items, "b", ItemLocation.Warehouse);
            Assert.That(restored.ActorId, Is.EqualTo("player"));
        }

        [Test]
        public void Mapper_ChildrenBeforeParentRestoresCompleteGraph()
        {
            SinglePlayerSaveDto dto = ProfileSaveMapper.ToDto(_profile, 4);
            Array.Reverse(dto.Items);
            Assert.That(ProfileSaveMapper.TryRestore(dto, _store.Catalog, out SinglePlayerProfile restored).Success, Is.True);
            At(restored.Items, "a", Bag()); Assert.That(restored.SaveGeneration, Is.EqualTo(4));
        }

        [Test]
        public void SubsequentSave_CreatesValidPreviousCommitBackup()
        {
            var repository = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            string previous = File.ReadAllText(repository.MainPath);
            Assert.That(_store.TryMove("b", Bag(1)).Success, Is.True);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            Assert.That(File.ReadAllText(repository.BackupPath), Is.EqualTo(previous));
            Assert.That(_profile.SaveGeneration, Is.EqualTo(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InterruptedTempWriteOrReplace_PreservesLastCommitAndGeneration(bool failWrite)
        {
            var files = new FaultingFiles();
            var repository = new ProfileSaveRepository(_directory, _store.Catalog, files);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            string committed = File.ReadAllText(repository.MainPath);
            files.FailWrite = failWrite; files.FailReplace = !failWrite;
            Assert.That(_store.TryMove("b", Bag(1)).Success, Is.True);
            Assert.That(repository.TrySave(_profile).Status, Is.EqualTo(ProfilePersistenceStatus.IoError));
            Assert.That(File.ReadAllText(repository.MainPath), Is.EqualTo(committed));
            Assert.That(_profile.SaveGeneration, Is.EqualTo(1));
        }

        [TestCase(true, ProfilePersistenceStatus.UnsupportedSchema)]
        [TestCase(false, ProfilePersistenceStatus.UnknownContent)]
        public void UnknownSchemaOrDefinition_DoesNotFallbackOrOverwriteOriginal(bool schema, ProfilePersistenceStatus expected)
        {
            var repository = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            Assert.That(repository.TrySave(_profile).Success, Is.True);
            SinglePlayerSaveDto dto = ProfileSaveMapper.ToDto(_profile, 2);
            if (schema) dto.SchemaVersion = 99; else dto.Items[0].ContentId = "removed-content";
            string unknown = UnityEngine.JsonUtility.ToJson(dto);
            File.WriteAllText(repository.MainPath, unknown);
            var reader = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(reader.TryLoad(out SinglePlayerProfile restored).Status, Is.EqualTo(expected));
            Assert.That(restored, Is.Null);
            Assert.That(reader.TrySave(_profile).Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
            Assert.That(File.ReadAllText(repository.MainPath), Is.EqualTo(unknown));
        }

        [Test]
        public void ExistingFileMustBeLoadedBeforeAnyOverwrite()
        {
            var writer = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(writer.TrySave(_profile).Success, Is.True);
            string before = File.ReadAllText(writer.MainPath);
            var unread = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(unread.TrySave(_profile).Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
            Assert.That(File.ReadAllText(writer.MainPath), Is.EqualTo(before));
        }

        [Test]
        public void CorruptPrimary_LoadsBackup_AndPreservesCorruptFileOnNextSave()
        {
            var writer = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(writer.TrySave(_profile).Success, Is.True);
            Assert.That(writer.TrySave(_profile).Success, Is.True);
            string backup = File.ReadAllText(writer.BackupPath);
            File.WriteAllText(writer.MainPath, "broken-json");
            var reader = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(reader.TryLoad(out SinglePlayerProfile restored).Status, Is.EqualTo(ProfilePersistenceStatus.RecoveredBackup));
            Assert.That(restored.SaveGeneration, Is.EqualTo(1));
            Assert.That(reader.TrySave(restored).Success, Is.True);
            Assert.That(File.ReadAllText(reader.BackupPath), Is.EqualTo(backup));
            string[] rejected = Directory.GetFiles(_directory, "profile.corrupt.*.json");
            Assert.That(rejected.Length, Is.EqualTo(1)); Assert.That(File.ReadAllText(rejected[0]), Is.EqualTo("broken-json"));
        }

        [Test]
        public void MissingPrimaryButValidBackup_DoesNotResetToStarterAssets()
        {
            var writer = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(writer.TrySave(_profile).Success, Is.True); Assert.That(writer.TrySave(_profile).Success, Is.True);
            File.Delete(writer.MainPath);
            var reader = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(reader.TryLoad(out SinglePlayerProfile restored).Status, Is.EqualTo(ProfilePersistenceStatus.RecoveredBackup));
            Assert.That(restored.Items.Count, Is.EqualTo(4));
        }

        [Test]
        public void CorruptPrimaryAndBackup_BlockNewSaveAndKeepBothFiles()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "profile.json"), "broken-main");
            File.WriteAllText(Path.Combine(_directory, "profile.backup.json"), "broken-backup");
            var repository = new ProfileSaveRepository(_directory, _store.Catalog);
            Assert.That(repository.TryLoad(out _).Success, Is.False);
            Assert.That(repository.TrySave(_profile).Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
            Assert.That(File.ReadAllText(repository.MainPath), Is.EqualTo("broken-main"));
            Assert.That(File.ReadAllText(repository.BackupPath), Is.EqualTo("broken-backup"));
        }

        [Test]
        public void DuplicateIdsOrIllegalLayout_DoNotPublishPartialProfile()
        {
            SinglePlayerSaveDto dto = ProfileSaveMapper.ToDto(_profile, 0);
            dto.Items[1].InstanceId = dto.Items[0].InstanceId;
            Assert.That(ProfileSaveMapper.TryRestore(dto, _store.Catalog, out SinglePlayerProfile duplicate).Success, Is.False);
            Assert.That(duplicate, Is.Null);
            dto = ProfileSaveMapper.ToDto(_profile, 0);
            for (int i = 0; i < dto.Items.Length; i++) if (dto.Items[i].InstanceId == "a") dto.Items[i].X = 999;
            Assert.That(ProfileSaveMapper.TryRestore(dto, _store.Catalog, out SinglePlayerProfile invalid).Success, Is.False);
            Assert.That(invalid, Is.Null);
        }

        private sealed class FaultingFiles : IProfileFileSystem
        {
            private readonly ProfileFileSystem _disk = new ProfileFileSystem();
            public bool FailWrite, FailReplace;
            public bool Exists(string path) => _disk.Exists(path);
            public string Read(string path) => _disk.Read(path);
            public void WriteFlushed(string path, string text)
            {
                if (FailWrite) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "partial"); throw new IOException("Injected partial temp write"); }
                _disk.WriteFlushed(path, text);
            }
            public void Move(string source, string destination) => _disk.Move(source, destination);
            public void Replace(string source, string destination, string backup)
            {
                if (FailReplace) throw new IOException("Injected replace failure");
                _disk.Replace(source, destination, backup);
            }
        }
    }
}
