using System;
using System.Collections.Generic;

namespace MxfPlayer.Models
{
    public class CachedMediaAnalysis
    {
        public string FullPath { get; set; } = "";
        public long FileLength { get; set; }
        public DateTime CreationTimeUtc { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public MediaInfoResult Info { get; set; } = new();
        public string SpecType { get; set; } = "Unknown";
        public bool SpecIsPass { get; set; }
        public List<string> SpecErrors { get; set; } = new();
        public string DecodeCheckStatus { get; set; } = "NotChecked";
        public string DecodeCheckError { get; set; } = "";
    }
}
