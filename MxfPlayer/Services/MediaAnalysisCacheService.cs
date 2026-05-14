using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class MediaAnalysisCacheService
    {
        private readonly string _cachePath;
        private readonly Dictionary<string, CachedMediaAnalysis> _cache;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public MediaAnalysisCacheService()
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MxfPlayer");

            _cachePath = Path.Combine(cacheDir, "media-analysis-cache.json");
            _cache = LoadCache();
        }

        public bool TryGetValid(string fullPath, out CachedMediaAnalysis analysis)
        {
            analysis = null!;

            if (!_cache.TryGetValue(fullPath, out var cached))
                return false;

            if (!File.Exists(fullPath))
                return false;

            var fileInfo = new FileInfo(fullPath);
            if (cached.FileLength != fileInfo.Length)
                return false;

            if (cached.CreationTimeUtc != default &&
                cached.CreationTimeUtc != fileInfo.CreationTimeUtc)
            {
                return false;
            }

            if (cached.LastWriteTimeUtc != fileInfo.LastWriteTimeUtc)
                return false;

            cached.Info.FullPath = fullPath;
            cached.Info.FileName = Path.GetFileName(fullPath);
            analysis = cached;
            return true;
        }

        public void Save(CachedMediaAnalysis analysis)
        {
            if (string.IsNullOrWhiteSpace(analysis.FullPath))
                return;

            _cache[analysis.FullPath] = analysis;
            SaveCache();
        }

        private Dictionary<string, CachedMediaAnalysis> LoadCache()
        {
            try
            {
                if (!File.Exists(_cachePath))
                    return new Dictionary<string, CachedMediaAnalysis>(StringComparer.OrdinalIgnoreCase);

                string json = File.ReadAllText(_cachePath);
                var entries = JsonSerializer.Deserialize<List<CachedMediaAnalysis>>(json) ?? new();

                return entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.FullPath))
                    .GroupBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, CachedMediaAnalysis>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveCache()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache.Values.ToList(), _jsonOptions));
            }
            catch
            {
            }
        }
    }
}
