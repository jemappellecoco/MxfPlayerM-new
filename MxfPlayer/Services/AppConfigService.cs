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
                return _cachedConfig;
            }
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
