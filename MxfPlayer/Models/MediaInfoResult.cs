namespace MxfPlayer.Models
{
    public class MediaInfoResult
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";

        public string Width { get; set; } = "";
        public string Height { get; set; } = "";
        public string FrameRate { get; set; } = "";
        public string AudioCount { get; set; } = "";
        public string CommercialName { get; set; } = "";
        public string ScanType { get; set; } = "";
        public string ScanOrder { get; set; } = "";
        public string Som { get; set; } = "";
        public string Eom { get; set; } = "";
        public string DurationTc { get; set; } = "";
        public string BitRate { get; set; } = "";
        public string DisplayAspect { get; set; } = "";
        public string SpecCheck { get; set; } = "";
    }
}