using System.Buffers;
using System.Threading;
using FFmpeg.AutoGen;

namespace MxfPlayer.Models;

// The cache and the view each own a reference. A seek may clear the cache while
// Windows still needs to repaint the last displayed frame.
public sealed class VideoFrameData : IDisposable
{
    private int _references = 1;
    public int Width { get; }
    public int Height { get; }
    public int ChromaWidth { get; }
    public int ChromaHeight { get; }
    public bool IsPlanarYuv { get; }
    public bool FullRange { get; }
    public AVColorSpace ColorSpace { get; }
    public AVChromaLocation ChromaLocation { get; }
    public byte[] Pixels { get; }
    public int YSize => Width * Height;
    public int ChromaSize => ChromaWidth * ChromaHeight;
    public int ByteLength => IsPlanarYuv ? YSize + 2 * ChromaSize : YSize * 4;

    public VideoFrameData(int width, int height, int chromaWidth, int chromaHeight,
        bool planarYuv, bool fullRange, AVColorSpace colorSpace,
        AVChromaLocation chromaLocation = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
    {
        Width = width;
        Height = height;
        ChromaWidth = chromaWidth;
        ChromaHeight = chromaHeight;
        IsPlanarYuv = planarYuv;
        FullRange = fullRange;
        ColorSpace = colorSpace;
        ChromaLocation = chromaLocation;
        Pixels = ArrayPool<byte>.Shared.Rent(ByteLength);
    }

    public VideoFrameData Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public VideoFrameData Clone()
    {
        var copy = new VideoFrameData(Width, Height, ChromaWidth, ChromaHeight,
            IsPlanarYuv, FullRange, ColorSpace, ChromaLocation);
        Pixels.AsSpan(0, ByteLength).CopyTo(copy.Pixels);
        return copy;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
            ArrayPool<byte>.Shared.Return(Pixels);
    }
}
