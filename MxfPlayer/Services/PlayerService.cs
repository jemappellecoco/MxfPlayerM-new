using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using NAudio.Wave;
using FFmpeg.AutoGen;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;

namespace MxfPlayer.Services
{
    public unsafe class PlayerService : IDisposable
    {
        private bool _filterReady = false; 

        private AVFormatContext* _formatContext;
        private Dictionary<int, PointerWrapper<AVCodecContext>> _audioDecoders = new();
        private List<int> _audioStreamIndices = new();
        private readonly object _ffmpegResourceLock = new object();
        private readonly object _audioCacheLock = new object();
   
        private AVFilterGraph* _filterGraph;
        private AVFilterContext** _srcContexts;
        private AVFilterContext* _sinkContext;
        private int _activeAudioTrackCount = 0;

        private const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;
        private const int SwsFastBilinear = 1;
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider _waveProvider;
        private MxfAudioProvider? _fileAudioProvider;
        private MemoryPcmAudioProvider? _memoryAudioProvider;
        private SlidingPcmAudioProvider? _slidingAudioProvider;
        private ReverseSegmentAudioProvider? _reverseAudioProvider;
        private double _audioFps = 29.97;
        private int _audioSampleRate = 48000;
        private int _pcmOutputChannels;
        public double CurrentFps => _audioFps > 0 ? _audioFps : 29.97;
        private readonly Dictionary<long, Bitmap> _videoFrameCache = new();
        private static readonly ParallelOptions VideoBlendParallelOptions = new()
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 2)
        };
        private const long VideoFrameCacheBudgetBytes = 3072L * 1024L * 1024L;
        private const int MinCachedVideoFrames = 96;
        private const int MaxCachedVideoFrames = 600; // Hard cap for decoded bitmaps kept in memory.
        private const int MaxPooledVideoBitmaps = 48;
        private int _maxCachedVideoFrames = 360;
        private int _pooledBitmapWidth;
        private int _pooledBitmapHeight;
        private readonly Stack<Bitmap> _videoBitmapPool = new();
        private const int VideoPreloadLowWaterFrames = 300; // Refill early enough to absorb short decode/CPU dips.
        private const int VideoPreloadHighWaterFrames = 420; // Keep a deeper forward cushion before throttling decode.
        private const int ForwardVideoCacheTailFrames = 12; // Keep only a short already-played tail so cache favors upcoming frames.
        private const int ReverseVideoDecodeWindowFrames = 300; // Keep reverse decode close to the playhead.
        private const int ReverseVideoPreloadLowWaterFrames = 360; // Refill reverse cache before the continuous window runs dry.
        private const int VideoDecoderRestartGapFrames = 30; // Restart decoder if playback has outrun the cached window.
        private const int VideoDecoderRestartCooldownMs = 300;
        private const double VideoStallResumeBufferSeconds = 0.6;//播放中卡住後先補短 buffer，避免卡住太久。
        private long _currentFrameIndex;
        private long _totalVideoFrames;
        private CancellationTokenSource? _videoCts;
        private Task? _videoDecodeTask;
        private int _videoDecodeGeneration;
        private CancellationTokenSource? _audioCacheCts;
        private Task? _audioCacheTask;
        private string? _pcmCachePath;
        private int _audioCacheGeneration;
        private float _forwardAudioCacheRate = 1.0f;
        private long _displayedVideoFrameIndex = -1;
        private ReversePlaybackSegment? _reverseSegment;
        private bool _isVideoPlaying;
        private bool _isPlaybackStalledForVideo;
        private bool _isPlaybackStalledForAudio;
        private string? _ffmpegRuntimeError;
        private float _videoRate = 1.0f;
        private readonly Stopwatch _playbackClock = new();
        private readonly Stopwatch _videoStatusLogClock = Stopwatch.StartNew();
        private readonly Stopwatch _videoDecodeRestartClock = Stopwatch.StartNew();
        private readonly Stopwatch _videoDecodePerfClock = Stopwatch.StartNew();
        private long _videoDecodePerfPackets;
        private long _videoDecodePerfReadTicks;
        private long _videoDecodePerfSendPackets;
        private long _videoDecodePerfSendTicks;
        private long _videoDecodePerfReceiveFrames;
        private long _videoDecodePerfReceiveTicks;
        private long _videoDecodePerfFilterInputFrames;
        private long _videoDecodePerfFilterInputTicks;
        private long _videoDecodePerfFilterOutputFrames;
        private long _videoDecodePerfFilterOutputTicks;
        private long _videoDecodePerfFrames;
        private long _videoDecodePerfBitmapTicks;
        private long _videoDecodePerfCacheTicks;
        private long _videoDecodePerfThrottleTicks;
        private long _playbackStartFrame;
        private long _audioClockStartBytes;
        private long _audioClockStartFrame;
        private const int AudioOutputLatencyMs = 350;
        private const int AudioOutputBufferCount = 4;
        private const int AudioStallResumeBufferMs = 1800;
        private const int ReverseAudioCacheWindowMs = 60000;
        private const int ReverseAudioCacheRefreshBehindMs = 5000;
        private const int ReverseAudioMinRefreshBehindMs = 15000;
        private const int ReverseAudioInitialPlaybackMs = 2000;
        private const int ReverseAudioInitialMaxSourceMs = 12000;
        private const int ReverseSegmentFrames = 300;
        private const int ReverseSegmentPreloadFrames = 90;
        private bool _isInterlaced;
        private bool _topFieldFirst = true;
        private int _displayUnitsPerFrame = 1;

        private bool DeinterlacePlaybackVideo => _isInterlaced;
        private bool FieldPlaybackMode => _displayUnitsPerFrame > 1;
        private int DisplayUnitsPerFrame => _displayUnitsPerFrame;
        private double DisplayFps => CurrentFps * DisplayUnitsPerFrame;


        private readonly object _lock = new object();

        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };
        public string CurrentPath { get; private set; } = string.Empty;
        public int CurrentAudioCount { get; private set; }
        public bool IsAudioReady => _waveOut != null && (_fileAudioProvider != null || _slidingAudioProvider != null || _reverseAudioProvider != null);
        public bool IsPlaying => _isVideoPlaying;
        public long CurrentFrameIndex => MediaFrameFromDisplayIndex(_currentFrameIndex);

        public int GetRequiredVideoBufferFramesForRate(float rate)
        {
            return DisplayFramesToMediaFrames(GetForwardLowWaterDisplayFrames(rate));
        }

        public int GetStartupVideoBufferFramesForRate(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int requestedDisplayFrames = (int)Math.Ceiling(120 * multiplier);
            int maxDisplayFrames = GetMaxCachedVideoFramesForRate(rate);
            int reserveFrames = Math.Max(12, maxDisplayFrames / 4);
            int displayFrames = Math.Clamp(
                Math.Min(requestedDisplayFrames, maxDisplayFrames - reserveFrames),
                30 * DisplayUnitsPerFrame,
                Math.Max(30 * DisplayUnitsPerFrame, maxDisplayFrames));
            return DisplayFramesToMediaFrames(displayFrames);
        }

        private int GetVideoPreloadHighWaterFramesForRate(float rate)
        {
            return DisplayFramesToMediaFrames(GetForwardHighWaterDisplayFrames(rate));
        }

        private int GetForwardLowWaterDisplayFrames(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int requestedFrames = (int)Math.Ceiling(120 * multiplier);
            int maxFrames = GetMaxCachedVideoFramesForRate(rate);
            int maxLowWater = Math.Max(24, maxFrames * 2 / 3);
            return Math.Clamp(Math.Min(requestedFrames, maxLowWater), 24, Math.Max(24, maxFrames));
        }

        private int GetForwardHighWaterDisplayFrames(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int requestedFrames = (int)Math.Ceiling(300 * multiplier);
            int maxFrames = GetMaxCachedVideoFramesForRate(rate);
            int tailFrames = ForwardVideoCacheTailFrames * DisplayUnitsPerFrame;
            int reserveFrames = Math.Max(tailFrames + 12, maxFrames / 12);
            int ceiling = Math.Max(60, maxFrames - reserveFrames);
            int lowWater = GetForwardLowWaterDisplayFrames(rate);
            return Math.Clamp(Math.Min(requestedFrames, ceiling), lowWater + 30, Math.Max(lowWater + 30, maxFrames));
        }

        private int GetForwardDecodeWindowDisplayFrames(float rate)
        {
            int maxFrames = GetMaxCachedVideoFramesForRate(rate);
            int tailFrames = ForwardVideoCacheTailFrames * DisplayUnitsPerFrame;
            int headroom = Math.Max(30, maxFrames / 8);
            return Math.Clamp(
                GetForwardHighWaterDisplayFrames(rate) + headroom,
                GetForwardLowWaterDisplayFrames(rate) + 30,
                Math.Max(GetForwardLowWaterDisplayFrames(rate) + 30, maxFrames - tailFrames));
        }

        private int DisplayFramesToMediaFrames(int displayFrames)
        {
            return Math.Max(1, (int)Math.Ceiling(displayFrames / (double)DisplayUnitsPerFrame));
        }

        private int GetMaxCachedVideoFramesForRate(float rate)
        {
            return Math.Clamp(Volatile.Read(ref _maxCachedVideoFrames), MinCachedVideoFrames, MaxCachedVideoFrames);
        }

        public bool HasVideoBufferForRate(float rate)
        {
            lock (_lock)
            {
                if (_videoFrameCache.Count == 0)
                    return false;

                long requiredFrames = GetRequiredVideoBufferFramesForRate(rate) * DisplayUnitsPerFrame;
                long continuousFrame = GetContinuousCachedFrameLimit(_currentFrameIndex, rate >= 0);

                if (rate >= 0)
                    return continuousFrame >= 0 && continuousFrame - _currentFrameIndex >= requiredFrames;

                return continuousFrame >= 0 && _currentFrameIndex - continuousFrame >= requiredFrames;
            }
        }

        private long GetContinuousCachedFrameLimit(long frameIndex, bool forward)
        {
            if (_videoFrameCache.Count == 0)
                return -1;

            long step = forward ? 1 : -1;
            long limit = frameIndex;

            if (!_videoFrameCache.ContainsKey(limit))
                return -1;

            while (true)
            {
                long next = limit + step;
                if (next < 0 || next >= TotalDisplayUnits)
                    return limit;

                if (!_videoFrameCache.ContainsKey(next))
                    return limit;

                limit = next;
            }
        }

        public void PrepareVideoBuffer()
        {
            EnsureVideoDecoderNearCurrentFrame();
        }
       
        public bool HasCurrentVideoFrame
        {
            get
            {
                lock (_lock)
                    return _videoFrameCache.ContainsKey(_currentFrameIndex);
            }
        }
        public Image? CurrentVideoFrame
        {
            get
            {
                lock (_lock)
                    return _videoFrameCache.TryGetValue(_currentFrameIndex, out var frame) ? frame : null;
            }
        }
        public Image? CreateCurrentVideoFrameSnapshot() => CreateCurrentVideoFrameSnapshot(out _);
        public Image? CreateCurrentVideoFrameSnapshot(out long frameIndex)
        {
            lock (_lock)
            {
                frameIndex = _currentFrameIndex;
                return _videoFrameCache.TryGetValue(_currentFrameIndex, out var frame)
                    ? (Image)frame.Clone()
                    : null;
            }
        }
        public long GetDisplayFrameIndex()
        {
            lock (_lock)
            {
                return _videoFrameCache.ContainsKey(_currentFrameIndex)
                    ? _currentFrameIndex
                    : -1;
            }
        }
        public Image? CreateDisplayVideoFrameSnapshot(out long frameIndex)
        {
            lock (_lock)
            {
                frameIndex = _currentFrameIndex;
                return _videoFrameCache.TryGetValue(_currentFrameIndex, out var exactFrame)
                    ? (Image)exactFrame.Clone()
                    : null;
            }
        }
        public Image? GetDisplayVideoFrameReference(out long frameIndex)
        {
            lock (_lock)
            {
                frameIndex = _currentFrameIndex;
                if (!_videoFrameCache.TryGetValue(_currentFrameIndex, out var exactFrame))
                    return null;

                _displayedVideoFrameIndex = frameIndex;
                return exactFrame;
            }
        }

        public void ClearDisplayedVideoFrameReference()
        {
            lock (_lock)
                _displayedVideoFrameIndex = -1;
        }
        public long CurrentTimeMs => TimeMsFromFrame(MediaFrameFromDisplayIndex(_currentFrameIndex), _audioFps);
        public long LengthMs => _totalVideoFrames <= 0 ? 0 : TimeMsFromFrame(_totalVideoFrames - 1, _audioFps);
        public long LastFrameIndex => Math.Max(0, _totalVideoFrames - 1);

        private long TotalDisplayUnits => Math.Max(0, _totalVideoFrames * DisplayUnitsPerFrame);

        private long DisplayIndexFromMediaFrame(long frameIndex)
        {
            if (frameIndex < 0) frameIndex = 0;
            return frameIndex * DisplayUnitsPerFrame;
        }

        private long MediaFrameFromDisplayIndex(long displayIndex)
        {
            if (displayIndex < 0) displayIndex = 0;
            return displayIndex / DisplayUnitsPerFrame;
        }

    

        private class PointerWrapper<T> where T : unmanaged { public T* Ptr; }

        private sealed class OneShotPcmAudioProvider : IWaveProvider
        {
            private readonly byte[] _pcmData;
            private int _position;

            public WaveFormat WaveFormat { get; }

            public OneShotPcmAudioProvider(byte[] pcmData, int sampleRate)
            {
                _pcmData = pcmData;
                WaveFormat = new WaveFormat(sampleRate > 0 ? sampleRate : 48000, 16, 2);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int available = _pcmData.Length - _position;
                if (available <= 0)
                    return 0;

                int bytesToCopy = Math.Min(count, available);
                Buffer.BlockCopy(_pcmData, _position, buffer, offset, bytesToCopy);
                _position += bytesToCopy;
                return bytesToCopy;
            }
        }

        private sealed unsafe class VideoBwdifFilter : IDisposable
        {
            private AVFilterGraph* _graph;
            private AVFilterContext* _srcContext;
            private AVFilterContext* _sinkContext;

            public bool IsReady => _graph != null && _srcContext != null && _sinkContext != null;

            public VideoBwdifFilter(
                int width,
                int height,
                AVPixelFormat pixelFormat,
                AVRational timeBase,
                AVRational frameRate,
                AVRational sampleAspectRatio)
            {
                _graph = ffmpeg.avfilter_graph_alloc();
                if (_graph == null)
                    return;

                AVFilter* buffer = ffmpeg.avfilter_get_by_name("buffer");
                AVFilter* bufferSink = ffmpeg.avfilter_get_by_name("buffersink");
                if (buffer == null || bufferSink == null)
                    return;

                if (timeBase.num <= 0 || timeBase.den <= 0)
                    timeBase = new AVRational { num = 1, den = 1000 };

                if (frameRate.num <= 0 || frameRate.den <= 0)
                    frameRate = new AVRational { num = 30000, den = 1001 };

                if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
                    sampleAspectRatio = new AVRational { num = 1, den = 1 };

                string args =
                    $"video_size={width}x{height}:" +
                    $"pix_fmt={(int)pixelFormat}:" +
                    $"time_base={timeBase.num}/{timeBase.den}:" +
                    $"pixel_aspect={sampleAspectRatio.num}/{sampleAspectRatio.den}:" +
                    $"frame_rate={frameRate.num}/{frameRate.den}";

                AVFilterContext* src = null;
                AVFilterContext* sink = null;
                int srcRet = ffmpeg.avfilter_graph_create_filter(&src, buffer, "in", args, null, _graph);
                if (srcRet < 0 || src == null)
                    return;

                int sinkRet = ffmpeg.avfilter_graph_create_filter(&sink, bufferSink, "out", null, null, _graph);
                if (sinkRet < 0 || sink == null)
                    return;

                _srcContext = src;
                _sinkContext = sink;

                AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
                AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
                if (outputs == null || inputs == null)
                {
                    ffmpeg.avfilter_inout_free(&outputs);
                    ffmpeg.avfilter_inout_free(&inputs);
                    return;
                }

                outputs->name = ffmpeg.av_strdup("in");
                outputs->filter_ctx = _srcContext;
                outputs->pad_idx = 0;
                outputs->next = null;

                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = _sinkContext;
                inputs->pad_idx = 0;
                inputs->next = null;

                //string filterMode = FieldPlaybackMode ? "send_field" : "send_frame";
                //string filterName = FieldPlaybackMode ? "bwdif" : "yadif";
                //string filterDesc = $"{filterName}=mode={filterMode}:parity=auto:deint=interlaced";
                string filterDesc = "bwdif=mode=send_field:parity=auto:deint=all";

                int parseRet = ffmpeg.avfilter_graph_parse_ptr(_graph, filterDesc, &inputs, &outputs, null);
                if (parseRet >= 0 && ffmpeg.avfilter_graph_config(_graph, null) < 0)
                    parseRet = -1;

                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);

                if (parseRet < 0)
                {
                    Dispose();
                }
            }

            public bool AddFrame(AVFrame* frame)
            {
                return IsReady &&
                    ffmpeg.av_buffersrc_add_frame_flags(_srcContext, frame, (int)AV_BUFFERSRC_FLAG_KEEP_REF) >= 0;
            }

            public bool TryReceiveFrame(AVFrame* frame)
            {
                return IsReady && ffmpeg.av_buffersink_get_frame(_sinkContext, frame) >= 0;
            }

            public void Dispose()
            {
                if (_graph != null)
                {
                    var graph = _graph;
                    ffmpeg.avfilter_graph_free(&graph);
                    _graph = null;
                }

                _srcContext = null;
                _sinkContext = null;
            }
        }

        public PlayerService()
        {
            LoadFFmpegFromConfig();

            _waveProvider = CreateWaveProvider(_audioSampleRate);
        }
        public void ConfigureVideoScan(string scanType, string scanOrder)
        {
            _isInterlaced = string.Equals(
                scanType?.Trim(),
                "Interlaced",
                StringComparison.OrdinalIgnoreCase);

            _topFieldFirst = !string.Equals(
                scanOrder?.Trim(),
                "Bottom Field First",
                StringComparison.OrdinalIgnoreCase);

            _displayUnitsPerFrame = _isInterlaced ? 2 : 1;
        }
        private static int NormalizeSampleRate(int sampleRate)
        {
            return sampleRate > 0 ? sampleRate : 48000;
        }

        private static BufferedWaveProvider CreateWaveProvider(int sampleRate)
        {
            return new BufferedWaveProvider(new WaveFormat(NormalizeSampleRate(sampleRate), 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(8000),
                DiscardOnBufferOverflow = true
            };
        }

        private void ResetWaveProvider()
        {
            if (_waveProvider.WaveFormat.SampleRate == _audioSampleRate)
            {
                _waveProvider.ClearBuffer();
                return;
            }

            _waveProvider = CreateWaveProvider(_audioSampleRate);
        }

        private void LoadFFmpegFromConfig()
        {
            try
            {
                var config = AppConfigService.Load();
                if (!string.IsNullOrWhiteSpace(config.FFmpegPath))
                    ffmpeg.RootPath = config.FFmpegPath;

                ValidateFFmpegRuntime();
            }
            catch (Exception ex)
            {
                _ffmpegRuntimeError = "FFmpeg 初始化失敗：" + ex.Message;
            }
        }

        public Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0, float rate = 1.0f, double fps = 0, int sampleRate = 48000)
        {
            if (!string.IsNullOrWhiteSpace(_ffmpegRuntimeError))
                throw new InvalidOperationException(_ffmpegRuntimeError);

            fps = fps > 0 ? fps : CurrentFps;

            LoadForBufferedPlayback(path, audioCount, startTimeMs, rate, fps, sampleRate);
            return WaitForFrameBufferAsync(FrameFromTimeMs(startTimeMs, fps), 3000);
        }

        private void ValidateFFmpegRuntime()
        {
            int avCodecMajor = GetFFmpegMajorVersion(ffmpeg.avcodec_version());
            int avFormatMajor = GetFFmpegMajorVersion(ffmpeg.avformat_version());
            int avUtilMajor = GetFFmpegMajorVersion(ffmpeg.avutil_version());

            if (avCodecMajor < 60 || avFormatMajor < 60 || avUtilMajor < 58)
            {
                _ffmpegRuntimeError =
                    $"FFmpeg 版本太舊，無法播放。目前載入的是 libavcodec {avCodecMajor}, libavformat {avFormatMajor}, libavutil {avUtilMajor}；請改用 FFmpeg 8.x shared/full build，config 的 FFmpegPath 要指到 bin 資料夾。";
            }
        }

        private int GetFFmpegMajorVersion(uint version)
        {
            return (int)(version >> 16);
        }




        private void LoadForBufferedPlayback(string path, int audioCount, long startTimeMs, float rate, double fps, int sampleRate)
        {
            StopAudioBridge();
            CurrentPath = path;
            CurrentAudioCount = audioCount;
            _audioFps = fps > 0 ? fps : CurrentFps;
            _audioSampleRate = NormalizeSampleRate(sampleRate);
            _pcmOutputChannels = Math.Clamp(audioCount, 1, ChannelMask.Length);
            ResetWaveProvider();

            long startFrame = FrameFromTimeMs(startTimeMs, _audioFps);
            _totalVideoFrames = ProbeVideoFrameCount(path, _audioFps);

            lock (_lock)
            {
                ClearVideoFrameCacheLocked();
                _currentFrameIndex = DisplayIndexFromMediaFrame(Math.Clamp(startFrame, 0, Math.Max(0, _totalVideoFrames - 1)));
                _playbackStartFrame = _currentFrameIndex;
            }

            SetVideoRate(rate);
            SeekVideoByFrame(startFrame);
            StartAudioCacheFromFrame(startFrame, _audioFps, rate, false);
        }

        private void StartAudioCacheFromFrame(long frameIndex, double fps, float rate, bool keepPlaying, bool waitForPreviousCache = true)
        {
            lock (_audioCacheLock)
            {
                if (string.IsNullOrEmpty(CurrentPath)) return;

                _audioCacheCts?.Cancel();

                if (waitForPreviousCache)
                    WaitForTaskQuietly(_audioCacheTask, 500);

                _audioCacheCts?.Dispose();
                _audioCacheCts = new CancellationTokenSource();
                _audioCacheGeneration++;

                try { _waveOut?.Stop(); } catch { }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;

                _fileAudioProvider?.Dispose();
                _fileAudioProvider = null;
                _slidingAudioProvider = null;
                _reverseAudioProvider = null;
                _reverseSegment = null;

                if (!string.IsNullOrEmpty(_pcmCachePath))
                {
                    try { File.Delete(_pcmCachePath); } catch { }
                    _pcmCachePath = null;
                }

                float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;
                bool reverse = effectiveRate < 0;
                long cacheStartFrame = frameIndex;
                if (!reverse)
                    _forwardAudioCacheRate = effectiveRate;

                if (reverse)
                {
                    StartReverseSlidingAudioCacheFromFrame(frameIndex, fps, effectiveRate, keepPlaying);
                    return;
                }

                _pcmCachePath = Path.Combine(Path.GetTempPath(), $"MxfPlayer_{Guid.NewGuid():N}.pcm");
                using (File.Create(_pcmCachePath)) { }

                long baseTimeMs = TimeMsFromFrame(cacheStartFrame, fps);

                int cacheChannelCount = Math.Clamp(_pcmOutputChannels > 0 ? _pcmOutputChannels : CurrentAudioCount, 1, ChannelMask.Length);
                _fileAudioProvider = new MxfAudioProvider(_pcmCachePath, cacheChannelCount, baseTimeMs, _audioSampleRate)
                {
                    PlaybackRate = GetForwardAudioProviderRate(effectiveRate),
                    TempoRate = GetForwardAudioTempoRate(effectiveRate),
                    Mask = ChannelMask
                };

                _fileAudioProvider.SeekFrame(frameIndex, fps);

                _waveOut = new WaveOutEvent
                {
                    DesiredLatency = AudioOutputLatencyMs,
                    NumberOfBuffers = AudioOutputBufferCount
                };
                _waveOut.Init(_fileAudioProvider);
                var waveOut = _waveOut;
                int generation = _audioCacheGeneration;

                var token = _audioCacheCts.Token;
                string pcmPath = _pcmCachePath;
                string path = CurrentPath;

                int? maxDurationMs = reverse
                    ? (int)Math.Ceiling(ReverseAudioCacheWindowMs * Math.Abs(effectiveRate) + GetReverseAudioRefreshBehindMs(effectiveRate))
                    : null;

                _audioCacheTask = Task.Factory.StartNew(() =>
                {
                    CreatePcmCacheFile(path, pcmPath, cacheStartFrame, fps, token, maxDurationMs, GetForwardAudioTempoRate(effectiveRate));
                }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                if (keepPlaying)
                {
                    Task.Run(() =>
                    {
                        WaitForAudioBuffer(frameIndex, fps, effectiveRate, 1000);
                        PlayWaveOutIfCurrent(waveOut, generation);
                    });
                }
            }
        }

        private void StartReverseSlidingAudioCacheFromFrame(long frameIndex, double fps, float rate, bool keepPlaying)
        {
            float effectiveRate = rate < 0 ? rate : -Math.Max(1.0f, Math.Abs(rate));
            long reverseWindowFrames = Math.Min(ReverseSegmentFrames, Math.Max(30, GetMaxCachedVideoFramesForRate(effectiveRate) - 24));
            long cacheStartFrame = Math.Max(0, frameIndex - reverseWindowFrames + 1);
            long cacheEndFrame = ClampFrameIndex(frameIndex);
            int cacheChannelCount = Math.Clamp(_pcmOutputChannels > 0 ? _pcmOutputChannels : CurrentAudioCount, 1, ChannelMask.Length);

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = AudioOutputLatencyMs,
                NumberOfBuffers = AudioOutputBufferCount
            };
            var waveOut = _waveOut;
            int generation = _audioCacheGeneration;

            var token = _audioCacheCts?.Token ?? CancellationToken.None;
            string path = CurrentPath;
            int maxDurationMs = (int)Math.Ceiling((cacheEndFrame - cacheStartFrame + 1) * 1000.0 / Math.Max(1.0, fps)) + 500;

            _audioCacheTask = Task.Factory.StartNew(() =>
            {
                double tempoRate = GetReverseAudioTempoRate(effectiveRate);
                byte[] pcm = DecodePcmCacheBytes(path, cacheStartFrame, fps, token, maxDurationMs, clearMeterSamples: !keepPlaying, audioTempoRate: (float)tempoRate);
                if (token.IsCancellationRequested || generation != _audioCacheGeneration)
                    return;

                var provider = new ReverseSegmentAudioProvider(
                    pcm,
                    cacheChannelCount,
                    _audioSampleRate,
                    cacheStartFrame,
                    cacheEndFrame,
                    fps,
                    effectiveRate,
                    tempoRate)
                {
                    Mask = ChannelMask
                };
                provider.SeekFrame(frameIndex);

                lock (_audioCacheLock)
                {
                    if (token.IsCancellationRequested || generation != _audioCacheGeneration || !ReferenceEquals(waveOut, _waveOut))
                        return;

                    _reverseAudioProvider = provider;
                    _memoryAudioProvider = null;
                    _slidingAudioProvider = null;
                    _reverseSegment = new ReversePlaybackSegment
                    {
                        StartFrame = cacheStartFrame,
                        EndFrame = cacheEndFrame,
                        Rate = effectiveRate,
                        AudioProvider = provider
                    };
                    waveOut.Init(provider);
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            if (keepPlaying)
            {
                Task.Run(() =>
                {
                    WaitForAudioBuffer(frameIndex, fps, effectiveRate, 2000);
                    PlayWaveOutIfCurrent(waveOut, generation);
                });
            }
        }

        private void StartReverseSlidingPrepend(long frameIndex, double fps, float rate)
        {
            var provider = _slidingAudioProvider;
            if (provider == null || string.IsNullOrEmpty(CurrentPath))
                return;

            float effectiveRate = rate < 0 ? rate : -Math.Max(1.0f, Math.Abs(rate));
            long reverseWindowFrames = FrameFromTimeMs((long)(ReverseAudioCacheWindowMs * Math.Abs(effectiveRate)), fps);
            long cacheStartFrame = Math.Max(0, frameIndex - reverseWindowFrames);
            var token = _audioCacheCts?.Token ?? CancellationToken.None;
            string path = CurrentPath;
            int generation = _audioCacheGeneration;
            int refreshBehindMs = GetReverseAudioRefreshBehindMs(effectiveRate);
            int maxDurationMs = (int)Math.Ceiling(ReverseAudioCacheWindowMs * Math.Abs(effectiveRate) + refreshBehindMs);

            _audioCacheTask = Task.Factory.StartNew(() =>
            {
                double tempoRate = GetReverseAudioTempoRate(effectiveRate);
                byte[] pcm = DecodePcmCacheBytes(path, cacheStartFrame, fps, token, maxDurationMs, clearMeterSamples: false, audioTempoRate: (float)tempoRate);
                if (token.IsCancellationRequested || generation != _audioCacheGeneration)
                    return;

                provider.PrependWindow(cacheStartFrame, fps, pcm, tempoRate);
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private float GetForwardAudioTempoRate(float rate)
        {
            return rate > 0 ? rate : 1.0f;
        }

        private float GetForwardAudioProviderRate(float rate)
        {
            return rate > 0 ? 1.0f : rate;
        }

        private double GetReverseAudioTempoRate(float rate)
        {
            return Math.Max(1.0, Math.Abs(rate));
        }

        private float GetReverseAudioProviderRate(float rate)
        {
            return rate < 0 ? -1.0f : 1.0f;
        }

        private int GetReverseAudioRefreshBehindMs(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int scaled = (int)Math.Ceiling(ReverseAudioCacheRefreshBehindMs * multiplier * 2.0);
            return Math.Max(ReverseAudioMinRefreshBehindMs, scaled);
        }

        private sealed class ReversePlaybackSegment
        {
            public long StartFrame { get; init; }
            public long EndFrame { get; init; }
            public float Rate { get; init; }
            public ReverseSegmentAudioProvider? AudioProvider { get; init; }

            public bool Contains(long frameIndex, float rate)
            {
                return frameIndex >= StartFrame &&
                       frameIndex <= EndFrame &&
                       Math.Abs(Rate - rate) <= 0.001f;
            }
        }

        private int GetInitialReverseAudioBehindMs(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int scaled = (int)Math.Ceiling(ReverseAudioInitialPlaybackMs * multiplier);
            return Math.Clamp(scaled, ReverseAudioInitialPlaybackMs, ReverseAudioInitialMaxSourceMs);
        }

        private static void WaitForTaskQuietly(Task? task, int timeoutMs)
        {
            if (task == null)
                return;

            try
            {
                task.Wait(timeoutMs);
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool PlayWaveOutIfCurrent(WaveOutEvent? waveOut, int generation)
        {
            if (waveOut == null) return false;

            lock (_audioCacheLock)
            {
                if (generation != _audioCacheGeneration || !ReferenceEquals(waveOut, _waveOut))
                    return false;

                try
                {
                    bool wasPlaying = waveOut.PlaybackState == PlaybackState.Playing;
                    waveOut.Play();
                    if (!wasPlaying)
                        ResetMediaClock(_currentFrameIndex, waveOut);
                    return true;
                }
                catch (ObjectDisposedException) { return false; }
                catch (NullReferenceException ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Audio Play ignored] " + ex.Message);
                    return false;
                }
                catch (InvalidOperationException ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Audio Play ignored] " + ex.Message);
                    return false;
                }
            }
        }

        private void ResetMediaClock(long frameIndex)
        {
            ResetMediaClock(frameIndex, _waveOut);
        }

        private void ResetMediaClock(long displayIndex, WaveOutEvent? waveOut)
        {
            _playbackStartFrame = ClampDisplayIndex(displayIndex);
            _audioClockStartFrame = _playbackStartFrame;
            _audioClockStartBytes = GetWaveOutPositionBytes(waveOut);
            _playbackClock.Restart();
        }

        private long GetWaveOutPositionBytes(WaveOutEvent? waveOut)
        {
            try
            {
                return waveOut?.GetPosition() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private double GetMasterClockElapsedMs()
        {
            var waveOut = _waveOut;
            if (_videoRate >= 0 &&
                waveOut != null &&
                waveOut.PlaybackState == PlaybackState.Playing &&
                _audioSampleRate > 0)
            {
                long currentBytes = GetWaveOutPositionBytes(waveOut);
                long elapsedBytes = Math.Max(0, currentBytes - _audioClockStartBytes);
                return elapsedBytes * 1000.0 / (_audioSampleRate * 4.0);
            }

            return Math.Max(0, _playbackClock.Elapsed.TotalMilliseconds);
        }

        private bool WaitForAudioBuffer(long frameIndex, double fps, float rate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_fileAudioProvider == null && _slidingAudioProvider == null && _reverseAudioProvider == null)
                {
                    if (rate >= 0 || _audioCacheTask == null || _audioCacheTask.IsCompleted)
                        return false;

                    Thread.Sleep(20);
                    continue;
                }

                bool available;
                if (rate < 0)
                {
                    available = _reverseAudioProvider != null
                        ? _reverseAudioProvider.HasFrameData(frameIndex, 500)
                        : _slidingAudioProvider != null
                            ? _slidingAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 500)
                            : _fileAudioProvider != null && _fileAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 500);
                }
                else
                {
                    available = _fileAudioProvider != null && _fileAudioProvider.IsFrameDataAvailable(frameIndex, fps, AudioStallResumeBufferMs);
                }

                if (available)
                    return true;

                Thread.Sleep(20);
            }

            return false;
        }

        public Task<bool> WaitForAudioBufferAsync(long frameIndex, double fps, float rate, int timeoutMs)
        {
            return Task.Run(() => WaitForAudioBuffer(frameIndex, fps, rate, timeoutMs));
        }

        private void CreatePcmCacheFile(string path, string pcmPath, long startFrame, double fps, CancellationToken token, int? maxDurationMs = null, float audioTempoRate = 1.0f)
        {
            lock (_ffmpegResourceLock)
            {
                try
                {
                    lock (_meterSamplesLock)
                    {
                        _meterSamples.Clear();
                    }

                    InitFFmpeg(path, audioTempoRate);

                    if (_formatContext != null && startFrame > 0)
                    {
                        long startTimeMs = TimeMsFromFrame(startFrame, fps);
                        long seekTarget = startTimeMs * ffmpeg.AV_TIME_BASE / 1000;

                        if (ffmpeg.av_seek_frame(_formatContext, -1, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                        {
                            foreach (var decoder in _audioDecoders.Values)
                            {
                                if (decoder.Ptr != null)
                                    ffmpeg.avcodec_flush_buffers(decoder.Ptr);
                            }
                        }
                    }

                    using (var output = new FileStream(pcmPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    {
                        DecodeToFile(output, token, maxDurationMs, TimeMsFromFrame(startFrame, fps));
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SEHException ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AudioCache SEH] " + ex.Message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AudioCache Error] " + ex.Message);
                }
                finally
                {
                    CloseDecodeResources();
                }
            }
        }

        private byte[] DecodePcmCacheBytes(
            string path,
            long startFrame,
            double fps,
            CancellationToken token,
            int maxDurationMs,
            bool clearMeterSamples,
            float audioTempoRate = 1.0f)
        {
            lock (_ffmpegResourceLock)
            {
                try
                {
                    if (clearMeterSamples)
                    {
                        lock (_meterSamplesLock)
                        {
                            _meterSamples.Clear();
                        }
                    }

                    InitFFmpeg(path, audioTempoRate);

                    if (_formatContext != null && startFrame > 0)
                    {
                        long startTimeMs = TimeMsFromFrame(startFrame, fps);
                        long seekTarget = startTimeMs * ffmpeg.AV_TIME_BASE / 1000;

                        if (ffmpeg.av_seek_frame(_formatContext, -1, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                        {
                            foreach (var decoder in _audioDecoders.Values)
                            {
                                if (decoder.Ptr != null)
                                    ffmpeg.avcodec_flush_buffers(decoder.Ptr);
                            }
                        }
                    }

                    using var output = new MemoryStream();
                    DecodeToFile(output, token, maxDurationMs, TimeMsFromFrame(startFrame, fps));
                    return output.ToArray();
                }
                catch (OperationCanceledException)
                {
                    return Array.Empty<byte>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AudioCache Memory Error] " + ex.Message);
                    return Array.Empty<byte>();
                }
                finally
                {
                    CloseDecodeResources();
                }
            }
        }

        public Task WaitForFrameBufferAsync(long frameIndex, int timeoutMs = 3000)
        {
            return Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                long displayIndex = DisplayIndexFromMediaFrame(ClampFrameIndex(frameIndex));

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_lock)
                    {
                        if (_videoFrameCache.ContainsKey(displayIndex))
                            return;
                    }

                    EnsureVideoDecoderNearCurrentFrame();
                    Thread.Sleep(25);
                }
            });
        }

        public Task<bool> WaitForVideoBufferAheadAsync(long frameIndex, int requiredAheadFrames, int timeoutMs = 3000)
        {
            return Task.Run(() =>
            {
                if (requiredAheadFrames <= 0)
                    return true;

                var sw = Stopwatch.StartNew();
                long clampedFrame = DisplayIndexFromMediaFrame(Math.Max(0, frameIndex));
                int requiredBufferedFrames = Math.Min(requiredAheadFrames * DisplayUnitsPerFrame, GetMaxCachedVideoFramesForRate(_videoRate));
                long requiredAhead = Math.Max(0, requiredBufferedFrames - 1);

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_lock)
                    {
                        long continuousFrame = GetContinuousCachedFrameLimit(clampedFrame, true);
                        if (continuousFrame >= 0 &&
                            continuousFrame - clampedFrame >= requiredAhead)
                        {
                            return true;
                        }
                    }

                    EnsureVideoDecoderNearCurrentFrame();
                    Thread.Sleep(25);
                }

                return false;
            });
        }

        private long ProbeVideoFrameCount(string path, double fps)
        {
            AVFormatContext* formatContext = null;
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0)
                return 0;

            try
            {
                ffmpeg.avformat_find_stream_info(formatContext, null);

                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    var stream = formatContext->streams[i];
                    if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                        continue;

                    if (stream->nb_frames > 0)
                        return stream->nb_frames;

                    long duration = stream->duration;
                    AVRational timeBase = stream->time_base;
                    if (duration <= 0 && formatContext->duration > 0)
                    {
                        duration = formatContext->duration;
                        timeBase = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
                    }

                    if (duration > 0)
                    {
                        double seconds = duration * ffmpeg.av_q2d(timeBase);
                        return Math.Max(1, (long)Math.Ceiling(seconds * fps));
                    }
                }
            }
            finally
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            return 0;
        }

        private Bitmap CreateBitmapFromFrame(AVFrame* sourceFrame, SwsContext* swsContext, int width, int height, bool deinterlaceFrame = false, int fieldOffset = 0)
        {
            var bitmap = RentVideoBitmap(width, height);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);

            try
            {
                int targetStride = data.Stride;
                int rowBytes = width * 4;

                AVFrame bitmapFrame = default;
                bitmapFrame.data[0] = (byte*)data.Scan0;
                bitmapFrame.linesize[0] = targetStride;

                ffmpeg.sws_scale(
                    swsContext,
                    sourceFrame->data,
                    sourceFrame->linesize,
                    0,
                    height,
                    bitmapFrame.data,
                    bitmapFrame.linesize);

                if (!deinterlaceFrame)
                    return bitmap;

                BobDeinterlaceField((byte*)data.Scan0, targetStride, rowBytes, height, fieldOffset);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        private static void BobDeinterlaceField(byte* basePtr, int stride, int rowBytes, int height, int fieldOffset)
        {
            const ulong ByteAverageMask64 = 0xFEFEFEFEFEFEFEFEUL;
            const uint ByteAverageMask32 = 0xFEFEFEFEU;
            int missingFieldOffset = fieldOffset == 0 ? 1 : 0;
            int missingRowCount = (height + 1 - missingFieldOffset) / 2;
            nint baseAddress = (nint)basePtr;

            Parallel.For(0, missingRowCount, VideoBlendParallelOptions, rowIndex =>
            {
                int y = missingFieldOffset + (rowIndex * 2);
                byte* imageBase = (byte*)baseAddress;
                byte* target = imageBase + (y * stride);
                int previousY = y - 1;
                int nextY = y + 1;

                if (previousY < 0 && nextY >= height)
                    return;

                if (previousY < 0)
                {
                    Buffer.MemoryCopy(imageBase + (nextY * stride), target, stride, rowBytes);
                    return;
                }

                if (nextY >= height)
                {
                    Buffer.MemoryCopy(imageBase + (previousY * stride), target, stride, rowBytes);
                    return;
                }

                byte* previous = imageBase + (previousY * stride);
                byte* next = imageBase + (nextY * stride);
                int x = 0;

                for (; x <= rowBytes - sizeof(ulong); x += sizeof(ulong))
                {
                    ulong a = *(ulong*)(previous + x);
                    ulong b = *(ulong*)(next + x);
                    ulong average = (a & b) + (((a ^ b) & ByteAverageMask64) >> 1);

                    *(ulong*)(target + x) = average;
                }

                for (; x <= rowBytes - sizeof(uint); x += sizeof(uint))
                {
                    uint a = *(uint*)(previous + x);
                    uint b = *(uint*)(next + x);
                    uint average = (a & b) + (((a ^ b) & ByteAverageMask32) >> 1);

                    *(uint*)(target + x) = average;
                }
            });
        }

        private Bitmap RentVideoBitmap(int width, int height)
        {
            lock (_lock)
            {
                if (_pooledBitmapWidth == width &&
                    _pooledBitmapHeight == height &&
                    _videoBitmapPool.Count > 0)
                {
                    return _videoBitmapPool.Pop();
                }
            }

            return new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        }

        private void ReleaseVideoBitmap(Bitmap bitmap)
        {
            lock (_lock)
            {
                if (bitmap.Width == _pooledBitmapWidth &&
                    bitmap.Height == _pooledBitmapHeight &&
                    _videoBitmapPool.Count < MaxPooledVideoBitmaps)
                {
                    _videoBitmapPool.Push(bitmap);
                    return;
                }
            }

            bitmap.Dispose();
        }

        private void ClearVideoBitmapPoolLocked()
        {
            foreach (var bitmap in _videoBitmapPool)
                bitmap.Dispose();

            _videoBitmapPool.Clear();
        }

        private static bool ShouldDisplaySingleField(AVFrame* frame)
        {
            return (frame->flags & ffmpeg.AV_FRAME_FLAG_INTERLACED) != 0;
        }

        private static int GetFirstFieldOffset(AVFrame* frame)
        {
            return (frame->flags & ffmpeg.AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0 ? 0 : 1;
        }

        private void DecodeVideoFrameWindow(string path, long startFrame, int decodeGeneration, CancellationToken token, long? endFrameExclusive = null)
        {
            AVFormatContext* formatContext = null;
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0) return;

            AVCodecContext* codecContext = null;
            SwsContext* swsContext = null;
            VideoBwdifFilter? videoFilter = null;
            AVFrame* frame = null;
            AVFrame* filteredFrame = null;
            AVPacket* packet = null;

            try
            {
                ffmpeg.avformat_find_stream_info(formatContext, null);

                int videoStreamIndex = -1;
                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamIndex = i;
                        break;
                    }
                }

                if (videoStreamIndex < 0) return;

                var stream = formatContext->streams[videoStreamIndex];
                var codecParameters = stream->codecpar;
                var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
                if (codec == null) return;

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters);
                codecContext->thread_count = Math.Max(1, Environment.ProcessorCount - 2);
                codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0) return;

                int width = codecContext->width;
                int height = codecContext->height;
                UpdateVideoFrameCacheLimit(width, height);
                swsContext = ffmpeg.sws_getContext(
                    width, height, codecContext->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR0,
                    SwsFastBilinear, null, null, null);

                if (DeinterlacePlaybackVideo || FieldPlaybackMode)
                {
                    AVRational sourceFrameRate = stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0
                        ? stream->avg_frame_rate
                        : stream->r_frame_rate;

                    videoFilter = new VideoBwdifFilter(
                        width,
                        height,
                        codecContext->pix_fmt,
                        stream->time_base,
                        sourceFrameRate,
                        codecContext->sample_aspect_ratio);
                }

                frame = ffmpeg.av_frame_alloc();
                filteredFrame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                long seekFrame = Math.Max(0, startFrame - 3);
                long seekTarget = ffmpeg.av_rescale_q(
                    TimeMsFromFrame(seekFrame, _audioFps),
                    new AVRational { num = 1, den = 1000 },
                    stream->time_base);

                if (ffmpeg.av_seek_frame(formatContext, videoStreamIndex, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                    ffmpeg.avcodec_flush_buffers(codecContext);

                long windowEndFrame = endFrameExclusive ?? (_totalVideoFrames > 0 ? _totalVideoFrames : long.MaxValue);
                long lastWindowFrame = windowEndFrame - 1;
                long fallbackFrameIndex = seekFrame;
                long cachedThroughFrame = startFrame - 1;
                long filterOutputDisplayIndex = -1;

                while (!token.IsCancellationRequested &&
                       IsCurrentVideoDecodeGeneration(decodeGeneration) &&
                       cachedThroughFrame < lastWindowFrame)
                {
                    long readStartTicks = Stopwatch.GetTimestamp();
                    int readResult = ffmpeg.av_read_frame(formatContext, packet);
                    TrackVideoPacketReadPerf(Stopwatch.GetTimestamp() - readStartTicks);
                    if (readResult < 0)
                        break;

                    if (packet->stream_index == videoStreamIndex)
                    {
                        long sendStartTicks = Stopwatch.GetTimestamp();
                        int sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
                        TrackVideoPacketSendPerf(Stopwatch.GetTimestamp() - sendStartTicks);
                        if (sendResult >= 0)
                        {
                            DecodeWindowFrames(
                                codecContext,
                                frame,
                                filteredFrame,
                                videoFilter,
                                swsContext,
                                width,
                                height,
                                stream->time_base,
                                stream->start_time,
                                startFrame,
                                windowEndFrame,
                                ref fallbackFrameIndex,
                                ref cachedThroughFrame,
                                ref filterOutputDisplayIndex,
                                decodeGeneration,
                                token);

                            if (cachedThroughFrame >= lastWindowFrame)
                            {
                                ffmpeg.av_packet_unref(packet);
                                break;
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (filteredFrame != null) ffmpeg.av_frame_free(&filteredFrame);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                videoFilter?.Dispose();
                if (swsContext != null) ffmpeg.sws_freeContext(swsContext);
                if (codecContext != null) ffmpeg.avcodec_free_context(&codecContext);
                if (formatContext != null) ffmpeg.avformat_close_input(&formatContext);
            }
        }

        private void DecodeWindowFrames(
            AVCodecContext* codecContext,
            AVFrame* frame,
            AVFrame* filteredFrame,
            VideoBwdifFilter? videoFilter,
            SwsContext* swsContext,
            int width,
            int height,
            AVRational streamTimeBase,
            long streamStartTime,
            long windowStartFrame,
            long windowEndFrame,
            ref long fallbackFrameIndex,
            ref long cachedThroughFrame,
            ref long filterOutputDisplayIndex,
            int decodeGeneration,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested &&
                   IsCurrentVideoDecodeGeneration(decodeGeneration))
            {
                long receiveStartTicks = Stopwatch.GetTimestamp();
                int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
                TrackVideoReceivePerf(Stopwatch.GetTimestamp() - receiveStartTicks);
                if (receiveResult != 0)
                    break;

                long frameIndex = GetFrameIndexFromTimestamp(frame, streamTimeBase, streamStartTime, fallbackFrameIndex);
                fallbackFrameIndex++;

                if (frameIndex >= windowEndFrame)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }

                long displayIndex = DisplayIndexFromMediaFrame(frameIndex);
                if (frameIndex >= windowStartFrame)
                    WaitForVideoDecodeHeadroom(displayIndex, decodeGeneration, token);

                if (token.IsCancellationRequested || !IsCurrentVideoDecodeGeneration(decodeGeneration))
                {
                    ffmpeg.av_frame_unref(frame);
                    break;
                }

                long bitmapStartTicks = Stopwatch.GetTimestamp();
                if ((DeinterlacePlaybackVideo || FieldPlaybackMode) && videoFilter?.IsReady == true)
                {
                    if (filterOutputDisplayIndex < 0)
                        filterOutputDisplayIndex = displayIndex;

                    long filterInputStartTicks = Stopwatch.GetTimestamp();
                    bool addedToFilter = videoFilter.AddFrame(frame);
                    TrackVideoFilterInputPerf(Stopwatch.GetTimestamp() - filterInputStartTicks);
                    if (addedToFilter)
                    {
                        while (true)
                        {
                            long filterOutputStartTicks = Stopwatch.GetTimestamp();
                            bool receivedFilteredFrame = videoFilter.TryReceiveFrame(filteredFrame);
                            TrackVideoFilterOutputPerf(
                                Stopwatch.GetTimestamp() - filterOutputStartTicks,
                                receivedFilteredFrame);
                            if (!receivedFilteredFrame)
                                break;

                            long filteredDisplayIndex;
                            if (FieldPlaybackMode)
                            {
                                filteredDisplayIndex = filterOutputDisplayIndex;
                                filterOutputDisplayIndex++;
                            }
                            else
                            {
                                long filteredFrameIndex = GetFrameIndexFromTimestamp(
                                    filteredFrame,
                                    streamTimeBase,
                                    streamStartTime,
                                    frameIndex);
                                filteredDisplayIndex = DisplayIndexFromMediaFrame(filteredFrameIndex);
                                filterOutputDisplayIndex = Math.Max(filterOutputDisplayIndex + 1, filteredDisplayIndex + 1);
                            }
                            long filteredMediaFrameIndex = MediaFrameFromDisplayIndex(filteredDisplayIndex);

                            if (filteredMediaFrameIndex >= windowStartFrame &&
                                filteredDisplayIndex < TotalDisplayUnits &&
                                filteredMediaFrameIndex < windowEndFrame &&
                                ShouldCacheVideoFrame(filteredDisplayIndex, decodeGeneration))
                            {
                                long fieldBitmapStartTicks = Stopwatch.GetTimestamp();
                                Bitmap bitmap = CreateBitmapFromFrame(filteredFrame, swsContext, width, height);
                                long fieldBitmapTicks = Stopwatch.GetTimestamp() - fieldBitmapStartTicks;
                                long fieldCacheStartTicks = Stopwatch.GetTimestamp();
                                bool addedToCache = AddVideoFrameToCache(filteredDisplayIndex, bitmap, decodeGeneration);
                                long fieldCacheTicks = Stopwatch.GetTimestamp() - fieldCacheStartTicks;
                                if (addedToCache)
                                    TrackVideoDecodePerf(fieldBitmapTicks, fieldCacheTicks);
                            }

                            if (filteredDisplayIndex >= DisplayIndexFromMediaFrame(windowStartFrame) &&
                                filteredDisplayIndex < DisplayIndexFromMediaFrame(windowEndFrame))
                            {
                                cachedThroughFrame = Math.Max(
                                    cachedThroughFrame,
                                    filteredMediaFrameIndex);
                            }

                            ffmpeg.av_frame_unref(filteredFrame);
                        }
                    }
                }
                else
                {
                    if (frameIndex < windowStartFrame)
                    {
                        ffmpeg.av_frame_unref(frame);
                        continue;
                    }

                    if (!ShouldCacheVideoFrame(displayIndex, decodeGeneration))
                    {
                        ffmpeg.av_frame_unref(frame);
                        continue;
                    }

                    Bitmap bitmap = CreateBitmapFromFrame(frame, swsContext, width, height);
                    long bitmapTicks = Stopwatch.GetTimestamp() - bitmapStartTicks;
                    long cacheStartTicks = Stopwatch.GetTimestamp();
                    bool addedToCache = AddVideoFrameToCache(displayIndex, bitmap, decodeGeneration);
                    long cacheTicks = Stopwatch.GetTimestamp() - cacheStartTicks;

                    if (addedToCache)
                    {
                        cachedThroughFrame = Math.Max(cachedThroughFrame, frameIndex);
                        TrackVideoDecodePerf(bitmapTicks, cacheTicks);
                    }
                }

                ffmpeg.av_frame_unref(frame);
            }
        }

        private void TrackVideoPacketReadPerf(long readTicks)
        {
            Interlocked.Increment(ref _videoDecodePerfPackets);
            Interlocked.Add(ref _videoDecodePerfReadTicks, readTicks);
        }

        private void TrackVideoPacketSendPerf(long sendTicks)
        {
            Interlocked.Increment(ref _videoDecodePerfSendPackets);
            Interlocked.Add(ref _videoDecodePerfSendTicks, sendTicks);
        }

        private void TrackVideoReceivePerf(long receiveTicks)
        {
            Interlocked.Increment(ref _videoDecodePerfReceiveFrames);
            Interlocked.Add(ref _videoDecodePerfReceiveTicks, receiveTicks);
        }

        private void TrackVideoFilterInputPerf(long filterInputTicks)
        {
            Interlocked.Increment(ref _videoDecodePerfFilterInputFrames);
            Interlocked.Add(ref _videoDecodePerfFilterInputTicks, filterInputTicks);
        }

        private void TrackVideoFilterOutputPerf(long filterOutputTicks, bool receivedFrame)
        {
            if (receivedFrame)
                Interlocked.Increment(ref _videoDecodePerfFilterOutputFrames);
            Interlocked.Add(ref _videoDecodePerfFilterOutputTicks, filterOutputTicks);
        }

        private void TrackVideoDecodePerf(long bitmapTicks, long cacheTicks)
        {
            long frames = Interlocked.Increment(ref _videoDecodePerfFrames);
            Interlocked.Add(ref _videoDecodePerfBitmapTicks, bitmapTicks);
            Interlocked.Add(ref _videoDecodePerfCacheTicks, cacheTicks);

            if (_videoDecodePerfClock.ElapsedMilliseconds < 1000)
                return;

            lock (_lock)
            {
                if (_videoDecodePerfClock.ElapsedMilliseconds < 1000)
                    return;

                double elapsedSeconds = Math.Max(0.001, _videoDecodePerfClock.Elapsed.TotalSeconds);
                long totalPackets = Interlocked.Exchange(ref _videoDecodePerfPackets, 0);
                long totalReadTicks = Interlocked.Exchange(ref _videoDecodePerfReadTicks, 0);
                long totalSendPackets = Interlocked.Exchange(ref _videoDecodePerfSendPackets, 0);
                long totalSendTicks = Interlocked.Exchange(ref _videoDecodePerfSendTicks, 0);
                long totalReceiveFrames = Interlocked.Exchange(ref _videoDecodePerfReceiveFrames, 0);
                long totalReceiveTicks = Interlocked.Exchange(ref _videoDecodePerfReceiveTicks, 0);
                long totalFilterInputFrames = Interlocked.Exchange(ref _videoDecodePerfFilterInputFrames, 0);
                long totalFilterInputTicks = Interlocked.Exchange(ref _videoDecodePerfFilterInputTicks, 0);
                long totalFilterOutputFrames = Interlocked.Exchange(ref _videoDecodePerfFilterOutputFrames, 0);
                long totalFilterOutputTicks = Interlocked.Exchange(ref _videoDecodePerfFilterOutputTicks, 0);
                long totalFrames = Interlocked.Exchange(ref _videoDecodePerfFrames, 0);
                long totalBitmapTicks = Interlocked.Exchange(ref _videoDecodePerfBitmapTicks, 0);
                long totalCacheTicks = Interlocked.Exchange(ref _videoDecodePerfCacheTicks, 0);
                long totalThrottleTicks = Interlocked.Exchange(ref _videoDecodePerfThrottleTicks, 0);
                double throttleSeconds = totalThrottleTicks / (double)Stopwatch.Frequency;
                double activeSeconds = Math.Max(0.001, elapsedSeconds - throttleSeconds);
                double readMs = AverageMilliseconds(totalReadTicks, totalPackets);
                double sendMs = AverageMilliseconds(totalSendTicks, totalSendPackets);
                double receiveMs = AverageMilliseconds(totalReceiveTicks, totalReceiveFrames);
                double filterInMs = AverageMilliseconds(totalFilterInputTicks, totalFilterInputFrames);
                double filterOutMs = AverageMilliseconds(totalFilterOutputTicks, totalFilterOutputFrames);
                double bitmapMs = totalFrames > 0
                    ? totalBitmapTicks * 1000.0 / Stopwatch.Frequency / totalFrames
                    : 0;
                double cacheMs = AverageMilliseconds(totalCacheTicks, totalFrames);

                Debug.WriteLine(
                    $"[VideoDecodePerf] fps={totalFrames / elapsedSeconds:0.0} " +
                    $"activeFps={totalFrames / activeSeconds:0.0} " +
                    $"readMs={readMs:0.00} sendMs={sendMs:0.00} receiveMs={receiveMs:0.00} " +
                    $"filterInMs={filterInMs:0.00} filterOutMs={filterOutMs:0.00} " +
                    $"bitmapMs={bitmapMs:0.00} cacheMs={cacheMs:0.00} " +
                    $"throttleMs={throttleSeconds * 1000.0:0} frames={totalFrames} packets={totalPackets}");

                _videoDecodePerfClock.Restart();
            }
        }

        private static double AverageMilliseconds(long ticks, long count)
        {
            return count > 0
                ? ticks * 1000.0 / Stopwatch.Frequency / count
                : 0;
        }

        private void WaitForVideoDecodeHeadroom(long frameIndex, int decodeGeneration, CancellationToken token)
        {
            if (_videoRate < 0)
                return;

            while (!token.IsCancellationRequested && IsCurrentVideoDecodeGeneration(decodeGeneration))
            {
                long currentFrame;
                lock (_lock)
                    currentFrame = _currentFrameIndex;

                if (frameIndex - currentFrame < GetForwardHighWaterDisplayFrames(_videoRate))
                    return;

                long throttleStartTicks = Stopwatch.GetTimestamp();
                Thread.Sleep(10);
                Interlocked.Add(ref _videoDecodePerfThrottleTicks, Stopwatch.GetTimestamp() - throttleStartTicks);
            }
        }

        private long GetFrameIndexFromTimestamp(AVFrame* frame, AVRational streamTimeBase, long streamStartTime, long fallbackFrameIndex)
        {
            if (frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE)
                return fallbackFrameIndex;

            long timestamp = frame->best_effort_timestamp;
            if (streamStartTime != ffmpeg.AV_NOPTS_VALUE)
                timestamp -= streamStartTime;

            long timeMs = ffmpeg.av_rescale_q(
                timestamp,
                streamTimeBase,
                new AVRational { num = 1, den = 1000 });

            return FrameFromTimeMs(timeMs, _audioFps);
        }

        private bool IsCurrentVideoDecodeGeneration(int decodeGeneration)
        {
            return Volatile.Read(ref _videoDecodeGeneration) == decodeGeneration;
        }

        private bool AddVideoFrameToCache(long frameIndex, Bitmap bitmap, int decodeGeneration)
        {
            List<Bitmap>? framesToRelease = null;
            bool addedToCache = false;

            lock (_lock)
            {
                if (!ShouldCacheVideoFrameLocked(frameIndex, decodeGeneration))
                {
                    framesToRelease = new List<Bitmap>(capacity: 1) { bitmap };
                }
                else
                {
                    _videoFrameCache[frameIndex] = bitmap;
                    addedToCache = true;

                    int maxCachedFrames = GetMaxCachedVideoFramesForRate(_videoRate);
                    int trimTriggerFrames = maxCachedFrames + Math.Max(12, maxCachedFrames / 10);
                    if (_videoFrameCache.Count > trimTriggerFrames)
                    {
                        framesToRelease = new List<Bitmap>();
                        TrimVideoFrameCacheLocked(maxCachedFrames, framesToRelease);
                    }
                }
            }

            ReleaseVideoBitmaps(framesToRelease);
            return addedToCache;
        }

        private bool ShouldCacheVideoFrame(long frameIndex, int decodeGeneration)
        {
            lock (_lock)
                return ShouldCacheVideoFrameLocked(frameIndex, decodeGeneration);
        }

        private bool ShouldCacheVideoFrameLocked(long frameIndex, int decodeGeneration)
        {
            return IsCurrentVideoDecodeGeneration(decodeGeneration) &&
                   !IsForwardFrameTooFarAheadLocked(frameIndex) &&
                   !_videoFrameCache.ContainsKey(frameIndex);
        }

        private bool IsForwardFrameTooFarAheadLocked(long frameIndex)
        {
            if (_videoRate < 0)
                return false;

            int horizonFrames = GetForwardDecodeWindowDisplayFrames(_videoRate);
            return frameIndex > _currentFrameIndex + horizonFrames;
        }

        private void UpdateVideoFrameCacheLimit(int width, int height)
        {
            long frameBytes = Math.Max(1L, width) * Math.Max(1L, height) * 4L;
            int targetFrames = (int)Math.Clamp(
                VideoFrameCacheBudgetBytes / frameBytes,
                MinCachedVideoFrames,
                MaxCachedVideoFrames);
            List<Bitmap>? framesToRelease = null;

            lock (_lock)
            {
                if (_pooledBitmapWidth != width || _pooledBitmapHeight != height)
                {
                    ClearVideoBitmapPoolLocked();
                    _pooledBitmapWidth = width;
                    _pooledBitmapHeight = height;
                }

                _maxCachedVideoFrames = targetFrames;
                if (_videoFrameCache.Count > targetFrames)
                {
                    framesToRelease = new List<Bitmap>();
                    TrimVideoFrameCacheLocked(targetFrames, framesToRelease);
                }
            }

            ReleaseVideoBitmaps(framesToRelease);
        }

        private void TrimVideoFrameCacheLocked(int maxCachedFrames, List<Bitmap> framesToRelease)
        {
            while (_videoFrameCache.Count > maxCachedFrames)
            {
                long victimIndex = FindVideoCacheTrimVictimLocked();
                if (victimIndex == long.MinValue)
                    break;

                if (_videoFrameCache.Remove(victimIndex, out var oldFrame))
                    framesToRelease.Add(oldFrame);
            }
        }

        private long FindVideoCacheTrimVictimLocked()
        {
            long victimIndex = long.MinValue;

            if (_videoRate >= 0)
            {
                long keepAfterFrame = _currentFrameIndex - 3;
                foreach (long index in _videoFrameCache.Keys)
                {
                    if (index == _displayedVideoFrameIndex || index >= keepAfterFrame)
                        continue;

                    if (victimIndex == long.MinValue || index < victimIndex)
                        victimIndex = index;
                }
            }
            else
            {
                long keepBeforeFrame = _currentFrameIndex + 3;
                foreach (long index in _videoFrameCache.Keys)
                {
                    if (index == _displayedVideoFrameIndex || index <= keepBeforeFrame)
                        continue;

                    if (victimIndex == long.MinValue || index > victimIndex)
                        victimIndex = index;
                }
            }

            if (victimIndex != long.MinValue)
                return victimIndex;

            long farthestDistance = -1;
            foreach (long index in _videoFrameCache.Keys)
            {
                if (index == _displayedVideoFrameIndex)
                    continue;

                long distance = Math.Abs(index - _currentFrameIndex);
                if (distance < 3 || distance <= farthestDistance)
                    continue;

                farthestDistance = distance;
                victimIndex = index;
            }

            return victimIndex;
        }

        private void ReleaseVideoBitmaps(List<Bitmap>? frames)
        {
            if (frames == null)
                return;

            foreach (var frame in frames)
                ReleaseVideoBitmap(frame);
        }

        private void ClearVideoFrameCacheLocked()
        {
            foreach (var frame in _videoFrameCache.Values)
                ReleaseVideoBitmap(frame);

            _videoFrameCache.Clear();
        }

        private void InitFFmpeg(string path, float rate)
        {
            AVFormatContext* pFormatContext = null;
            if (ffmpeg.avformat_open_input(&pFormatContext, path, null, null) < 0) return;
            _formatContext = pFormatContext;

            ffmpeg.avformat_find_stream_info(_formatContext, null);

            _audioStreamIndices.Clear();
            _audioDecoders.Clear();

            for (int i = 0; i < _formatContext->nb_streams; i++)
            {
                var stream = _formatContext->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                    var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                    ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar);
                    ffmpeg.avcodec_open2(codecCtx, codec, null);

                    _audioStreamIndices.Add(i);
                    _audioDecoders[i] = new PointerWrapper<AVCodecContext> { Ptr = codecCtx };
                    if (_audioStreamIndices.Count >= 8) break;
                }
            }

            _activeAudioTrackCount = _audioStreamIndices.Count;
            UpdateFilterGraph(rate);

        }
        private readonly float[] _channelLevels = new float[8];
        private readonly object _levelLock = new();

        public float GetChannelLevel(int index)
        {
            if (index < 0 || index >= _channelLevels.Length) return 0f;

            lock (_levelLock)
            {
                return _channelLevels[index];
            }
        }
        public void UpdateFilterGraph(float rate)
        {
            lock (_lock)
            {
                _filterReady = false;

                // Clear buffered audio so the next output matches the current mix settings.
                _waveProvider?.ClearBuffer();

                if (_filterGraph != null)
                {
                    var tmp = _filterGraph;
                    ffmpeg.avfilter_graph_free(&tmp);
                    _filterGraph = null;
                }

                if (_srcContexts != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_srcContexts);
                    _srcContexts = null;
                }

                if (_activeAudioTrackCount == 0 || _audioDecoders.Count == 0) return;

                _filterGraph = ffmpeg.avfilter_graph_alloc();
                _srcContexts = (AVFilterContext**)Marshal.AllocHGlobal(sizeof(AVFilterContext*) * _activeAudioTrackCount);

                AVFilter* abuffer = ffmpeg.avfilter_get_by_name("abuffer");
                AVFilterInOut* outputs = null;

                for (int i = 0; i < _activeAudioTrackCount; i++)
                {
                    int streamIdx = _audioStreamIndices[i];
                    var ctx = _audioDecoders[streamIdx].Ptr;

                    string sampleFormat = ffmpeg.av_get_sample_fmt_name(ctx->sample_fmt) ?? "";
                    string channelLayout = GetAudioChannelLayout(ctx);
                    if (ctx->sample_rate <= 0 || string.IsNullOrWhiteSpace(sampleFormat) || string.IsNullOrWhiteSpace(channelLayout))
                    {
                        System.Diagnostics.Debug.WriteLine("[FFmpeg Filter Error] Audio stream has incomplete format information.");
                        return;
                    }

                    string args = $"sample_rate={ctx->sample_rate}:sample_fmt={sampleFormat}:channel_layout={channelLayout}";

                    AVFilterContext* srcCtx;
                    string name = $"in{i}";
                    int createRet = ffmpeg.avfilter_graph_create_filter(&srcCtx, abuffer, name, args, null, _filterGraph);
                    if (createRet < 0 || srcCtx == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] Failed to create audio source filter: {GetFfmpegError(createRet)}");
                        return;
                    }

                    _srcContexts[i] = srcCtx;

                    var currOut = ffmpeg.avfilter_inout_alloc();
                    currOut->name = ffmpeg.av_strdup(name);
                    currOut->filter_ctx = srcCtx;
                    currOut->pad_idx = 0;
                    currOut->next = outputs;
                    outputs = currOut;
                }

                AVFilter* abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");
                AVFilterContext* sinkCtx = null;
                int sinkRet = ffmpeg.avfilter_graph_create_filter(&sinkCtx, abuffersink, "out", null, null, _filterGraph);
                if (sinkRet < 0 || sinkCtx == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] Failed to create audio sink filter: {GetFfmpegError(sinkRet)}");
                    return;
                }

                _sinkContext = sinkCtx;

                AVSampleFormat[] fmts = { AVSampleFormat.AV_SAMPLE_FMT_S16, AVSampleFormat.AV_SAMPLE_FMT_NONE };
                fixed (AVSampleFormat* pFmts = fmts)
                    ffmpeg.av_opt_set_bin(_sinkContext, "sample_fmts", (byte*)pFmts, sizeof(AVSampleFormat) * 1, (int)ffmpeg.AV_OPT_SEARCH_CHILDREN);

                AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = _sinkContext;
                inputs->pad_idx = 0;
                inputs->next = null;

              
                string filterDesc = BuildFilterDesc(rate);

                int ret = ffmpeg.avfilter_graph_parse_ptr(_filterGraph, filterDesc, &inputs, &outputs, null);
                
                if (ret < 0)
                {
                 
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] {GetFfmpegError(ret)}");
                    return; 
                }
                if (ret >= 0)
                {
                    ffmpeg.avfilter_graph_config(_filterGraph, null);
                    _filterReady = true; 
                }

                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
            }
        }

        private string GetAudioChannelLayout(AVCodecContext* ctx)
        {
            int channels = ctx->ch_layout.nb_channels;
            if (channels <= 0)
                return "";

            byte[] layoutName = new byte[128];
            fixed (byte* pLayout = layoutName)
                ffmpeg.av_channel_layout_describe(&ctx->ch_layout, pLayout, (ulong)layoutName.Length);

            string describedLayout = System.Text.Encoding.UTF8.GetString(layoutName).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(describedLayout) && !describedLayout.StartsWith("0 channels", StringComparison.OrdinalIgnoreCase))
                return describedLayout;

            return channels switch
            {
                1 => "mono",
                2 => "stereo",
                3 => "2.1",
                4 => "quad",
                5 => "5.0",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                _ => ""
            };
        }

        private string GetFfmpegError(int errorCode)
        {
            if (errorCode >= 0)
                return "";

            byte* errBuff = (byte*)Marshal.AllocHGlobal(256);
            try
            {
                ffmpeg.av_strerror(errorCode, errBuff, 256);
                return Marshal.PtrToStringAnsi((IntPtr)errBuff) ?? errorCode.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)errBuff);
            }
        }

        private string BuildFilterDesc(float rate)
        {
            string merge;
            if (_activeAudioTrackCount == 1)
            {
                merge = "[in0]anull[merged]";
            }
            else
            {
                merge = "";
                for (int i = 0; i < _activeAudioTrackCount; i++)
                    merge += $"[in{i}]";

                merge += $"amerge=inputs={_activeAudioTrackCount}[merged]";
            }

            string tempoFilters = "";
        
            if (rate <= 0)
            {
                tempoFilters = "volume=0";
            }
            else
            {
                
                List<string> filters = new List<string>();
                float tempRate = rate;
                while (tempRate > 2.0f) { filters.Add("atempo=2.0"); tempRate /= 2.0f; }
                while (tempRate < 0.5f) { filters.Add("atempo=0.5"); tempRate /= 0.5f; }
                if (tempRate != 1.0f || filters.Count == 0) filters.Add($"atempo={tempRate:F2}");
                tempoFilters = string.Join(",", filters);
            }

            return $"{merge};[merged]{tempoFilters},aresample={_audioSampleRate},aformat=sample_fmts=s16:sample_rates={_audioSampleRate}";
        }
        public float GetChannelLevelAtTime(int channel, long currentTimeMs)
        {
            long frameIndex = ClampFrameIndex(FrameFromTimeMs(currentTimeMs, _audioFps));

            lock (_lastFrameAudioPeaksLock)
            {
                if (_lastFrameAudioPeakFrame == frameIndex &&
                    channel >= 0 &&
                    channel < _lastFrameAudioPeaks.Length)
                {
                    return _lastFrameAudioPeaks[channel];
                }
            }

            float meterPeak = GetMeterSamplePeak(channel, currentTimeMs);
            if (_isVideoPlaying)
                return meterPeak;

            if (_videoRate < 0 && _slidingAudioProvider != null)
                return _slidingAudioProvider.GetChannelPeakAtFrame(frameIndex, _audioFps, channel);

            if (_fileAudioProvider != null)
                return _fileAudioProvider.GetChannelPeakAtFrame(frameIndex, _audioFps, channel);

            return meterPeak;
        }

        private float GetMeterSamplePeak(int channel, long currentTimeMs)
        {
            const long windowMs = 120;
            float peak = 0f;

            lock (_meterSamplesLock)
            {
                for (int i = _meterSamples.Count - 1; i >= 0; i--)
                {
                    var s = _meterSamples[i];

                    if (s.Channel == channel &&
                        s.TimeMs <= currentTimeMs &&
                        s.TimeMs >= currentTimeMs - windowMs)
                    {
                        peak = Math.Max(peak, s.Peak);
                    }
                }
            }

            return peak;
        }
        private void DecodeToFile(Stream output, CancellationToken token, int? maxDurationMs = null, long minOutputTimeMs = 0)
        {
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVFrame* filtFrame = ffmpeg.av_frame_alloc();
            long maxOutputBytes = -1;
            if (maxDurationMs.HasValue && maxDurationMs.Value > 0)
            {
                int channels = Math.Clamp(_pcmOutputChannels > 0 ? _pcmOutputChannels : CurrentAudioCount, 1, ChannelMask.Length);
                maxOutputBytes = (_audioSampleRate * channels * 2L * maxDurationMs.Value) / 1000;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int readRet;

                    lock (_lock)
                    {
                        if (_formatContext == null)
                            break;

                        readRet = ffmpeg.av_read_frame(_formatContext, packet);
                    }

                    if (readRet < 0)
                        break;

                    int streamIndex = packet->stream_index;

                    if (!_audioDecoders.TryGetValue(streamIndex, out var ctxWrapper))
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    int idx = _audioStreamIndices.IndexOf(streamIndex);
                    int sendRet = ffmpeg.avcodec_send_packet(ctxWrapper.Ptr, packet);
                    ffmpeg.av_packet_unref(packet);

                    if (sendRet < 0)
                        continue;

                    while (true)
                    {
                        int recvRet = ffmpeg.avcodec_receive_frame(ctxWrapper.Ptr, frame);

                        if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet == ffmpeg.AVERROR_EOF)
                            break;

                        if (recvRet < 0)
                            break;

                        var stream = _formatContext->streams[streamIndex];
                        long ptsMs = 0;
                        bool hasPts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE;

                        if (hasPts)
                        {
                            ptsMs = ffmpeg.av_rescale_q(
                                frame->best_effort_timestamp,
                                stream->time_base,
                                new AVRational { num = 1, den = 1000 }
                            );

                            long frameDurationMs = frame->sample_rate > 0
                                ? (long)Math.Ceiling(frame->nb_samples * 1000.0 / frame->sample_rate)
                                : 0;

                            if (minOutputTimeMs > 0 && ptsMs + frameDurationMs <= minOutputTimeMs)
                            {
                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }
                        }

                        if (idx >= 0 && idx < 8)
                        {
                            float peak = CalculatePeakFromFrame(frame);

                            lock (_meterSamplesLock)
                            {
                                _meterSamples.Add(new MeterSample
                                {
                                    TimeMs = ptsMs,
                                    Channel = idx,
                                    Peak = peak
                                });
                            }
                        }

                        lock (_lock)
                        {
                            if (_filterReady && _srcContexts != null && _sinkContext != null && idx >= 0)
                            {
                                int addRet = ffmpeg.av_buffersrc_add_frame_flags(
                                    _srcContexts[idx],
                                    frame,
                                    (int)AV_BUFFERSRC_FLAG_KEEP_REF
                                );

                                if (addRet >= 0)
                                {
                                    while (ffmpeg.av_buffersink_get_frame(_sinkContext, filtFrame) >= 0)
                                    {
                                        byte[] pcmData = ExtractPcm(filtFrame);
                                        ffmpeg.av_frame_unref(filtFrame);

                                        if (pcmData.Length > 0)
                                        {
                                            output.Write(pcmData, 0, pcmData.Length);
                                            if (maxOutputBytes > 0 && output.Length >= maxOutputBytes)
                                                return;
                                        }
                                    }
                                }
                            }
                        }

                        ffmpeg.av_frame_unref(frame);
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_frame_free(&filtFrame);
            }
        }

        private class MeterSample
        {
            public long TimeMs;
            public int Channel;
            public float Peak;
        }

        private readonly List<MeterSample> _meterSamples = new();
        private readonly object _meterSamplesLock = new();
        private readonly float[] _lastFrameAudioPeaks = new float[8];
        private readonly object _lastFrameAudioPeaksLock = new();
        private long _lastFrameAudioPeakFrame = -1;
        private byte[] ExtractPcm(AVFrame* frame)
        {
            int channels = frame->ch_layout.nb_channels;
            if (channels > 0)
                _pcmOutputChannels = Math.Clamp(channels, 1, ChannelMask.Length);
            int size = ffmpeg.av_samples_get_buffer_size(null, channels, frame->nb_samples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
            byte[] res = new byte[size];
            Marshal.Copy((IntPtr)frame->data[0], res, 0, size);
            return res;
        }


        public void Pause()
        {
            _isVideoPlaying = false;
            _isPlaybackStalledForVideo = false;
            _isPlaybackStalledForAudio = false;
            _playbackClock.Stop();
            StopWaveOutForReposition();

          
            _waveProvider?.ClearBuffer();
        }

        private void StopWaveOutForReposition()
        {
            try { _waveOut?.Stop(); } catch { }
        }

        public void ResumeAudio(int audioBufferTimeoutMs = 3000)
        {
            ClearFrameAudioPeaks();
            long currentMediaFrame = CurrentFrameIndex;
            SeekAudioByFrame(currentMediaFrame, _audioFps);
            var waveOut = _waveOut;
            int generation = _audioCacheGeneration;

            if (audioBufferTimeoutMs > 0)
            {
                WaitForAudioBuffer(currentMediaFrame, _audioFps, _videoRate, audioBufferTimeoutMs);
                if (!PlayWaveOutIfCurrent(waveOut, generation))
                    ResetMediaClock(_currentFrameIndex);
            }
            else
            {
                long frameIndex = currentMediaFrame;
                double fps = _audioFps;
                float rate = _videoRate;

                Task.Run(() =>
                {
                    WaitForAudioBuffer(frameIndex, fps, rate, 1000);
                    PlayWaveOutIfCurrent(waveOut, generation);
                });
                ResetMediaClock(_currentFrameIndex);
            }

            _isVideoPlaying = true;
            _isPlaybackStalledForVideo = false;
            _isPlaybackStalledForAudio = false;
        }

        public void SetAudioRate(float rate)
        {
            float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;

            if (effectiveRate >= 0)
            {
                if (_fileAudioProvider != null)
                    _fileAudioProvider.PlaybackRate = GetForwardAudioProviderRate(effectiveRate);

                if (_isVideoPlaying)
                {
                    long currentMediaFrame = CurrentFrameIndex;
                    StartAudioCacheFromFrame(currentMediaFrame, _audioFps, effectiveRate, false);
                    WaitForAudioBuffer(currentMediaFrame, _audioFps, effectiveRate, 1000);
                    PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
                }

                return;
            }

            if (_memoryAudioProvider != null)
                _memoryAudioProvider.PlaybackRate = effectiveRate;
            if (_slidingAudioProvider != null)
                _slidingAudioProvider.PlaybackRate = GetReverseAudioProviderRate(effectiveRate);

            if (_isVideoPlaying)
            {
                long currentMediaFrame = CurrentFrameIndex;
                StartAudioCacheFromFrame(currentMediaFrame, _audioFps, effectiveRate, false);
                WaitForAudioBuffer(currentMediaFrame, _audioFps, effectiveRate, 1000);
                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
            }
        }

        public void SetVideoRate(float rate)
        {
            float previousRate = _videoRate;
            AdvanceVideo(0);
            _videoRate = rate == 0 ? 1.0f : rate;
            SetAudioRate(_videoRate);
            ResetMediaClock(_currentFrameIndex);
            if (Math.Sign(previousRate) != Math.Sign(_videoRate))
                EnsureVideoDecoderNearCurrentFrame(forceRestart: true);
        }

        public void PrepareAudioForRate(float rate)
        {
            if (string.IsNullOrEmpty(CurrentPath))
                return;

            float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;

            if (effectiveRate < 0)
            {
                if (_reverseSegment == null ||
                    !_reverseSegment.Contains(CurrentFrameIndex, effectiveRate) ||
                    _reverseAudioProvider == null ||
                    !_reverseAudioProvider.HasFrameData(CurrentFrameIndex, GetInitialReverseAudioBehindMs(effectiveRate)))
                {
                    StartAudioCacheFromFrame(CurrentFrameIndex, _audioFps, effectiveRate, _isVideoPlaying);
                }

                return;
            }

            if (_fileAudioProvider == null ||
                Math.Abs(_forwardAudioCacheRate - effectiveRate) > 0.001f ||
                !_fileAudioProvider.IsFrameDataAvailable(CurrentFrameIndex, _audioFps, AudioStallResumeBufferMs))
            {
                StartAudioCacheFromFrame(CurrentFrameIndex, _audioFps, effectiveRate, _isVideoPlaying);
            }
        }

        public void AdvanceVideo(int elapsedMs)
        {
            if (!_isVideoPlaying || _totalVideoFrames <= 0) return;

            if (_isPlaybackStalledForVideo)
            {
                EnsureVideoDecoderNearCurrentFrame();
                EnsureAudioCacheForCurrentFrame();
                ResumePlaybackAfterVideoBufferIfReady();
                LogVideoBufferStatus();
                return;
            }
            if (_isPlaybackStalledForAudio)
            {
                EnsureVideoDecoderNearCurrentFrame();
                EnsureAudioCacheForCurrentFrame();
                ResumePlaybackAfterAudioBufferIfReady();
                LogVideoBufferStatus();
                return;
            }

            double clockElapsedMs = GetMasterClockElapsedMs();
            long framesToMove = (long)Math.Floor(clockElapsedMs * DisplayFps * Math.Abs(_videoRate) / 1000.0);
            long targetFrame;
            if (_videoRate >= 0)
                targetFrame = Math.Min(TotalDisplayUnits - 1, _audioClockStartFrame + framesToMove);
            else
                targetFrame = Math.Max(0, _playbackStartFrame - framesToMove);

            if (HasAudioUnderrun())
            {
                StallPlaybackForAudioBuffer();
                EnsureVideoDecoderNearCurrentFrame();
                EnsureAudioCacheForCurrentFrame();
                LogVideoBufferStatus();
                return;
            }

            if (IsVideoFrameCached(targetFrame))
            {
                _currentFrameIndex = targetFrame;
                TrimForwardVideoCacheTail();
            }
            else
            {
                StallPlaybackForVideoBuffer();
            }

            EnsureVideoDecoderNearCurrentFrame();
            EnsureAudioCacheForCurrentFrame();
            LogVideoBufferStatus();

            if ((_videoRate < 0 && _currentFrameIndex == 0) ||
                (_videoRate > 0 && _currentFrameIndex == TotalDisplayUnits - 1))
                Pause();
        }

        private bool IsVideoFrameCached(long frameIndex)
        {
            lock (_lock)
                return _videoFrameCache.ContainsKey(frameIndex);
        }

        private void TrimForwardVideoCacheTail()
        {
            if (_videoRate < 0)
                return;

            List<long>? staleFrameIndexes = null;
            List<Bitmap>? framesToRelease = null;

            lock (_lock)
            {
                long keepFromFrame = Math.Max(0, _currentFrameIndex - (ForwardVideoCacheTailFrames * DisplayUnitsPerFrame));
                foreach (long index in _videoFrameCache.Keys)
                {
                    if (index >= keepFromFrame || index == _displayedVideoFrameIndex)
                        continue;

                    staleFrameIndexes ??= new List<long>();
                    staleFrameIndexes.Add(index);
                }

                if (staleFrameIndexes != null)
                {
                    framesToRelease = new List<Bitmap>(staleFrameIndexes.Count);
                    foreach (long frameIndex in staleFrameIndexes)
                    {
                        if (_videoFrameCache.Remove(frameIndex, out var oldFrame))
                            framesToRelease.Add(oldFrame);
                    }
                }
            }

            ReleaseVideoBitmaps(framesToRelease);
        }

        private void StallPlaybackForVideoBuffer()
        {
            if (_isPlaybackStalledForVideo)
                return;

            _isPlaybackStalledForVideo = true;
            _isPlaybackStalledForAudio = false;
            _playbackStartFrame = _currentFrameIndex;
            _playbackClock.Stop();
            StopWaveOutForReposition();
        }

        private void StallPlaybackForAudioBuffer()
        {
            if (_isPlaybackStalledForAudio)
                return;

            _isPlaybackStalledForAudio = true;
            _isPlaybackStalledForVideo = false;
            _playbackStartFrame = _currentFrameIndex;
            _playbackClock.Stop();
            StopWaveOutForReposition();
        }

        private void ResumePlaybackAfterVideoBufferIfReady()
        {
            if (!HasVideoStallResumeBuffer())
                return;

            SeekAudioByFrame(CurrentFrameIndex, _audioFps);
            WaitForAudioBuffer(CurrentFrameIndex, _audioFps, _videoRate, 1000);
            if (!PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration))
                ResetMediaClock(_currentFrameIndex);
            _isPlaybackStalledForVideo = false;
        }

        private void ResumePlaybackAfterAudioBufferIfReady()
        {
            if (!HasAudioPlaybackBuffer(CurrentFrameIndex, AudioStallResumeBufferMs))
                return;
            if (!IsVideoFrameCached(_currentFrameIndex))
                return;

            SeekAudioByFrame(CurrentFrameIndex, _audioFps);
            if (!PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration))
                ResetMediaClock(_currentFrameIndex);
            _isPlaybackStalledForAudio = false;
        }

        private bool HasAudioUnderrun()
        {
            bool underrun = _videoRate >= 0 && _fileAudioProvider?.ConsumeUnderrun() == true;
            if (underrun)
                Debug.WriteLine($"[AudioUnderrun] frame={_currentFrameIndex} rate={_videoRate:0.###}");
            return underrun;
        }

        private bool HasAudioPlaybackBuffer(long frameIndex, int requiredAheadMs)
        {
            if (_videoRate < 0)
                return true;
            if (CurrentAudioCount <= 0)
                return true;

            return _fileAudioProvider != null &&
                   _fileAudioProvider.IsFrameDataAvailable(frameIndex, _audioFps, requiredAheadMs);
        }

        private bool HasVideoStallResumeBuffer()
        {
            lock (_lock)
            {
                long continuousFrame = GetContinuousCachedFrameLimit(_currentFrameIndex, _videoRate >= 0);
                if (continuousFrame < 0)
                    return false;

                int requiredFrames = GetVideoStallResumeBufferFrames();
                if (_videoRate >= 0)
                    return continuousFrame - _currentFrameIndex >= requiredFrames;

                return _currentFrameIndex - continuousFrame >= requiredFrames;
            }
        }

        private int GetVideoStallResumeBufferFrames()
        {
            double rate = Math.Max(1.0, Math.Abs(_videoRate));
            int requestedFrames = (int)Math.Ceiling(DisplayFps * rate * VideoStallResumeBufferSeconds);
            return Math.Clamp(requestedFrames, 3, GetMaxCachedVideoFramesForRate(_videoRate) / 2);
        }

        private void LogVideoBufferStatus()
        {
            if (_videoStatusLogClock.ElapsedMilliseconds < 1000)
                return;

            _videoStatusLogClock.Restart();

            long currentFrame;
            long minCachedFrame = -1;
            long maxCachedFrame = -1;
            int cacheCount;
            long displayFrame;
            long continuousFrame = -1;
            lock (_lock)
            {
                currentFrame = _currentFrameIndex;
                cacheCount = _videoFrameCache.Count;
                if (cacheCount > 0)
                {
                    minCachedFrame = _videoFrameCache.Keys.Min();
                    maxCachedFrame = _videoFrameCache.Keys.Max();
                    continuousFrame = GetContinuousCachedFrameLimit(currentFrame, _videoRate >= 0);
                }
            }

            displayFrame = GetDisplayFrameIndex();
            long cacheAhead = maxCachedFrame >= 0 ? maxCachedFrame - currentFrame : -1;
            long continuousAhead = continuousFrame >= 0
                ? (_videoRate >= 0 ? continuousFrame - currentFrame : currentFrame - continuousFrame)
                : -1;
            string taskStatus = _videoDecodeTask?.Status.ToString() ?? "null";
            bool taskCompleted = _videoDecodeTask?.IsCompleted ?? true;

            Debug.WriteLine(
                $"[VideoBuffer] current={currentFrame} display={displayFrame} " +
                $"cache={minCachedFrame}-{maxCachedFrame} ahead={cacheAhead} continuous={continuousAhead} " +
                $"continuousEnd={continuousFrame} count={cacheCount} " +
                $"decodeTask={taskStatus} completed={taskCompleted} rate={_videoRate:0.###}");
        }

        public void SeekVideoByFrame(long frameIndex)
        {
            if (_totalVideoFrames <= 0) return;
            _currentFrameIndex = DisplayIndexFromMediaFrame(ClampFrameIndex(frameIndex));
            ResetMediaClock(_currentFrameIndex);
            EnsureVideoDecoderNearCurrentFrame(forceRestart: true);
        }

        private void EnsureVideoDecoderNearCurrentFrame(bool forceRestart = false)
        {
            if (string.IsNullOrEmpty(CurrentPath)) return;

            if (!forceRestart && _videoDecodeTask != null && !_videoDecodeTask.IsCompleted)
                return;

            long minCachedFrame = -1;
            long maxCachedFrame = -1;
            long continuousCachedFrame = -1;
            bool restartLaggingDecoder = false;
            lock (_lock)
            {
                if (_videoFrameCache.Count > 0)
                {
                    minCachedFrame = _videoFrameCache.Keys.Min();
                    maxCachedFrame = _videoFrameCache.Keys.Max();
                }

                if (!forceRestart && _videoFrameCache.ContainsKey(_currentFrameIndex))
                {
                    continuousCachedFrame = GetContinuousCachedFrameLimit(_currentFrameIndex, _videoRate >= 0);
                    if (_videoRate >= 0)
                    {
                        if (continuousCachedFrame >= 0 &&
                            continuousCachedFrame - _currentFrameIndex > GetRequiredVideoBufferFramesForRate(_videoRate) * DisplayUnitsPerFrame)
                        {
                            return;
                        }
                    }
                    else if (continuousCachedFrame >= 0 &&
                             _currentFrameIndex - continuousCachedFrame > GetReverseVideoPreloadLowWaterFrames() * DisplayUnitsPerFrame)
                    {
                        return;
                    }
                }

                restartLaggingDecoder = _videoRate >= 0
                    ? maxCachedFrame >= 0 && _currentFrameIndex > maxCachedFrame + VideoDecoderRestartGapFrames
                    : continuousCachedFrame >= 0 && _currentFrameIndex < continuousCachedFrame - VideoDecoderRestartGapFrames;
            }

            if (!forceRestart &&
                restartLaggingDecoder &&
                Math.Abs(_videoRate) <= 1.0f)
            {
                return;
            }

            _videoCts?.Cancel();
            //_videoCts?.Dispose();
            _videoCts = new CancellationTokenSource();
            long startFrame;
            long? endFrameExclusive = null;
            if (_videoRate < 0)
            {
                int reverseWindowFrames = Math.Min(ReverseVideoDecodeWindowFrames * DisplayUnitsPerFrame, GetMaxCachedVideoFramesForRate(_videoRate));
                long startDisplayIndex = Math.Max(0, _currentFrameIndex - reverseWindowFrames + 1);
                startFrame = MediaFrameFromDisplayIndex(startDisplayIndex);
                endFrameExclusive = Math.Min(_totalVideoFrames, MediaFrameFromDisplayIndex(_currentFrameIndex) + 1);
            }
            else
            {
                long forwardCacheEnd = continuousCachedFrame >= _currentFrameIndex
                    ? continuousCachedFrame
                    : maxCachedFrame;

                long startDisplayIndex = forceRestart || restartLaggingDecoder || forwardCacheEnd < _currentFrameIndex
                    ? _currentFrameIndex
                    : Math.Min(TotalDisplayUnits - 1, forwardCacheEnd + 1);
                startFrame = MediaFrameFromDisplayIndex(startDisplayIndex);
                endFrameExclusive = null;
            }
            var token = _videoCts.Token;
            int decodeGeneration = Interlocked.Increment(ref _videoDecodeGeneration);
            _videoDecodeRestartClock.Restart();
            _videoDecodeTask = Task.Factory.StartNew(
                () => DecodeVideoFrameWindow(CurrentPath, startFrame, decodeGeneration, token, endFrameExclusive),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private int GetReverseVideoPreloadLowWaterFrames()
        {
            int maxFrames = GetMaxCachedVideoFramesForRate(_videoRate);
            return Math.Min(ReverseVideoPreloadLowWaterFrames, Math.Max(3, maxFrames * 3 / 4));
        }

        public void Seek(long timeMs)
        {
            long frame = FrameFromTimeMs(timeMs, _audioFps);
            SeekVideoByFrame(frame);
            SeekAudioByFrame(frame, _audioFps);
        }

        public void SeekAudio(long timeMs)
        {
            SeekAudioByFrame(FrameFromTimeMs(timeMs, _audioFps), _audioFps);
        }

        public void SeekAudioByFrame(long frameIndex, double fps, bool waitForPreviousCache = true)
        {
            frameIndex = ClampFrameIndex(frameIndex);
            bool positiveTempoPlayback = _videoRate > 0 && Math.Abs(_videoRate - 1.0f) > 0.001f;

            bool hasData = _videoRate < 0
                ? (_reverseSegment != null &&
                   _reverseSegment.Contains(frameIndex, _videoRate) &&
                   _reverseAudioProvider != null &&
                   _reverseAudioProvider.HasFrameData(frameIndex, 250))
                : (_fileAudioProvider != null &&
                   (!positiveTempoPlayback || Math.Abs(_forwardAudioCacheRate - _videoRate) <= 0.001f) &&
                   _fileAudioProvider.IsFrameDataAvailable(frameIndex, fps));

            if (!hasData)
            {
                StartAudioCacheFromFrame(
                    frameIndex,
                    fps,
                    Math.Abs(_videoRate) > 0 ? _videoRate : 1.0f,
                    _isVideoPlaying,
                    waitForPreviousCache);
                return;
            }

            if (_videoRate < 0)
                _reverseAudioProvider?.SeekFrame(frameIndex);
            else
                _fileAudioProvider?.SeekFrame(frameIndex, fps);
            _memoryAudioProvider?.SeekFrame(frameIndex, fps);
        }

        public Task PlayFrameAudioAsync(long frameIndex, double fps)
        {
            return Task.Run(() =>
            {
                if (fps <= 0) fps = CurrentFps;
                frameIndex = ClampFrameIndex(frameIndex);

                float previousRate = _videoRate;
                bool wasPlaying = _isVideoPlaying;

                _isVideoPlaying = false;
                _playbackClock.Stop();
                _videoRate = 1.0f;

                if (_fileAudioProvider == null ||
                    !_fileAudioProvider.IsFrameDataAvailable(frameIndex, fps, 0))
                {
                    StartAudioCacheFromFrame(frameIndex, fps, 1.0f, false);
                }

                WaitForFrameAudioBuffer(frameIndex, fps, 1000);
                CaptureFrameAudioPeaks(frameIndex, fps);

                byte[]? framePcm = _fileAudioProvider?.ReadFrameStereoPcm(frameIndex, fps, ChannelMask);
                if (framePcm is { Length: > 0 })
                    PlayOneShotFrameAudio(framePcm);

                _fileAudioProvider?.SeekFrame(frameIndex, fps);
                _videoRate = previousRate;
                _isVideoPlaying = wasPlaying;
            });
        }

        private void PlayOneShotFrameAudio(byte[] framePcm)
        {
            var frameProvider = new OneShotPcmAudioProvider(framePcm, _audioSampleRate);
            using var waveOut = new WaveOutEvent
            {
                DesiredLatency = 40,
                NumberOfBuffers = 2
            };

            using var playbackDone = new ManualResetEventSlim(false);
            waveOut.PlaybackStopped += (_, _) => playbackDone.Set();
            waveOut.Init(frameProvider);
            waveOut.Play();

            int frameMs = Math.Max(35, (int)Math.Ceiling(framePcm.Length * 1000.0 / Math.Max(1, _audioSampleRate * 4)));
            playbackDone.Wait(frameMs + 200);
            try { waveOut.Stop(); } catch { }
        }

        private void CaptureFrameAudioPeaks(long frameIndex, double fps)
        {
            lock (_lastFrameAudioPeaksLock)
            {
                Array.Clear(_lastFrameAudioPeaks, 0, _lastFrameAudioPeaks.Length);

                if (_fileAudioProvider != null)
                {
                    for (int channel = 0; channel < _lastFrameAudioPeaks.Length; channel++)
                        _lastFrameAudioPeaks[channel] = _fileAudioProvider.GetChannelPeakAtFrame(frameIndex, fps, channel);
                }

                _lastFrameAudioPeakFrame = frameIndex;
            }
        }

        private void ClearFrameAudioPeaks()
        {
            lock (_lastFrameAudioPeaksLock)
            {
                Array.Clear(_lastFrameAudioPeaks, 0, _lastFrameAudioPeaks.Length);
                _lastFrameAudioPeakFrame = -1;
            }
        }

        private bool WaitForFrameAudioBuffer(long frameIndex, double fps, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            frameIndex = ClampFrameIndex(frameIndex);

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_fileAudioProvider == null)
                    return false;

                if (_fileAudioProvider.IsFrameDataAvailable(frameIndex, fps, 0))
                    return true;

                Thread.Sleep(20);
            }

            return false;
        }

        private long ClampFrameIndex(long frameIndex)
        {
            if (_totalVideoFrames <= 0)
                return Math.Max(0, frameIndex);

            return Math.Clamp(frameIndex, 0, _totalVideoFrames - 1);
        }

        private long ClampDisplayIndex(long displayIndex)
        {
            if (TotalDisplayUnits <= 0)
                return Math.Max(0, displayIndex);

            return Math.Clamp(displayIndex, 0, TotalDisplayUnits - 1);
        }

        private void EnsureAudioCacheForCurrentFrame()
        {
            if (_videoRate >= 0)
            {
                if (_fileAudioProvider != null || _audioCacheTask != null && !_audioCacheTask.IsCompleted)
                    return;

                StartAudioCacheFromFrame(CurrentFrameIndex, _audioFps, _videoRate, false);
                return;
            }

            if (_audioCacheTask != null && !_audioCacheTask.IsCompleted)
                return;

            if (_reverseSegment == null ||
                !_reverseSegment.Contains(CurrentFrameIndex, _videoRate) ||
                CurrentFrameIndex - _reverseSegment.StartFrame < ReverseSegmentPreloadFrames)
            {
                StartAudioCacheFromFrame(CurrentFrameIndex, _audioFps, _videoRate, _isVideoPlaying, waitForPreviousCache: false);
            }
        }
        // frameIndex = round(timeMs * fps / 1000)
        public static long FrameFromTimeMs(long timeMs, double fps)
        {
            if (timeMs < 0) timeMs = 0;
            if (fps <= 0) fps = 29.97;
            return (long)Math.Round(timeMs * fps / 1000.0);
        }
        // timeMs = round(frameIndex * 1000 / fps)
        public static long TimeMsFromFrame(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;
            return (long)Math.Round(frameIndex * 1000.0 / fps);
        }
        private float CalculatePeakFromFrame(AVFrame* frame)
        {
            float peak = 0f;
            int samples = frame->nb_samples;
            int channels = Math.Max(1, frame->ch_layout.nb_channels);
            var fmt = (AVSampleFormat)frame->format;

            if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                short* data = (short*)frame->data[0];
                int total = samples * channels;

                for (int i = 0; i < total; i++)
                    peak = Math.Max(peak, GetInt16Peak(data[i]));
            }
            else if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S16P)
            {
                for (uint ch = 0; ch < channels; ch++)
                {
                    short* data = (short*)frame->data[ch];
                    for (int i = 0; i < samples; i++)
                        peak = Math.Max(peak, GetInt16Peak(data[i]));
                }
            }
            else if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S32)
            {
                int* data = (int*)frame->data[0];
                int total = samples * channels;

                for (int i = 0; i < total; i++)
                    peak = Math.Max(peak, Math.Abs((long)data[i]) / 2147483648f);
            }
            else if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S32P)
            {
                for (uint ch = 0; ch < channels; ch++)
                {
                    int* data = (int*)frame->data[ch];
                    for (int i = 0; i < samples; i++)
                        peak = Math.Max(peak, Math.Abs((long)data[i]) / 2147483648f);
                }
            }

            return Math.Min(1f, peak);
        }

        private static float GetInt16Peak(short sample)
        {
            int magnitude = sample == short.MinValue ? 32768 : Math.Abs(sample);
            return magnitude / 32768f;
        }

        public void StopAudioBridge()
        {
            _isVideoPlaying = false;
            _isPlaybackStalledForVideo = false;
            _isPlaybackStalledForAudio = false;
            _playbackClock.Stop();
            _videoCts?.Cancel();
            _audioCacheCts?.Cancel();
            Interlocked.Increment(ref _videoDecodeGeneration);
            _audioCacheGeneration++;

            WaitForTaskQuietly(_videoDecodeTask, 500);
            WaitForTaskQuietly(_audioCacheTask, 500);

            lock (_lock)
            {
                _filterReady = false;

                try { _waveOut?.Stop(); } catch { }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;

                _fileAudioProvider?.Dispose();
                _fileAudioProvider = null;
                _forwardAudioCacheRate = 1.0f;

                _memoryAudioProvider = null;
                _slidingAudioProvider = null;
                _reverseAudioProvider = null;
                _reverseSegment = null;
                _waveProvider?.ClearBuffer();

                ClearVideoFrameCacheLocked();
                _totalVideoFrames = 0;
            }

            CloseDecodeResources();

            lock (_levelLock)
            {
                Array.Clear(_channelLevels, 0, _channelLevels.Length);
            }

            _videoCts?.Dispose();
            _videoCts = null;
            _videoDecodeTask = null;

            _audioCacheCts?.Dispose();
            _audioCacheCts = null;
            _audioCacheTask = null;

            if (!string.IsNullOrEmpty(_pcmCachePath))
            {
                try { File.Delete(_pcmCachePath); } catch { }
                _pcmCachePath = null;
            }
        }

        private void CloseDecodeResources()
        {
            lock (_ffmpegResourceLock)
            {
                foreach (var decoder in _audioDecoders.Values)
                {
                    if (decoder.Ptr != null)
                    {
                        var p = decoder.Ptr;
                        ffmpeg.avcodec_free_context(&p);
                        decoder.Ptr = null;
                    }
                }

                _audioDecoders.Clear();

                if (_formatContext != null)
                {
                    var p = _formatContext;
                    ffmpeg.avformat_close_input(&p);
                    _formatContext = null;
                }

                if (_filterGraph != null)
                {
                    var p = _filterGraph;
                    ffmpeg.avfilter_graph_free(&p);
                    _filterGraph = null;
                }

                if (_srcContexts != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_srcContexts);
                    _srcContexts = null;
                }

                _sinkContext = null;
                _filterReady = false;
            }
        }

        public void Dispose()
        {
            StopAudioBridge();
            lock (_lock)
                ClearVideoBitmapPoolLocked();
        }
    }
}

