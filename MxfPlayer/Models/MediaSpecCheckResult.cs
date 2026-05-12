namespace MxfPlayer.Models
{
    public class MediaSpecCheckResult
    {
        public bool IsPass { get; set; }
        public string Status => IsPass ? "OK" : "Error";
        public List<string> Errors { get; set; } = new();
    }
}