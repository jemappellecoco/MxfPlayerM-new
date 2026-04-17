namespace MxfPlayer.Models
{
    public class MediaFile
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public override string ToString() => FileName;
    }
}