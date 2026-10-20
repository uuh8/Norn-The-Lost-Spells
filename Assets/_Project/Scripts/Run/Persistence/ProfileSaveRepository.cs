using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Game.Run
{
    /// <summary>文件操作边界只用于注入真实 I/O 中断，不模拟库存规则。</summary>
    public interface IProfileFileSystem
    {
        bool Exists(string path);
        string Read(string path);
        void WriteFlushed(string path, string text);
        void Move(string source, string destination);
        void Replace(string source, string destination, string backup);
    }
    public sealed class ProfileFileSystem : IProfileFileSystem
    {
        public bool Exists(string path) => File.Exists(path);
        public string Read(string path) => File.ReadAllText(path);
        public void WriteFlushed(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            { writer.Write(text); writer.Flush(); stream.Flush(true); }
        }
        public void Move(string source, string destination) => File.Move(source, destination);
        public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);
    }
    public sealed class ProfileSaveRepository
    {
        public string MainPath { get; }
        public string BackupPath { get; }
        private readonly ItemCatalogIndex _catalog;
        private readonly IProfileFileSystem _files;
        private readonly string _temporaryPath;
        private bool _authorized, _blocked, _recovered;
        private long _generation;
        private string _rejectedPrimary, _recoveredSource;
        public ProfileSaveRepository(string directory, ItemCatalogIndex catalog, IProfileFileSystem files = null)
        {
            MainPath = Path.Combine(Path.GetFullPath(directory), "profile.json");
            BackupPath = Path.Combine(Path.GetFullPath(directory), "profile.backup.json");
            _temporaryPath = Path.Combine(Path.GetFullPath(directory), "profile.tmp.json");
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _files = files ?? new ProfileFileSystem();
        }
        public ProfilePersistenceResult TryLoad(out SinglePlayerProfile profile)
        {
            profile = null; _authorized = false; _blocked = true; _recovered = false; _rejectedPrimary = null;
            try
            {
                bool mainExists = _files.Exists(MainPath);
                if (!mainExists && !_files.Exists(BackupPath))
                { _authorized = true; _blocked = false; _generation = 0; return new ProfilePersistenceResult(ProfilePersistenceStatus.Missing); }
                ProfilePersistenceResult main = new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData);
                if (mainExists)
                {
                    _rejectedPrimary = _files.Read(MainPath);
                    main = Decode(_rejectedPrimary, out profile);
                    if (main.Success) { Authorize(profile); return main; }
                    // 未知版本/定义可能属于合法的新档；绝不能用旧备份静默降级其资产。
                    if (main.Status != ProfilePersistenceStatus.InvalidData) return main;
                }
                if (!_files.Exists(BackupPath)) return main;
                string backupSource=_files.Read(BackupPath);
                ProfilePersistenceResult backup = Decode(backupSource, out profile);
                if (!backup.Success) return backup;
                Authorize(profile); _recovered = true;_recoveredSource=backupSource;
                return new ProfilePersistenceResult(ProfilePersistenceStatus.RecoveredBackup, "主档不可用，已读取合法备份；原文件保留。");
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            { profile = null; return new ProfilePersistenceResult(ProfilePersistenceStatus.IoError, exception.Message); }
        }

        public ProfilePersistenceResult TrySave(SinglePlayerProfile profile)
        {
            if (profile == null) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData);
            if (profile.Items.IsResourceBusy) return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "资源预约期间不能保存半次交易。");
            if (profile.PendingRaid == null && profile.Items.HasWorldTransit) return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "投掷材料尚在世界中，不能写入据点快照。");
            if (profile.PendingRaid == null && profile.PendingMaterialOutputs > 0) return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "附着材料尚在输出中，不能写入据点快照。");
            if (_blocked || (!_authorized && (_files.Exists(MainPath) || _files.Exists(BackupPath))))
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "已有存档未合法读取；拒绝覆盖。");
            if (profile.SaveGeneration != _generation)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "提交代已变化；需重新读取存档。");
            try
            {
                if (_files.Exists(MainPath))
                {
                    string current = _files.Read(MainPath);
                    if (_recovered)
                    {
                        if (!string.Equals(current, _rejectedPrimary, StringComparison.Ordinal))
                            return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "恢复后主档被外部修改，拒绝覆盖。");
                    }
                    else
                    {
                        ProfilePersistenceResult check = Decode(current, out SinglePlayerProfile committed);
                        if (!check.Success || committed.SaveGeneration != _generation)
                        { _blocked = true; return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "磁盘提交已变化或无法识别，拒绝覆盖。"); }
                        PreserveSchemaOriginal(current);
                    }
                }
                // 恢复自旧Schema的backup也是迁移原件；第二次滚动保存前必须另存，不能只保护好主档。
                if(_recovered)PreserveSchemaOriginal(_recoveredSource);
                long nextGeneration = checked(_generation + 1);
                SinglePlayerSaveDto dto = ProfileSaveMapper.ToDto(profile, nextGeneration);
                ProfilePersistenceResult valid = ProfileSaveMapper.TryRestore(dto, _catalog, out _);
                if (!valid.Success) return valid;
                _files.WriteFlushed(_temporaryPath, JsonUtility.ToJson(dto, true));
                if (_recovered && _files.Exists(MainPath))
                {
                    // 保留坏主档，不能 File.Replace 把唯一合法备份换成坏文件。
                    string rejected = Path.Combine(Path.GetDirectoryName(MainPath), "profile.corrupt." + Guid.NewGuid().ToString("N") + ".json");
                    _files.Move(MainPath, rejected);
                }
                if (_files.Exists(MainPath)) _files.Replace(_temporaryPath, MainPath, BackupPath);
                else _files.Move(_temporaryPath, MainPath);
                // 只有最终文件提交成功才确认代数，UI 不能以“临时文件已写”宣称保存成功。
                _generation = nextGeneration; _authorized = true; _recovered = false;
                profile.ConfirmSaved(nextGeneration);
                return new ProfilePersistenceResult(ProfilePersistenceStatus.Success);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is OverflowException)
            { return new ProfilePersistenceResult(ProfilePersistenceStatus.IoError, exception.Message); }
        }

        private void Authorize(SinglePlayerProfile profile) { _authorized = true; _blocked = false; _generation = profile.SaveGeneration; }
        private void PreserveSchemaOriginal(string source)
        {
            var dto=JsonUtility.FromJson<SinglePlayerSaveDto>(source);
            if(dto.SchemaVersion>=ProfileSaveMapper.CurrentSchema)return;
            string path=Path.Combine(Path.GetDirectoryName(MainPath),"profile.schema"+dto.SchemaVersion+".original.json");
            if(!_files.Exists(path))_files.WriteFlushed(path,source);
        }
        private ProfilePersistenceResult Decode(string text, out SinglePlayerProfile profile)
        {
            profile = null;
            try { return ProfileSaveMapper.TryRestore(JsonUtility.FromJson<SinglePlayerSaveDto>(text), _catalog, out profile); }
            catch (ArgumentException exception) { return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, exception.Message); }
        }
    }
}
