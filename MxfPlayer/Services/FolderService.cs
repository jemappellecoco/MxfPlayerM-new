using System.Collections.Generic;
using System.IO;
using System.Linq;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class FolderService
    {
        private static readonly string[] MediaExtensions =
        {
            ".mxf",
            ".mp4",
            ".mov",
            ".avi",
            ".mkv"
        };

        public List<MediaFile> LoadFolder(string path)
        {
            if (!Directory.Exists(path)) return new();

            return Directory.EnumerateFiles(path)
                .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(f => f)
                .Select(f => new MediaFile
                {
                    FileName = Path.GetFileName(f),
                    FullPath = f
                })
                .ToList();
        }
    }
}
