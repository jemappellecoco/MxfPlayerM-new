namespace MxfPlayer.Models
{
    public class MediaFile
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        // MediaInfo 結果
        public MediaInfoResult? Info { get; set; }

        // 規格 + 解碼檢查結果
        public MediaSpecCheckResult? SpecCheck { get; set; }

        // HD / SD / Unknown
        public string SpecType { get; set; } = "";

        // 是否已經檢查過
        public bool IsChecked { get; set; } = false;
        public override string ToString() => FileName;
    }
}