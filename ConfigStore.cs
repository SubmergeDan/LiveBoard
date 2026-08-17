using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LiveBoard
{
    public sealed class ConfigStore
    {
        private const int BackupGenerationCount = 5;
        public string ConfigDirectory { get; private set; }
        public string ConfigPath { get; private set; }
        public string BackupPath { get; private set; }
        public bool LoadedFromBackup { get; private set; }
        public bool LoadedFromLegacyLocation { get; private set; }

        public ConfigStore()
        {
            ConfigDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoard");
            ConfigPath = Path.Combine(ConfigDirectory, "config.json");
            BackupPath = ConfigPath + ".bak";
        }

        public AppConfig Load()
        {
            LoadedFromBackup = false;
            LoadedFromLegacyLocation = false;
            AppConfig config;
            if (TryLoad(ConfigPath, out config))
            {
                AppConfig backup;
                if (!config.QueueExplicitlyCleared && config.Rooms.Count == 0 && TryLoadNonEmptyBackup(ConfigPath, out backup))
                {
                    LoadedFromBackup = true;
                    return backup;
                }
                if (!config.QueueExplicitlyCleared && config.Rooms.Count == 0)
                {
                    var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecordingHelper", "config.json");
                    if (TryLoad(legacyPath, out backup) && backup.Rooms.Count > 0)
                    {
                        LoadedFromLegacyLocation = true;
                        return backup;
                    }
                }
                return config;
            }

            if (TryLoadBackup(ConfigPath, out config))
            {
                LoadedFromBackup = true;
                return config;
            }

            var legacyConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecordingHelper", "config.json");
            if (TryLoad(legacyConfigPath, out config))
            {
                LoadedFromLegacyLocation = true;
                return config;
            }
            return AppConfig.CreateDefault();
        }

        private bool TryLoadNonEmptyBackup(string path, out AppConfig config)
        {
            config = null;
            for (var generation = 0; generation <= BackupGenerationCount; generation++)
            {
                var backupPath = GetBackupPath(path, generation);
                AppConfig candidate;
                if (TryLoad(backupPath, out candidate) && candidate.Rooms.Count > 0)
                {
                    config = candidate;
                    return true;
                }
            }
            return false;
        }

        private bool TryLoadBackup(string path, out AppConfig config)
        {
            config = null;
            for (var generation = 0; generation <= BackupGenerationCount; generation++)
            {
                if (TryLoad(GetBackupPath(path, generation), out config))
                    return true;
            }
            return false;
        }

        private string GetBackupPath(string path, int generation)
        {
            return generation == 0 ? path + ".bak" : path + ".bak." + generation;
        }

        private bool TryLoad(string path, out AppConfig config)
        {
            config = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            try
            {
                config = LoadFrom(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public AppConfig LoadFrom(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(AppConfig));
            using (var stream = File.OpenRead(path))
            {
                var config = serializer.ReadObject(stream) as AppConfig;
                if (config == null)
                    throw new InvalidDataException("配置文件为空");
                if (config.Rooms == null)
                    config.Rooms = new System.Collections.ObjectModel.ObservableCollection<RoomConfig>();
                if (string.IsNullOrWhiteSpace(config.OutputDirectory))
                    config.OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (config.CheckIntervalSeconds <= 0)
                    config.CheckIntervalSeconds = 16;
                if (config.CheckIntervalSeconds > 120)
                    config.CheckIntervalSeconds = 120;
                if (string.IsNullOrWhiteSpace(config.DefaultQuality))
                    config.DefaultQuality = "自动";
                if (string.IsNullOrWhiteSpace(config.DefaultFormat))
                    config.DefaultFormat = "MP4";
                if (string.IsNullOrWhiteSpace(config.DefaultPlatform))
                    config.DefaultPlatform = "抖音";
                if (string.IsNullOrWhiteSpace(config.MediaOutputDirectory))
                    config.MediaOutputDirectory = config.OutputDirectory;
                if (string.IsNullOrWhiteSpace(config.MediaCookieBrowser))
                    config.MediaCookieBrowser = "不使用浏览器登录";
                foreach (var room in config.Rooms)
                {
                    if (room == null)
                        continue;
                    if (string.IsNullOrWhiteSpace(room.SegmentMode))
                        room.SegmentMode = room.SegmentEnabled ? "时间" : "关闭";
                    if (room.SegmentMode != "时间" && room.SegmentMode != "大小" && room.SegmentMode != "关闭")
                        room.SegmentMode = "关闭";
                    if (room.SegmentMinutes <= 0)
                        room.SegmentMinutes = 60;
                    if (room.SegmentSizeMb <= 0)
                        room.SegmentSizeMb = 2048;
                    if (string.IsNullOrWhiteSpace(room.Quality))
                        room.Quality = "自动";
                    if (string.IsNullOrWhiteSpace(room.OutputFormat))
                        room.OutputFormat = "MP4";
                    if (string.IsNullOrWhiteSpace(room.Platform))
                        room.Platform = "抖音";
                }
                return config;
            }
        }

        public bool Save(AppConfig config)
        {
            if (ShouldPreserveExistingQueue(config))
                return false;
            SaveTo(config, ConfigPath);
            return true;
        }

        private bool ShouldPreserveExistingQueue(AppConfig config)
        {
            if (config == null || config.QueueExplicitlyCleared || config.Rooms == null || config.Rooms.Count != 0)
                return false;

            AppConfig savedConfig;
            return TryLoad(ConfigPath, out savedConfig) && savedConfig.Rooms.Count > 0;
        }

        public void SaveTo(AppConfig config, string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var serializer = new DataContractJsonSerializer(typeof(AppConfig));
            var tempPath = path + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                serializer.WriteObject(stream, config);
            }
            if (File.Exists(path))
            {
                RotateBackups(path);
                try
                {
                    File.Replace(tempPath, path, path + ".bak", true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, path + ".bak", true);
                    File.Delete(path);
                    File.Move(tempPath, path);
                }
            }
            else
                File.Move(tempPath, path);
        }

        private void RotateBackups(string path)
        {
            for (var generation = BackupGenerationCount; generation >= 1; generation--)
            {
                var sourcePath = GetBackupPath(path, generation - 1);
                if (File.Exists(sourcePath))
                    File.Copy(sourcePath, GetBackupPath(path, generation), true);
            }
        }

        public string ReadablePath()
        {
            return ConfigPath;
        }
    }
}
