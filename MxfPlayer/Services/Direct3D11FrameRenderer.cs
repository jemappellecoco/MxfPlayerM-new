using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using AVColorSpace = FFmpeg.AutoGen.AVColorSpace;
using AVChromaLocation = FFmpeg.AutoGen.AVChromaLocation;
using Color = System.Drawing.Color;
using MxfPlayer.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace MxfPlayer.Services;

// Device, immediate context and swap chain are owned by one presentation thread.
internal sealed class Direct3D11FrameRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11PixelShader _verticalShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11Buffer _scaleConstants;
    private ID3D11Texture2D? _horizontalTexture;
    private ID3D11RenderTargetView? _horizontalTarget;
    private ID3D11ShaderResourceView? _horizontalView;
    private int _horizontalWidth, _horizontalHeight;
    private ID3D11Texture2D? _horizontalWeights, _verticalWeights;
    private ID3D11ShaderResourceView? _horizontalWeightView, _verticalWeightView;
    private int _weightSourceWidth, _weightSourceHeight, _weightWidth, _weightHeight;
    private int _horizontalTaps, _verticalTaps;
    private ID3D11RenderTargetView? _target;
    private readonly ID3D11Texture2D?[] _textures = new ID3D11Texture2D?[3];
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[3];
    private int _width, _height, _sourceWidth, _sourceHeight, _chromaWidth, _chromaHeight;
    private bool _planar;

    public Direct3D11FrameRenderer(IntPtr window, int width, int height)
    {
        // Hardware only: do not silently report software WARP rendering as GPU use.
        D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _device, out _, out _context).CheckError();
        try
        {
            _factory = CreateDXGIFactory1<IDXGIFactory2>();
            _swapChain = _factory.CreateSwapChainForHwnd(_device, window, new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height),
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore
            });
            _factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            _vertexShader = _device.CreateVertexShader(CompileShader("VS", "vs_4_0"));
            _pixelShader = _device.CreatePixelShader(CompileShader("PSHorizontal", "ps_4_0"));
            _verticalShader = _device.CreatePixelShader(CompileShader("PSVertical", "ps_4_0"));
            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue
            });
            _constants = _device.CreateBuffer(48, BindFlags.ConstantBuffer);
            _scaleConstants = _device.CreateBuffer(32, BindFlags.ConstantBuffer);
            Resize(width, height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (_width == width && _height == height)) return;
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _target?.Dispose();
        _target = null;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(backBuffer);
        _width = width;
        _height = height;
    }

    public unsafe void Upload(VideoFrameData frame)
    {
        if (_sourceWidth != frame.Width || _sourceHeight != frame.Height ||
            _chromaWidth != frame.ChromaWidth || _chromaHeight != frame.ChromaHeight || _planar != frame.IsPlanarYuv)
        {
            ReleaseTextures();
            _sourceWidth = frame.Width; _sourceHeight = frame.Height;
            _chromaWidth = frame.ChromaWidth; _chromaHeight = frame.ChromaHeight;
            _planar = frame.IsPlanarYuv;
            CreateTexture(0, frame.Width, frame.Height, _planar ? Format.R8_UNorm : Format.B8G8R8A8_UNorm);
            if (_planar)
            {
                CreateTexture(1, frame.ChromaWidth, frame.ChromaHeight, Format.R8_UNorm);
                CreateTexture(2, frame.ChromaWidth, frame.ChromaHeight, Format.R8_UNorm);
            }
        }
        fixed (byte* pixels = frame.Pixels)
        {
            _context.UpdateSubresource(_textures[0]!, 0, null, (IntPtr)pixels,
                (uint)(frame.Width * (_planar ? 1 : 4)), 0);
            if (_planar)
            {
                _context.UpdateSubresource(_textures[1]!, 0, null, (IntPtr)(pixels + frame.YSize), (uint)frame.ChromaWidth, 0);
                _context.UpdateSubresource(_textures[2]!, 0, null, (IntPtr)(pixels + frame.YSize + frame.ChromaSize), (uint)frame.ChromaWidth, 0);
            }
        }

        // Explicit matrix/range selection prevents limited-range blacks becoming
        // grey and avoids applying an SD matrix to tagged HD material.
        (float kr, float kb) = frame.ColorSpace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => (0.2126f, 0.0722f),
            AVColorSpace.AVCOL_SPC_BT2020_NCL => (0.2627f, 0.0593f),
            AVColorSpace.AVCOL_SPC_FCC => (0.30f, 0.11f),
            AVColorSpace.AVCOL_SPC_SMPTE240M => (0.212f, 0.087f),
            _ => (0.299f, 0.114f)
        };
        float kg = 1 - kr - kb;
        float chromaX = frame.ChromaWidth < frame.Width && frame.ChromaLocation is
            AVChromaLocation.AVCHROMA_LOC_LEFT or AVChromaLocation.AVCHROMA_LOC_TOPLEFT or
            AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT ? 0.5f / frame.Width : 0;
        float chromaY = frame.ChromaHeight < frame.Height ? frame.ChromaLocation switch
        {
            AVChromaLocation.AVCHROMA_LOC_TOP or AVChromaLocation.AVCHROMA_LOC_TOPLEFT => 0.5f / frame.Height,
            AVChromaLocation.AVCHROMA_LOC_BOTTOM or AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT => -0.5f / frame.Height,
            _ => 0
        } : 0;
        Vector4[] constants =
        {
            new(frame.FullRange ? 0 : 16f / 255, frame.FullRange ? 1 : 255f / 219,
                128f / 255, frame.FullRange ? 1 : 255f / 224),
            new(2 * (1 - kr), 2 * (1 - kb), -2 * kb * (1 - kb) / kg, -2 * kr * (1 - kr) / kg),
            new(_planar ? 1 : 0, chromaX, chromaY, 0)
        };
        _context.UpdateSubresource(constants, _constants);
    }

    private void CreateTexture(int plane, int width, int height, Format format)
    {
        _textures[plane] = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
        });
        _views[plane] = _device.CreateShaderResourceView(_textures[plane]!);
    }

    public bool Draw(bool hasFrame, Color background)
    {
        if (_target == null) return false;
        _context.OMSetRenderTargets(_target);
        _context.ClearRenderTargetView(_target, new Color4(background.R / 255f, background.G / 255f, background.B / 255f, 1));
        if (hasFrame && _sourceWidth > 0)
        {
            float scale = Math.Min(_width / (float)_sourceWidth, _height / (float)_sourceHeight);
            int w = Math.Clamp((int)Math.Round(_sourceWidth * scale), 1, _width);
            int h = Math.Clamp((int)Math.Round(_sourceHeight * scale), 1, _height);
            EnsureHorizontalTarget(w, _sourceHeight);
            EnsureWeights(w, h);
            _context.OMSetRenderTargets(_horizontalTarget!);
            _context.RSSetViewport(new Viewport(0, 0, w, _sourceHeight));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetConstantBuffer(0, _constants);
            _context.PSSetConstantBuffer(1, _scaleConstants);
            _context.UpdateSubresource(new[] { new Vector4(_sourceWidth, _sourceHeight, w, h),
                new Vector4(_horizontalTaps, _verticalTaps, 0, 0) }, _scaleConstants);
            _context.PSSetSampler(0, _sampler);
            for (uint i = 0; i < 3; i++) _context.PSSetShaderResource(i, _views[i]!);
            _context.PSSetShaderResource(3, _horizontalWeightView!);
            _context.PSSetShaderResource(4, _verticalWeightView!);
            _context.Draw(3, 0);
            for (uint i = 0; i < 3; i++) _context.PSSetShaderResource(i, null!);

            _context.OMSetRenderTargets(_target);
            _context.RSSetViewport(new Viewport((_width - w) / 2, (_height - h) / 2, w, h));
            _context.PSSetShader(_verticalShader);
            _context.PSSetShaderResource(0, _horizontalView!);
            _context.Draw(3, 0);
            _context.PSSetShaderResource(0, null!);
            _context.PSSetShaderResource(3, null!);
            _context.PSSetShaderResource(4, null!);
        }
        // Submit each picture in sequence without waiting for monitor refresh.
        // The compositor may show only a subset; the application never skips
        // a source picture to catch the media clock.
        var result = _swapChain.Present(0, PresentFlags.None);
        if (result.Code == 0x087A0001) return false; // DXGI_STATUS_OCCLUDED
        result.CheckError();
        return true;
    }

    public bool IsVisible()
    {
        var result = _swapChain.Present(0, PresentFlags.Test);
        if (result.Code == 0x087A0001) return false;
        result.CheckError();
        return true;
    }

    private void EnsureHorizontalTarget(int width, int height)
    {
        if (_horizontalTexture != null && _horizontalWidth == width && _horizontalHeight == height) return;
        _horizontalView?.Dispose();
        _horizontalTarget?.Dispose();
        _horizontalTexture?.Dispose();
        _horizontalTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        });
        _horizontalTarget = _device.CreateRenderTargetView(_horizontalTexture);
        _horizontalView = _device.CreateShaderResourceView(_horizontalTexture);
        _horizontalWidth = width; _horizontalHeight = height;
    }

    private void ReleaseTextures()
    {
        for (int i = 0; i < 3; i++)
        {
            _views[i]?.Dispose(); _views[i] = null;
            _textures[i]?.Dispose(); _textures[i] = null;
        }
    }

    private void EnsureWeights(int width, int height)
    {
        if (_weightSourceWidth == _sourceWidth && _weightSourceHeight == _sourceHeight &&
            _weightWidth == width && _weightHeight == height) return;
        _horizontalWeightView?.Dispose(); _horizontalWeights?.Dispose();
        _verticalWeightView?.Dispose(); _verticalWeights?.Dispose();
        (_horizontalWeights, _horizontalWeightView, _horizontalTaps) = CreateWeights(_sourceWidth, width);
        (_verticalWeights, _verticalWeightView, _verticalTaps) = CreateWeights(_sourceHeight, height);
        _weightSourceWidth = _sourceWidth; _weightSourceHeight = _sourceHeight;
        _weightWidth = width; _weightHeight = height;
    }

    private unsafe (ID3D11Texture2D, ID3D11ShaderResourceView, int) CreateWeights(int source, int destination)
    {
        // Coefficients depend only on geometry. Compute them once per resize,
        // and combine adjacent same-sign taps using the GPU's linear sampler.
        double reduction = Math.Max(1.0, source / (double)destination);
        var rows = new List<Vector4>[destination];
        int taps = 1;
        for (int pixel = 0; pixel < destination; pixel++)
        {
            double center = (pixel + 0.5) * source / destination - 0.5;
            int first = (int)Math.Ceiling(center - 2 * reduction);
            int last = (int)Math.Floor(center + 2 * reduction);
            double[] weights = new double[last - first + 1];
            double total = 0;
            for (int t = 0; t < weights.Length; t++)
            {
                double x = Math.Abs((center - (first + t)) / reduction);
                // Bicubic B=0, C=0.6 (FFmpeg's default bicubic parameters).
                // Widen the four-tap kernel during reduction for antialiasing.
                const double a = -0.6;
                weights[t] = x < 1
                    ? ((a + 2) * x - (a + 3)) * x * x + 1
                    : x < 2 ? ((a * x - 5 * a) * x + 8 * a) * x - 4 * a : 0;
                total += weights[t];
            }
            var row = rows[pixel] = new List<Vector4>();
            for (int t = 0; t < weights.Length; t++)
            {
                double weight = weights[t];
                if (Math.Abs(weight) < 1e-10) continue;
                double coordinate = first + t;
                if (t + 1 < weights.Length && weight * weights[t + 1] > 0)
                {
                    double next = weights[++t];
                    coordinate += next / (weight + next);
                    weight += next;
                }
                row.Add(new Vector4((float)((coordinate + 0.5) / source), (float)(weight / total), 0, 0));
            }
            taps = Math.Max(taps, row.Count);
        }
        var data = new Vector4[taps * destination];
        for (int pixel = 0; pixel < destination; pixel++)
            rows[pixel].CopyTo(data, pixel * taps);
        ID3D11Texture2D texture;
        fixed (Vector4* pixels = data)
            texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)taps, Height = (uint)destination, MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
            }, new[] { new SubresourceData((IntPtr)pixels, (uint)(taps * 16)) });
        return (texture, _device.CreateShaderResourceView(texture), taps);
    }

    public void Dispose()
    {
        _context?.ClearState();
        ReleaseTextures();
        _target?.Dispose();
        _horizontalView?.Dispose(); _horizontalTarget?.Dispose(); _horizontalTexture?.Dispose();
        _horizontalWeightView?.Dispose(); _horizontalWeights?.Dispose();
        _verticalWeightView?.Dispose(); _verticalWeights?.Dispose();
        _scaleConstants?.Dispose(); _verticalShader?.Dispose();
        _constants?.Dispose(); _sampler?.Dispose();
        _pixelShader?.Dispose(); _vertexShader?.Dispose();
        _swapChain?.Dispose(); _factory?.Dispose();
        _context?.Dispose(); _device?.Dispose();
    }

    private static byte[] CompileShader(string entry, string profile)
    {
        byte[] source = Encoding.UTF8.GetBytes(ShaderSource);
        int hr = D3DCompile(source, (nuint)source.Length, null, IntPtr.Zero, IntPtr.Zero,
            entry, profile, 0x8000, 0, out var code, out var errors);
        try
        {
            if (hr < 0)
                throw new InvalidOperationException(errors == null ? $"D3DCompile: 0x{hr:X8}" :
                    Marshal.PtrToStringAnsi(errors.GetBufferPointer()));
            byte[] bytes = new byte[checked((int)code!.GetBufferSize())];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nuint size, string? sourceName,
        IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2,
        out ID3DBlob? code, out ID3DBlob? errors);

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    private const string ShaderSource = """
        Texture2D plane0 : register(t0);
        Texture2D plane1 : register(t1);
        Texture2D plane2 : register(t2);
        Texture2D horizontalWeights : register(t3);
        Texture2D verticalWeights : register(t4);
        SamplerState linearClamp : register(s0);
        cbuffer Conversion : register(b0) { float4 range; float4 coefficients; float4 mode; };
        cbuffer Scaling : register(b1) { float4 dimensions; float4 tapCounts; };
        struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.position = float4(o.uv.x * 2 - 1, 1 - o.uv.y * 2, 0, 1);
            return o;
        }
        float3 ReadRgb(float2 uv) {
            if (mode.x < 0.5) return plane0.SampleLevel(linearClamp, uv, 0).rgb;
            float y = (plane0.SampleLevel(linearClamp, uv, 0).r - range.x) * range.y;
            float u = (plane1.SampleLevel(linearClamp, uv + mode.yz, 0).r - range.z) * range.w;
            float v = (plane2.SampleLevel(linearClamp, uv + mode.yz, 0).r - range.z) * range.w;
            return float3(y + coefficients.x * v,
                y + coefficients.z * u + coefficients.w * v, y + coefficients.y * u);
        }
        // Separable bicubic. Widen the kernel when reducing size, so the
        // full-frame downsample filters thin lines instead of aliasing them.
        float4 PSHorizontal(Vertex i) : SV_TARGET {
            int pixel = min((int)(i.uv.x * dimensions.z), (int)dimensions.z - 1);
            float3 sum = 0;
            [loop] for (int tap = 0; tap < (int)tapCounts.x; tap++) {
                float2 coefficient = horizontalWeights.Load(int3(tap, pixel, 0)).xy;
                sum += coefficient.y * ReadRgb(float2(coefficient.x, i.uv.y));
            }
            return float4(sum, 1);
        }
        float4 PSVertical(Vertex i) : SV_TARGET {
            int pixel = min((int)(i.uv.y * dimensions.w), (int)dimensions.w - 1);
            float3 sum = 0;
            [loop] for (int tap = 0; tap < (int)tapCounts.y; tap++) {
                float2 coefficient = verticalWeights.Load(int3(tap, pixel, 0)).xy;
                sum += coefficient.y * plane0.SampleLevel(linearClamp,
                    float2(i.uv.x, coefficient.x), 0).rgb;
            }
            return float4(saturate(sum), 1);
        }
        """;
}
