using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LiveBoard
{
    public sealed class ConfigStore
    {
        public string ConfigDirectory { get; private set; }
        public string ConfigPath { get; private set; }

        public ConfigStore()
        {
            ConfigDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoard");
            ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        }

        public AppConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return AppConfig.CreateDefault();

            try
            {
                return LoadFrom(ConfigPath);
            }
            catch
            {
                return AppConfig.CreateDefault();
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

        public void Save(AppConfig config)
        {
            SaveTo(config, ConfigPath);
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
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }

        public string ReadablePath()
        {
            return ConfigPath;
        }
    }
}
