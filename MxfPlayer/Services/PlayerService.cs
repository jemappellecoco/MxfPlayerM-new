using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    public class ConfigModel
    {
        public string FFmpegPath { get; set; } = "";
    }

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
        private IWavePlayer? _waveOut;
        private BufferedWaveProvider _waveProvider;
        private MxfAudioProvider? _fileAudioProvider;
        private MemoryPcmAudioProvider? _memoryAudioProvider;
        private double _audioFps = 29.97;
        private int _audioSampleRate = 48000;
        public double CurrentFps => _audioFps > 0 ? _audioFps : 29.97;
        private readonly Dictionary<long, Bitmap> _videoFrameCache = new();
        private readonly Queue<long> _videoFrameCacheOrder = new();
        private readonly List<Bitmap> _videoFrames = new();
        private const int MaxCachedVideoFrames = 1800; //MaxCachedVideoFrames = 最多保留多少張畫面
        private const int VideoPreloadLowWaterFrames = 360;//VideoPreloadLowWaterFrames = 前方剩多少 frame 時開始補 buffe
        private const int VideoDecoderRestartGapFrames = 30; //VideoDecoderRestartGapFrames = 落後多少 frame 才重啟 decoder
        private const int MaxStaleDisplayFrames = 180;
        private long _currentFrameIndex;
        private long _totalVideoFrames;
        private CancellationTokenSource? _videoCts;
        private Task? _videoDecodeTask;
        private CancellationTokenSource? _audioCacheCts;
        private Task? _audioCacheTask;
        private string? _pcmCachePath;
        private int _audioCacheGeneration;
        private double _videoFrameAccumulator;
        private bool _isVideoPlaying;
        private float _videoRate = 1.0f;
        private readonly Stopwatch _playbackClock = new();
        private readonly Stopwatch _videoStatusLogClock = Stopwatch.StartNew();
        private long _playbackStartFrame;
        private const int AudioOutputLatencyMs = 0;
        private const int ReverseAudioCacheWindowMs = 15000;
        private const int ReverseAudioCacheRefreshBehindMs = 1200;

        private CancellationTokenSource? _cts;
        private bool _isDecoding;
        private readonly object _lock = new object();

        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };
        public string CurrentPath { get; private set; } = string.Empty;
        public int CurrentAudioCount { get; private set; }
        public bool IsAudioReady => _waveOut != null && _fileAudioProvider != null;
        public bool IsPlaying => _isVideoPlaying;
        public long CurrentFrameIndex => _currentFrameIndex;

        public bool HasVideoBufferForRate(float rate)
        {
            lock (_lock)
            {
                if (_videoFrameCache.Count == 0)
                    return false;

                long requiredFrames = Math.Abs(rate) switch
                {
                    >= 16 => 900,
                    >= 8 => 600,
                    >= 4 => 360,
                    >= 2 => 180,
                    _ => 60
                };

                if (rate >= 0)
                {
                    long maxCached = _videoFrameCache.Keys.Max();
                    return maxCached - _currentFrameIndex >= requiredFrames;
                }
                else
                {
                    long minCached = _videoFrameCache.Keys.Min();
                    return _currentFrameIndex - minCached >= requiredFrames;
                }
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
                if (_videoFrameCache.TryGetValue(_currentFrameIndex, out _))
                    return _currentFrameIndex;

                if (_videoFrameCache.Count == 0)
                    return -1;

                long bestFrameIndex = _videoRate >= 0
                    ? _videoFrameCache.Keys.Where(index => index <= _currentFrameIndex).DefaultIfEmpty(-1).Max()
                    : _videoFrameCache.Keys.Where(index => index >= _currentFrameIndex).DefaultIfEmpty(-1).Min();

                if (bestFrameIndex < 0 || !_videoFrameCache.ContainsKey(bestFrameIndex))
                {
                    bestFrameIndex = _videoFrameCache.Keys
                        .OrderBy(index => Math.Abs(index - _currentFrameIndex))
                        .First();
                }

                long maxStaleFrames = Math.Max(
                    MaxStaleDisplayFrames,
                    (long)Math.Ceiling(Math.Abs(_videoRate) * _audioFps)
                );

                if (Math.Abs(bestFrameIndex - _currentFrameIndex) > maxStaleFrames)
                    return -1;

                return bestFrameIndex;
            }
        }
        public Image? CreateDisplayVideoFrameSnapshot(out long frameIndex)
        {
            lock (_lock)
            {
                frameIndex = _currentFrameIndex;
                if (_videoFrameCache.TryGetValue(_currentFrameIndex, out var exactFrame))
                    return (Image)exactFrame.Clone();

                if (_videoFrameCache.Count == 0)
                    return null;

                long bestFrameIndex = _videoRate >= 0
                    ? _videoFrameCache.Keys.Where(index => index <= _currentFrameIndex).DefaultIfEmpty(-1).Max()
                    : _videoFrameCache.Keys.Where(index => index >= _currentFrameIndex).DefaultIfEmpty(-1).Min();

                if (bestFrameIndex < 0 || !_videoFrameCache.ContainsKey(bestFrameIndex))
                {
                    bestFrameIndex = _videoFrameCache.Keys
                        .OrderBy(index => Math.Abs(index - _currentFrameIndex))
                        .First();
                }

                long maxStaleFrames = Math.Max(MaxStaleDisplayFrames, (long)Math.Ceiling(Math.Abs(_videoRate) * _audioFps));
                if (Math.Abs(bestFrameIndex - _currentFrameIndex) > maxStaleFrames)
                    return null;

                frameIndex = bestFrameIndex;
                return (Image)_videoFrameCache[bestFrameIndex].Clone();
            }
        }
        public long CurrentTimeMs => TimeMsFromFrame(_currentFrameIndex, _audioFps);
        public long LengthMs => _totalVideoFrames <= 0 ? 0 : TimeMsFromFrame(_totalVideoFrames - 1, _audioFps);

        private class PointerWrapper<T> where T : unmanaged { public T* Ptr; }

        public PlayerService()
        {
            LoadFFmpegFromConfig();

            _waveProvider = CreateWaveProvider(_audioSampleRate);
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
                string jsonPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (File.Exists(jsonPath))
                {
                    var json = File.ReadAllText(jsonPath);
                    var config = JsonSerializer.Deserialize<ConfigModel>(json);
                    if (!string.IsNullOrEmpty(config?.FFmpegPath))
                        ffmpeg.RootPath = config.FFmpegPath;
                }
            }
            catch { }
        }

        public Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0, float rate = 1.0f, double fps = 0, int sampleRate = 48000)
        {
            fps = fps > 0 ? fps : CurrentFps;

            LoadForBufferedPlayback(path, audioCount, startTimeMs, rate, fps, sampleRate);
            return WaitForFrameBufferAsync(FrameFromTimeMs(startTimeMs, fps), 3000);
        }

        private void LoadFullFile(string path, int audioCount, long startTimeMs, float rate, double fps, int sampleRate)
        {
            LoadForBufferedPlayback(path, audioCount, startTimeMs, rate, fps, sampleRate);
            return;

            StopAudioBridge();
            CurrentPath = path;
            CurrentAudioCount = audioCount;
            _audioFps = fps > 0 ? fps : 29.97;
            _audioSampleRate = NormalizeSampleRate(sampleRate);
            ResetWaveProvider();
            DecodeVideoFrames(path);
            byte[] pcmData = CreatePcmCacheData(path);

            _memoryAudioProvider = new MemoryPcmAudioProvider(pcmData, 2, _audioSampleRate);
            _memoryAudioProvider.PlaybackRate = Math.Abs(rate) > 0 ? Math.Abs(rate) : 1.0f;
            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100
            };
            _waveOut.Init(_memoryAudioProvider);

         

            SetVideoRate(rate);
            long startFrame = FrameFromTimeMs(startTimeMs, _audioFps);
            SeekAudioByFrame(startFrame, _audioFps);
            SeekVideoByFrame(startFrame);
        }

        private byte[] CreatePcmCacheData(string path)
        {
            lock (_meterSamplesLock)
            {
                _meterSamples.Clear();
            }

            InitFFmpeg(path, 1.0f);

            using (var output = new MemoryStream())
            {
                DecodeToFile(output, CancellationToken.None);
                CloseDecodeResources();
                return output.ToArray();
            }
        }

        private void LoadForBufferedPlayback(string path, int audioCount, long startTimeMs, float rate, double fps, int sampleRate)
        {
            StopAudioBridge();
            CurrentPath = path;
            CurrentAudioCount = audioCount;
            _audioFps = fps > 0 ? fps : CurrentFps;
            _audioSampleRate = NormalizeSampleRate(sampleRate);
            ResetWaveProvider();

            long startFrame = FrameFromTimeMs(startTimeMs, _audioFps);
            _totalVideoFrames = ProbeVideoFrameCount(path, _audioFps);

            lock (_lock)
            {
                ClearVideoFrameCacheLocked();
                _currentFrameIndex = Math.Clamp(startFrame, 0, Math.Max(0, _totalVideoFrames - 1));
                _playbackStartFrame = _currentFrameIndex;
            }

            SetVideoRate(rate);
            SeekVideoByFrame(startFrame);
            StartAudioCacheFromFrame(startFrame, _audioFps, rate, false);
        }

        private void StartAudioCacheFromFrame(long frameIndex, double fps, float rate, bool keepPlaying)
        {
            lock (_audioCacheLock)
            {
                if (string.IsNullOrEmpty(CurrentPath)) return;

                _audioCacheCts?.Cancel();

                try
                {
                    _audioCacheTask?.Wait(500);
                }
                catch { }

                _audioCacheCts?.Dispose();
                _audioCacheCts = new CancellationTokenSource();
                _audioCacheGeneration++;

                try { _waveOut?.Stop(); } catch { }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;

                _fileAudioProvider?.Dispose();
                _fileAudioProvider = null;

                if (!string.IsNullOrEmpty(_pcmCachePath))
                {
                    try { File.Delete(_pcmCachePath); } catch { }
                    _pcmCachePath = null;
                }

                _pcmCachePath = Path.Combine(Path.GetTempPath(), $"MxfPlayer_{Guid.NewGuid():N}.pcm");
                using (File.Create(_pcmCachePath)) { }

                float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;
                bool reverse = effectiveRate < 0;
                long cacheStartFrame = frameIndex;

                if (reverse)
                {
                    long reverseWindowFrames = FrameFromTimeMs((long)(ReverseAudioCacheWindowMs * Math.Abs(effectiveRate)), fps);
                    cacheStartFrame = Math.Max(0, frameIndex - reverseWindowFrames);
                }

                long baseTimeMs = TimeMsFromFrame(cacheStartFrame, fps);

                int cacheChannelCount = Math.Clamp(CurrentAudioCount, 1, ChannelMask.Length);
                _fileAudioProvider = new MxfAudioProvider(_pcmCachePath, cacheChannelCount, baseTimeMs, _audioSampleRate)
                {
                    PlaybackRate = effectiveRate,
                    Mask = ChannelMask
                };

                _fileAudioProvider.SeekFrame(frameIndex, fps);

                _waveOut = new WaveOutEvent
                {
                    DesiredLatency = 100
                };
                _waveOut.Init(_fileAudioProvider);
                var waveOut = _waveOut;
                int generation = _audioCacheGeneration;

                var token = _audioCacheCts.Token;
                string pcmPath = _pcmCachePath;
                string path = CurrentPath;

                _audioCacheTask = Task.Run(() =>
                {
                    CreatePcmCacheFile(path, pcmPath, cacheStartFrame, fps, token);
                }, token);

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

        private void PlayWaveOutIfCurrent(IWavePlayer? waveOut, int generation)
        {
            if (waveOut == null) return;

            lock (_audioCacheLock)
            {
                if (generation != _audioCacheGeneration || !ReferenceEquals(waveOut, _waveOut))
                    return;

                try
                {
                    waveOut.Play();
                }
                catch (ObjectDisposedException) { }
                catch (NullReferenceException ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Audio Play ignored] " + ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Audio Play ignored] " + ex.Message);
                }
            }
        }

        private void WaitForAudioBuffer(long frameIndex, double fps, float rate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_fileAudioProvider == null)
                    return;

                bool available = rate < 0
                    ? _fileAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 500)
                    : _fileAudioProvider.IsFrameDataAvailable(frameIndex, fps, 250);

                if (available)
                    return;

                Thread.Sleep(20);
            }
        }

        private void CreatePcmCacheFile(string path, string pcmPath, long startFrame, double fps, CancellationToken token)
        {
            try
            {
                lock (_meterSamplesLock)
                {
                    _meterSamples.Clear();
                }

                InitFFmpeg(path, 1.0f);

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
                    DecodeToFile(output, token);
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

        public Task WaitForFrameBufferAsync(long frameIndex, int timeoutMs = 3000)
        {
            return Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_lock)
                    {
                        if (_videoFrameCache.ContainsKey(frameIndex))
                            return;
                    }

                    EnsureVideoDecoderNearCurrentFrame();
                    Thread.Sleep(25);
                }
            });
        }

        public Task WaitForVideoBufferAheadAsync(long frameIndex, int requiredAheadFrames, int timeoutMs = 3000)
        {
            return Task.Run(() =>
            {
                if (requiredAheadFrames <= 0)
                    return;

                var sw = Stopwatch.StartNew();
                long clampedFrame = Math.Max(0, frameIndex);
                long targetFrame = _totalVideoFrames > 0
                    ? Math.Min(_totalVideoFrames - 1, clampedFrame + requiredAheadFrames)
                    : clampedFrame + requiredAheadFrames;

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_lock)
                    {
                        if (_videoFrameCache.ContainsKey(clampedFrame) &&
                            _videoFrameCache.Count > 0 &&
                            _videoFrameCache.Keys.Max() >= targetFrame)
                        {
                            return;
                        }
                    }

                    EnsureVideoDecoderNearCurrentFrame();
                    Thread.Sleep(25);
                }
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

        private void DecodeVideoFrames(string path)
        {
            DecodeVideoFrameWindow(path, _currentFrameIndex, CancellationToken.None);
            return;

            foreach (var cachedFrame in _videoFrames)
                cachedFrame.Dispose();

            _videoFrames.Clear();
            _currentFrameIndex = 0;
            _videoFrameAccumulator = 0;

            AVFormatContext* formatContext = null;
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0) return;

            AVCodecContext* codecContext = null;
            SwsContext* swsContext = null;
            AVFrame* frame = null;
            AVFrame* rgbFrame = null;
            AVPacket* packet = null;
            byte* rgbBuffer = null;

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

                var codecParameters = formatContext->streams[videoStreamIndex]->codecpar;
                var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
                if (codec == null) return;

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters);
                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0) return;

                int width = codecContext->width;
                int height = codecContext->height;
                var sourceFormat = codecContext->pix_fmt;
                var targetFormat = AVPixelFormat.AV_PIX_FMT_BGR24;

                swsContext = ffmpeg.sws_getContext(
                    width,
                    height,
                    sourceFormat,
                    width,
                    height,
                    targetFormat,
                    2,
                    null,
                    null,
                    null);

                int rgbBufferSize = width * height * 3;
                rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)rgbBufferSize);

                frame = ffmpeg.av_frame_alloc();
                rgbFrame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                rgbFrame->data[0] = rgbBuffer;
                rgbFrame->linesize[0] = width * 3;

                while (ffmpeg.av_read_frame(formatContext, packet) >= 0)
                {
                    if (packet->stream_index == videoStreamIndex)
                    {
                        if (ffmpeg.avcodec_send_packet(codecContext, packet) >= 0)
                            ReceiveVideoFrames(codecContext, frame, rgbFrame, swsContext, width, height);
                    }

                    ffmpeg.av_packet_unref(packet);
                }

                ffmpeg.avcodec_send_packet(codecContext, null);
                ReceiveVideoFrames(codecContext, frame, rgbFrame, swsContext, width, height);
            }
            finally
            {
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (rgbFrame != null) ffmpeg.av_frame_free(&rgbFrame);
                if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
                if (swsContext != null) ffmpeg.sws_freeContext(swsContext);
                if (codecContext != null) ffmpeg.avcodec_free_context(&codecContext);
                if (formatContext != null) ffmpeg.avformat_close_input(&formatContext);
            }
        }

        private void ReceiveVideoFrames(AVCodecContext* codecContext, AVFrame* frame, AVFrame* rgbFrame, SwsContext* swsContext, int width, int height)
        {
            while (ffmpeg.avcodec_receive_frame(codecContext, frame) == 0)
            {
                ffmpeg.sws_scale(
                    swsContext,
                    frame->data,
                    frame->linesize,
                    0,
                    height,
                    rgbFrame->data,
                    rgbFrame->linesize);

                _videoFrames.Add(CreateBitmapFromFrame(rgbFrame, width, height));
                ffmpeg.av_frame_unref(frame);
            }
        }

        private Bitmap CreateBitmapFromFrame(AVFrame* rgbFrame, int width, int height)
        {
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            try
            {
                int sourceStride = rgbFrame->linesize[0];
                int targetStride = data.Stride;
                int rowBytes = width * 3;

                for (int y = 0; y < height; y++)
                {
                    byte* source = rgbFrame->data[0] + (y * sourceStride);
                    byte* target = (byte*)data.Scan0 + (y * targetStride);
                    Buffer.MemoryCopy(source, target, targetStride, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        private void DecodeVideoFrameWindow(string path, long startFrame, CancellationToken token)
        {
            AVFormatContext* formatContext = null;
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0) return;

            AVCodecContext* codecContext = null;
            SwsContext* swsContext = null;
            AVFrame* frame = null;
            AVFrame* rgbFrame = null;
            AVPacket* packet = null;
            byte* rgbBuffer = null;

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
                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0) return;

                int width = codecContext->width;
                int height = codecContext->height;
                swsContext = ffmpeg.sws_getContext(
                    width, height, codecContext->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    2, null, null, null);

                int rgbBufferSize = width * height * 3;
                rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)rgbBufferSize);
                frame = ffmpeg.av_frame_alloc();
                rgbFrame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();
                rgbFrame->data[0] = rgbBuffer;
                rgbFrame->linesize[0] = width * 3;

                long seekFrame = Math.Max(0, startFrame - 3);
                long seekTarget = ffmpeg.av_rescale_q(
                    TimeMsFromFrame(seekFrame, _audioFps),
                    new AVRational { num = 1, den = 1000 },
                    stream->time_base);

                if (ffmpeg.av_seek_frame(formatContext, videoStreamIndex, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                    ffmpeg.avcodec_flush_buffers(codecContext);

                long windowEndFrame = Math.Min(_totalVideoFrames > 0 ? _totalVideoFrames : long.MaxValue, startFrame + MaxCachedVideoFrames);
                long fallbackFrameIndex = seekFrame;
                long cachedThroughFrame = startFrame - 1;

                while (!token.IsCancellationRequested && cachedThroughFrame < windowEndFrame && ffmpeg.av_read_frame(formatContext, packet) >= 0)
                {
                    if (packet->stream_index == videoStreamIndex &&
                        ffmpeg.avcodec_send_packet(codecContext, packet) >= 0)
                    {
                        DecodeWindowFrames(
                            codecContext,
                            frame,
                            rgbFrame,
                            swsContext,
                            width,
                            height,
                            stream->time_base,
                            stream->start_time,
                            startFrame,
                            windowEndFrame,
                            ref fallbackFrameIndex,
                            ref cachedThroughFrame,
                            token);

                        if (cachedThroughFrame >= windowEndFrame)
                        {
                            ffmpeg.av_packet_unref(packet);
                            break;
                        }
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (rgbFrame != null) ffmpeg.av_frame_free(&rgbFrame);
                if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
                if (swsContext != null) ffmpeg.sws_freeContext(swsContext);
                if (codecContext != null) ffmpeg.avcodec_free_context(&codecContext);
                if (formatContext != null) ffmpeg.avformat_close_input(&formatContext);
            }
        }

        private void DecodeWindowFrames(
            AVCodecContext* codecContext,
            AVFrame* frame,
            AVFrame* rgbFrame,
            SwsContext* swsContext,
            int width,
            int height,
            AVRational streamTimeBase,
            long streamStartTime,
            long windowStartFrame,
            long windowEndFrame,
            ref long fallbackFrameIndex,
            ref long cachedThroughFrame,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested && ffmpeg.avcodec_receive_frame(codecContext, frame) == 0)
            {
                long frameIndex = GetFrameIndexFromTimestamp(frame, streamTimeBase, streamStartTime, fallbackFrameIndex);
                fallbackFrameIndex++;

                if (frameIndex < windowStartFrame || frameIndex >= windowEndFrame)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }

                ffmpeg.sws_scale(swsContext, frame->data, frame->linesize, 0, height, rgbFrame->data, rgbFrame->linesize);
                AddVideoFrameToCache(frameIndex, CreateBitmapFromFrame(rgbFrame, width, height));
                cachedThroughFrame = Math.Max(cachedThroughFrame, frameIndex);

                ffmpeg.av_frame_unref(frame);
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

        private void AddVideoFrameToCache(long frameIndex, Bitmap bitmap)
        {
            lock (_lock)
            {
                if (_videoFrameCache.ContainsKey(frameIndex))
                {
                    bitmap.Dispose();
                    return;
                }

                _videoFrameCache[frameIndex] = bitmap;
                _videoFrameCacheOrder.Enqueue(frameIndex);

                while (_videoFrameCacheOrder.Count > MaxCachedVideoFrames)
                {
                    long oldIndex = _videoFrameCacheOrder.Dequeue();
                    if (Math.Abs(oldIndex - _currentFrameIndex) < 3)
                    {
                        _videoFrameCacheOrder.Enqueue(oldIndex);
                        break;
                    }

                    if (_videoFrameCache.Remove(oldIndex, out var oldFrame))
                        oldFrame.Dispose();
                }
            }
        }

        private void ClearVideoFrameCacheLocked()
        {
            foreach (var frame in _videoFrameCache.Values)
                frame.Dispose();

            _videoFrameCache.Clear();
            _videoFrameCacheOrder.Clear();
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

                // A. ?遣??皜征蝺抵??嚗Ⅱ靽?銝蝘?啁?撠望?啗身摰??脤
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

                    byte[] layoutName = new byte[64];
                    fixed (byte* pLayout = layoutName)
                        // 撠?(int) ?寧 (nuint) 隞亦泵??size_t ??瘙?
                        ffmpeg.av_channel_layout_describe(&ctx->ch_layout, pLayout, (ulong)layoutName.Length);

                    string args = $"sample_rate={ctx->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(ctx->sample_fmt)}:channel_layout={System.Text.Encoding.UTF8.GetString(layoutName).TrimEnd('\0')}";

                    AVFilterContext* srcCtx;
                    string name = $"in{i}";
                    ffmpeg.avfilter_graph_create_filter(&srcCtx, abuffer, name, args, null, _filterGraph);
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
                ffmpeg.avfilter_graph_create_filter(&sinkCtx, abuffersink, "out", null, null, _filterGraph);
                _sinkContext = sinkCtx;

                AVSampleFormat[] fmts = { AVSampleFormat.AV_SAMPLE_FMT_S16, AVSampleFormat.AV_SAMPLE_FMT_NONE };
                fixed (AVSampleFormat* pFmts = fmts)
                    ffmpeg.av_opt_set_bin(_sinkContext, "sample_fmts", (byte*)pFmts, sizeof(AVSampleFormat) * 1, (int)ffmpeg.AV_OPT_SEARCH_CHILDREN);

                AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = _sinkContext;
                inputs->pad_idx = 0;
                inputs->next = null;

                // B. 雿輻靽格迤敺? BuildFilterDesc
                string filterDesc = BuildFilterDesc(rate);

                int ret = ffmpeg.avfilter_graph_parse_ptr(_filterGraph, filterDesc, &inputs, &outputs, null);
                
                if (ret < 0)
                {
                    // ?? FFmpeg ?航炊閮
                    byte* errBuff = (byte*)Marshal.AllocHGlobal(256);
                    ffmpeg.av_strerror(ret, errBuff, 256);
                    string errMsg = Marshal.PtrToStringAnsi((IntPtr)errBuff);
                    Marshal.FreeHGlobal((IntPtr)errBuff);

                    System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] {errMsg}");
                    return; // ?ㄐ憭望?鈭?DecodeLoop 撠曹???雿?撠?∟
                }
                if (ret >= 0)
                {
                    ffmpeg.avfilter_graph_config(_filterGraph, null);
                    _filterReady = true; // ?遣摰?嚗銵?DecodeLoop
                }

                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
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

            // 3. ??霈???
            string tempoFilters = "";
            // ?詨?靽格迤嚗ate < 0 ?身??volume=0
            if (rate <= 0)
            {
                tempoFilters = "volume=0";
            }
            else
            {
                // 甇??霈?頛?(atempo ??
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
            const long windowMs = 120;
            float peak = 0f;

            lock (_meterSamplesLock)
            {
                for (int i = _meterSamples.Count - 1; i >= 0; i--)
                {
                    var s = _meterSamples[i];

                    if (s.TimeMs < currentTimeMs - windowMs)
                        break;

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
        private void DecodeLoop(long startTimeMs, CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine("[Decode] started");

            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVFrame* filtFrame = ffmpeg.av_frame_alloc();

            try
            {
                // startTimeMs = 0 銝? seek嚗??MXF ?∩?
                if (startTimeMs > 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Decode] seek start");

                    lock (_lock)
                    {
                        if (_formatContext != null)
                        {
                            long seekTarget = startTimeMs * 1000;
                            int seekRet = ffmpeg.av_seek_frame(
                                _formatContext,
                                -1,
                                seekTarget,
                                ffmpeg.AVSEEK_FLAG_BACKWARD
                            );

                            System.Diagnostics.Debug.WriteLine($"[Decode] seek ret={seekRet}");
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine("[Decode] loop enter");

                while (!token.IsCancellationRequested && _isDecoding)
                {
                    int readRet;

                    lock (_lock)
                    {
                        if (_formatContext == null)
                            break;

                        readRet = ffmpeg.av_read_frame(_formatContext, packet);
                    }

                    if (readRet < 0)
                    {
                        if (readRet == ffmpeg.AVERROR_EOF)
                        {
                            //System.Diagnostics.Debug.WriteLine("[Decode] EOF");
                            //break;
                        }

                        Thread.Sleep(1);
                        continue;
                    }

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
                    {
                        System.Diagnostics.Debug.WriteLine($"[SendPacket Error] stream={streamIndex}, ret={sendRet}");
                        continue;
                    }

                    while (true)
                    {
                        int recvRet = ffmpeg.avcodec_receive_frame(ctxWrapper.Ptr, frame);

                        if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet == ffmpeg.AVERROR_EOF)
                            break;

                        if (recvRet < 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ReceiveFrame Error] stream={streamIndex}, ret={recvRet}");
                            break;
                        }

                        if (idx >= 0 && idx < 8)
                        {

                            float peak = CalculatePeakFromFrame(frame);

                            long ptsMs = 0;
                            var stream = _formatContext->streams[streamIndex];

                            if (frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
                            {
                                ptsMs = ffmpeg.av_rescale_q(
                                    frame->best_effort_timestamp,
                                    stream->time_base,
                                    new AVRational { num = 1, den = 1000 }
                                );
                            }

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
                                            _waveProvider.AddSamples(pcmData, 0, pcmData.Length);
                                    }
                                }
                            }
                        }

                        ffmpeg.av_frame_unref(frame);
                    }

                    //float currentRate = Math.Abs(_mp.Rate);
                    //if (currentRate <= 0) currentRate = 1;

                    //int bufferLimit = currentRate > 2.0f ? 2000 : 500;

                    //if (_waveProvider.BufferedDuration.TotalMilliseconds > bufferLimit)
                    //    Thread.Sleep(currentRate > 4.0f ? 5 : 15);
                    //else
                    //    Thread.Sleep(0);
                    // 潃?摰銝?撟脫?唾? pipeline
                    if (_waveProvider.BufferedDuration.TotalMilliseconds > 4000)
                    {
                        Thread.Sleep(1); // ?芸??頛凝靽風
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Decode Error] {ex.Message}");
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_frame_free(&filtFrame);

                System.Diagnostics.Debug.WriteLine("[Decode] ended");
            }
        }

        private void DecodeToFile(Stream output, CancellationToken token)
        {
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVFrame* filtFrame = ffmpeg.av_frame_alloc();

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

                        if (idx >= 0 && idx < 8)
                        {
                            float peak = CalculatePeakFromFrame(frame);
                            long ptsMs = 0;
                            var stream = _formatContext->streams[streamIndex];

                            if (frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
                            {
                                ptsMs = ffmpeg.av_rescale_q(
                                    frame->best_effort_timestamp,
                                    stream->time_base,
                                    new AVRational { num = 1, den = 1000 }
                                );
                            }

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
                                            output.Write(pcmData, 0, pcmData.Length);
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
        private byte[] ExtractPcm(AVFrame* frame)
        {
            int channels = frame->ch_layout.nb_channels;
            int size = ffmpeg.av_samples_get_buffer_size(null, channels, frame->nb_samples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
            byte[] res = new byte[size];
            Marshal.Copy((IntPtr)frame->data[0], res, 0, size);
            return res;
        }


        public void Pause()
        {
            _isVideoPlaying = false;
            _playbackClock.Stop();
            try { _waveOut?.Pause(); } catch { }

            // ???歇蝬??圾憟賜??脤嚗??resume ???buffer
            _waveProvider?.ClearBuffer();
        }

        public void ResumeAudio()
        {
            SeekAudioByFrame(_currentFrameIndex, _audioFps);
            _playbackStartFrame = _currentFrameIndex;
            WaitForAudioBuffer(_currentFrameIndex, _audioFps, _videoRate, 1000);
            _playbackClock.Restart();
            _isVideoPlaying = true;
            PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
        }

        public void SetAudioRate(float rate)
        {
            float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;

            if (_fileAudioProvider != null)
                _fileAudioProvider.PlaybackRate = effectiveRate;
            if (_memoryAudioProvider != null)
                _memoryAudioProvider.PlaybackRate = effectiveRate;

            if (_isVideoPlaying && _fileAudioProvider != null)
            {
                StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, effectiveRate, false);
                WaitForAudioBuffer(_currentFrameIndex, _audioFps, effectiveRate, 1000);
                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
            }
        }

        public void SetVideoRate(float rate)
        {
            AdvanceVideo(0);
            _videoRate = rate == 0 ? 1.0f : rate;
            SetAudioRate(_videoRate);
            _playbackStartFrame = _currentFrameIndex;
            if (_isVideoPlaying)
                _playbackClock.Restart();
        }

        public void AdvanceVideo(int elapsedMs)
        {
            if (!_isVideoPlaying || _totalVideoFrames <= 0) return;

            double clockElapsedMs = Math.Max(0, _playbackClock.Elapsed.TotalMilliseconds - AudioOutputLatencyMs);
            //framesToMove = floor(經過毫秒 × fps × 播放倍率 ÷ 1000)
            long framesToMove = (long)Math.Floor(clockElapsedMs * _audioFps * Math.Abs(_videoRate) / 1000.0);
            if (_videoRate >= 0)
                _currentFrameIndex = Math.Min(_totalVideoFrames - 1, _playbackStartFrame + framesToMove);
            else
                _currentFrameIndex = Math.Max(0, _playbackStartFrame - framesToMove);

            EnsureVideoDecoderNearCurrentFrame();
            EnsureAudioCacheForCurrentFrame();
            LogVideoBufferStatus();

            if ((_videoRate < 0 && _currentFrameIndex == 0) ||
                (_videoRate > 0 && _currentFrameIndex == _totalVideoFrames - 1))
                Pause();
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
            lock (_lock)
            {
                currentFrame = _currentFrameIndex;
                cacheCount = _videoFrameCache.Count;
                if (cacheCount > 0)
                {
                    minCachedFrame = _videoFrameCache.Keys.Min();
                    maxCachedFrame = _videoFrameCache.Keys.Max();
                }
            }

            displayFrame = GetDisplayFrameIndex();
            long cacheAhead = maxCachedFrame >= 0 ? maxCachedFrame - currentFrame : -1;
            string taskStatus = _videoDecodeTask?.Status.ToString() ?? "null";
            bool taskCompleted = _videoDecodeTask?.IsCompleted ?? true;

            Debug.WriteLine(
                $"[VideoBuffer] current={currentFrame} display={displayFrame} " +
                $"cache={minCachedFrame}-{maxCachedFrame} ahead={cacheAhead} count={cacheCount} " +
                $"decodeTask={taskStatus} completed={taskCompleted} rate={_videoRate:0.###}");
        }

        public void SeekVideoByFrame(long frameIndex)
        {
            if (_totalVideoFrames <= 0) return;
            _currentFrameIndex = Math.Clamp(frameIndex, 0, _totalVideoFrames - 1);
            _videoFrameAccumulator = 0;
            _playbackStartFrame = _currentFrameIndex;
            EnsureVideoDecoderNearCurrentFrame(forceRestart: true);
            if (_isVideoPlaying)
                _playbackClock.Restart();
        }

        private void EnsureVideoDecoderNearCurrentFrame(bool forceRestart = false)
        {
            if (string.IsNullOrEmpty(CurrentPath)) return;

            long minCachedFrame = -1;
            long maxCachedFrame = -1;
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
                    if (_videoRate >= 0 && maxCachedFrame - _currentFrameIndex > VideoPreloadLowWaterFrames)
                        return;

                    if (_videoRate < 0 && minCachedFrame >= 0 && _currentFrameIndex - minCachedFrame > VideoPreloadLowWaterFrames)
                        return;
                }

                restartLaggingDecoder = _videoRate >= 0
                    ? maxCachedFrame >= 0 && _currentFrameIndex > maxCachedFrame + VideoDecoderRestartGapFrames
                    : minCachedFrame >= 0 && _currentFrameIndex < minCachedFrame - VideoDecoderRestartGapFrames;
            }

            if (!forceRestart && _videoDecodeTask != null && !_videoDecodeTask.IsCompleted && !restartLaggingDecoder)
                return;

            _videoCts?.Cancel();
            //_videoCts?.Dispose();
            _videoCts = new CancellationTokenSource();
            long startFrame = forceRestart || restartLaggingDecoder || _videoRate < 0 || maxCachedFrame < _currentFrameIndex
                ? _currentFrameIndex
                : Math.Min(_totalVideoFrames - 1, maxCachedFrame + 1);
            var token = _videoCts.Token;
            _videoDecodeTask = Task.Run(() => DecodeVideoFrameWindow(CurrentPath, startFrame, token), token);
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

        public void SeekAudioByFrame(long frameIndex, double fps)
        {
            bool hasData = _fileAudioProvider != null &&
                (_videoRate < 0
                    ? _fileAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, ReverseAudioCacheRefreshBehindMs)
                    : _fileAudioProvider.IsFrameDataAvailable(frameIndex, fps));

            if (!hasData)
            {
                StartAudioCacheFromFrame(frameIndex, fps, Math.Abs(_videoRate) > 0 ? _videoRate : 1.0f, _isVideoPlaying);
                return;
            }

            _fileAudioProvider.SeekFrame(frameIndex, fps);
            _memoryAudioProvider?.SeekFrame(frameIndex, fps);
        }

        public Task PlayFrameAudioAsync(long frameIndex, double fps)
        {
            return Task.Run(() =>
            {
                if (fps <= 0) fps = CurrentFps;
                if (_fileAudioProvider == null)
                    return;

                float previousRate = _videoRate;
                bool wasPlaying = _isVideoPlaying;

                _isVideoPlaying = false;
                _playbackClock.Stop();
                _videoRate = 1.0f;

                SeekAudioByFrame(frameIndex, fps);
                WaitForAudioBuffer(frameIndex, fps, 1.0f, 1000);

                if (_fileAudioProvider != null)
                    _fileAudioProvider.PlaybackRate = 1.0f;

                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);

                int frameMs = Math.Max(35, (int)Math.Ceiling(1000.0 / fps));
                Thread.Sleep(frameMs + AudioOutputLatencyMs);

                try { _waveOut?.Pause(); } catch { }

                SeekAudioByFrame(frameIndex, fps);
                _videoRate = previousRate;
                _isVideoPlaying = wasPlaying;
            });
        }

        private void EnsureAudioCacheForCurrentFrame()
        {
            if (_videoRate >= 0 || _fileAudioProvider == null)
                return;

            if (_audioCacheTask != null && !_audioCacheTask.IsCompleted)
                return;

            if (!_fileAudioProvider.IsReverseFrameDataAvailable(_currentFrameIndex, _audioFps, ReverseAudioCacheRefreshBehindMs))
                StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, _videoRate, true);
        }
        //frameIndex = round(timeMs × fps ÷ 1000)
        public static long FrameFromTimeMs(long timeMs, double fps)
        {
            if (timeMs < 0) timeMs = 0;
            if (fps <= 0) fps = 29.97;
            return (long)Math.Round(timeMs * fps / 1000.0);
        }
        //timeMs = round(frameIndex × 1000 ÷ fps)
        public static long TimeMsFromFrame(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;
            return (long)Math.Round(frameIndex * 1000.0 / fps);
        }
        private void UpdateOriginalChannelLevel(int ch, float peak)
        {

            lock (_levelLock)
            {
                _channelLevels[ch] = Math.Max(peak, _channelLevels[ch] * 0.85f);
            }
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
                    peak = Math.Max(peak, Math.Abs(data[i]) / 32768f);
            }
            else if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S16P)
            {
                for (uint ch = 0; ch < channels; ch++)
                {
                    short* data = (short*)frame->data[ch];
                    for (int i = 0; i < samples; i++)
                        peak = Math.Max(peak, Math.Abs(data[i]) / 32768f);
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
        public void StopAudioBridge()
        {
            _isDecoding = false;

            _cts?.Cancel();
            _videoCts?.Cancel();
            _audioCacheCts?.Cancel();
            _audioCacheGeneration++;

            try
            {
                _videoDecodeTask?.Wait(500);
            }
            catch { }

            try
            {
                _audioCacheTask?.Wait(500);
            }
            catch { }

            lock (_lock)
            {
                _filterReady = false;

                try { _waveOut?.Stop(); } catch { }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;

                _fileAudioProvider?.Dispose();
                _fileAudioProvider = null;

                _memoryAudioProvider = null;
                _waveProvider?.ClearBuffer();

                ClearVideoFrameCacheLocked();
                _totalVideoFrames = 0;

                foreach (var decoder in _audioDecoders.Values)
                {
                    var p = decoder.Ptr;
                    ffmpeg.avcodec_free_context(&p);
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
            }

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
            foreach (var frame in _videoFrames)
                frame.Dispose();
            _videoFrames.Clear();
        }
    }
}
