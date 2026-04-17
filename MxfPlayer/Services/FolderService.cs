using System.Collections.Generic;
using System.IO;
using System.Linq;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class FolderService
    {
        public List<MediaFile> LoadFolder(string path)
        {
            if (!Directory.Exists(path)) return new();

            return Directory.GetFiles(path, "*.mxf")
                .Concat(Directory.GetFiles(path, "*.MXF"))
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