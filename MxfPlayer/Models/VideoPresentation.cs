namespace MxfPlayer.Models;

// A retained frame remains valid if a seek clears the cache during Present.
public sealed record VideoPresentation(VideoFrameData Frame, long FrameIndex, long Epoch) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}
