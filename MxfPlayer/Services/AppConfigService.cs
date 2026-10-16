using System;
using System.IO;
using System.Text.Json;

namespace MxfPlayer.Services
{
    public class AppConfig
    {
        public string FFmpegPath { get; set; } = "";
        public string MediaInfoPath { get; set; } = "";
    }

    public static class AppConfigService
    {
        private static readonly object LockObject = new();
        private static AppConfig? _cachedConfig;

        public static AppConfig Load()
        {
            lock (LockObject)
            {
                if (_cachedConfig != null)
                    return _cachedConfig;

                _cachedConfig = LoadFromDisk();
                string bundledFfmpeg = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
                string bundledMediaInfo = Path.Combine(AppContext.BaseDirectory, "MediaInfo", "MediaInfo.dll");
                _cachedConfig.FFmpegPath = File.Exists(Path.Combine(bundledFfmpeg, "avcodec-62.dll"))
                    ? bundledFfmpeg : ResolvePath(_cachedConfig.FFmpegPath);
                _cachedConfig.MediaInfoPath = File.Exists(bundledMediaInfo)
                    ? bundledMediaInfo : ResolvePath(_cachedConfig.MediaInfoPath);
                return _cachedConfig;
            }
        }

        private static string ResolvePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path, AppContext.BaseDirectory);
        }

        private static AppConfig LoadFromDisk()
        {
            string jsonPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(jsonPath))
                return new AppConfig();

            try
            {
                string json = File.ReadAllText(jsonPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }
    }
}
