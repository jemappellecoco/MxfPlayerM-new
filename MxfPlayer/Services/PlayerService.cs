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
        private IWavePlayer? _waveOut;
        private BufferedWaveProvider _waveProvider;
        private MxfAudioProvider? _fileAudioProvider;
        private MemoryPcmAudioProvider? _memoryAudioProvider;
        private SlidingPcmAudioProvider? _slidingAudioProvider;
        private double _audioFps = 29.97;
        private int _audioSampleRate = 48000;
        private int _pcmOutputChannels;
        public double CurrentFps => _audioFps > 0 ? _audioFps : 29.97;
        private readonly Dictionary<long, Bitmap> _videoFrameCache = new();
        private const long VideoFrameCacheBudgetBytes = 512L * 1024L * 1024L;
        private const int MinCachedVideoFrames = 48;
        private const int MaxCachedVideoFrames = 300; // Hard cap for decoded bitmaps kept in memory.
        private int _maxCachedVideoFrames = 120;
        private const int VideoPreloadLowWaterFrames = 900; // Start refilling when forward buffer drops below this.
        private const int VideoPreloadHighWaterFrames = 1500; // Pause background decode when this much is ready.
        private const int ReverseVideoDecodeWindowFrames = 300; // Keep reverse decode close to the playhead.
        private const int ReverseVideoPreloadLowWaterFrames = 360; // Refill reverse cache before the continuous window runs dry.
        private const int VideoDecoderRestartGapFrames = 30; // Restart decoder if playback has outrun the cached window.
        private const int VideoDecoderRestartCooldownMs = 1500;
        private const double VideoStallResumeBufferSeconds = 0.75;//播放中卡住後：等 0.75 秒
        private long _currentFrameIndex;
        private long _totalVideoFrames;
        private CancellationTokenSource? _videoCts;
        private Task? _videoDecodeTask;
        private int _videoDecodeGeneration;
        private CancellationTokenSource? _audioCacheCts;
        private Task? _audioCacheTask;
        private string? _pcmCachePath;
        private int _audioCacheGeneration;
        private bool _isVideoPlaying;
        private bool _isPlaybackStalledForVideo;
        private float _videoRate = 1.0f;
        private readonly Stopwatch _playbackClock = new();
        private readonly Stopwatch _videoStatusLogClock = Stopwatch.StartNew();
        private readonly Stopwatch _videoDecodeRestartClock = Stopwatch.StartNew();
        private long _playbackStartFrame;
        private const int AudioOutputLatencyMs = 100;
        private const int ReverseAudioCacheWindowMs = 60000;
        private const int ReverseAudioCacheRefreshBehindMs = 5000;
        private const int ReverseAudioMinRefreshBehindMs = 15000;
        private const int ReverseAudioInitialPlaybackMs = 2000;
        private const int ReverseAudioInitialMaxSourceMs = 12000;

        private readonly object _lock = new object();

        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };
        public string CurrentPath { get; private set; } = string.Empty;
        public int CurrentAudioCount { get; private set; }
        public bool IsAudioReady => _waveOut != null && (_fileAudioProvider != null || _slidingAudioProvider != null);
        public bool IsPlaying => _isVideoPlaying;
        public long CurrentFrameIndex => _currentFrameIndex;

        public int GetRequiredVideoBufferFramesForRate(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int requestedFrames = (int)Math.Ceiling(VideoPreloadLowWaterFrames * multiplier);
            int maxFrames = GetMaxCachedVideoFramesForRate(rate);
            int reserveFrames = Math.Max(6, maxFrames / 4);
            return Math.Clamp(Math.Min(requestedFrames, maxFrames - reserveFrames), 3, maxFrames);
        }

        private int GetVideoPreloadHighWaterFramesForRate(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int requestedFrames = (int)Math.Ceiling(VideoPreloadHighWaterFrames * multiplier);
            int maxFrames = GetMaxCachedVideoFramesForRate(rate);
            int reserveFrames = Math.Max(3, maxFrames / 8);
            return Math.Clamp(Math.Min(requestedFrames, maxFrames - reserveFrames), 3, maxFrames);
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

                long requiredFrames = GetRequiredVideoBufferFramesForRate(rate);
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
                if (next < 0 || (_totalVideoFrames > 0 && next >= _totalVideoFrames))
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
        public long CurrentTimeMs => TimeMsFromFrame(_currentFrameIndex, _audioFps);
        public long LengthMs => _totalVideoFrames <= 0 ? 0 : TimeMsFromFrame(_totalVideoFrames - 1, _audioFps);
        public long LastFrameIndex => Math.Max(0, _totalVideoFrames - 1);

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
                var config = AppConfigService.Load();
                if (!string.IsNullOrWhiteSpace(config.FFmpegPath))
                    ffmpeg.RootPath = config.FFmpegPath;
            }
            catch { }
        }

        public Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0, float rate = 1.0f, double fps = 0, int sampleRate = 48000)
        {
            fps = fps > 0 ? fps : CurrentFps;

            LoadForBufferedPlayback(path, audioCount, startTimeMs, rate, fps, sampleRate);
            return WaitForFrameBufferAsync(FrameFromTimeMs(startTimeMs, fps), 3000);
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
                _currentFrameIndex = Math.Clamp(startFrame, 0, Math.Max(0, _totalVideoFrames - 1));
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

                if (!string.IsNullOrEmpty(_pcmCachePath))
                {
                    try { File.Delete(_pcmCachePath); } catch { }
                    _pcmCachePath = null;
                }

                float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;
                bool reverse = effectiveRate < 0;
                long cacheStartFrame = frameIndex;

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

                int? maxDurationMs = reverse
                    ? (int)Math.Ceiling(ReverseAudioCacheWindowMs * Math.Abs(effectiveRate) + GetReverseAudioRefreshBehindMs(effectiveRate))
                    : null;

                _audioCacheTask = Task.Run(() =>
                {
                    CreatePcmCacheFile(path, pcmPath, cacheStartFrame, fps, token, maxDurationMs);
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

        private void StartReverseSlidingAudioCacheFromFrame(long frameIndex, double fps, float rate, bool keepPlaying)
        {
            float effectiveRate = rate < 0 ? rate : -Math.Max(1.0f, Math.Abs(rate));
            int initialBehindMs = GetInitialReverseAudioBehindMs(effectiveRate);
            long reverseWindowFrames = FrameFromTimeMs(initialBehindMs, fps);
            long cacheStartFrame = Math.Max(0, frameIndex - reverseWindowFrames);
            int cacheChannelCount = Math.Clamp(_pcmOutputChannels > 0 ? _pcmOutputChannels : CurrentAudioCount, 1, ChannelMask.Length);

            _slidingAudioProvider = new SlidingPcmAudioProvider(cacheChannelCount, _audioSampleRate)
            {
                PlaybackRate = effectiveRate,
                Mask = ChannelMask
            };
            _slidingAudioProvider.SeekFrame(frameIndex, fps);

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100
            };
            _waveOut.Init(_slidingAudioProvider);
            var waveOut = _waveOut;
            var provider = _slidingAudioProvider;
            int generation = _audioCacheGeneration;

            var token = _audioCacheCts?.Token ?? CancellationToken.None;
            string path = CurrentPath;
            int maxDurationMs = initialBehindMs + 1500;

            _audioCacheTask = Task.Run(() =>
            {
                byte[] pcm = DecodePcmCacheBytes(path, cacheStartFrame, fps, token, maxDurationMs, clearMeterSamples: !keepPlaying);
                if (token.IsCancellationRequested || generation != _audioCacheGeneration)
                    return;

                provider.ResetWindow(cacheStartFrame, fps, pcm);
                provider.SeekFrame(frameIndex, fps);
            }, token);

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

            _audioCacheTask = Task.Run(() =>
            {
                byte[] pcm = DecodePcmCacheBytes(path, cacheStartFrame, fps, token, maxDurationMs, clearMeterSamples: false);
                if (token.IsCancellationRequested || generation != _audioCacheGeneration)
                    return;

                provider.PrependWindow(cacheStartFrame, fps, pcm);
            }, token);
        }

        private int GetReverseAudioRefreshBehindMs(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            int scaled = (int)Math.Ceiling(ReverseAudioCacheRefreshBehindMs * multiplier * 2.0);
            return Math.Max(ReverseAudioMinRefreshBehindMs, scaled);
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

        private bool WaitForAudioBuffer(long frameIndex, double fps, float rate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_fileAudioProvider == null && _slidingAudioProvider == null)
                    return false;

                bool available;
                if (rate < 0)
                {
                    available = _slidingAudioProvider != null
                        ? _slidingAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 500)
                        : _fileAudioProvider != null && _fileAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 500);
                }
                else
                {
                    available = _fileAudioProvider != null && _fileAudioProvider.IsFrameDataAvailable(frameIndex, fps, 250);
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

        private void CreatePcmCacheFile(string path, string pcmPath, long startFrame, double fps, CancellationToken token, int? maxDurationMs = null)
        {
            lock (_ffmpegResourceLock)
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
            bool clearMeterSamples)
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
                int requiredBufferedFrames = Math.Min(requiredAheadFrames, GetMaxCachedVideoFramesForRate(_videoRate));
                long requiredAhead = Math.Max(0, requiredBufferedFrames - 1);

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_lock)
                    {
                        long continuousFrame = GetContinuousCachedFrameLimit(clampedFrame, true);
                        if (continuousFrame >= 0 &&
                            continuousFrame - clampedFrame >= requiredAhead)
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

        private Bitmap CreateBitmapFromFrame(AVFrame* rgbFrame, int width, int height, bool displaySingleField = false, int fieldOffset = 0)
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
                    int sourceY = displaySingleField
                        ? Math.Min(height - 1, ((y / 2) * 2) + fieldOffset)
                        : y;
                    byte* source = rgbFrame->data[0] + (sourceY * sourceStride);
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
                UpdateVideoFrameCacheLimit(width, height);
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

                long windowEndFrame = endFrameExclusive ?? (_totalVideoFrames > 0 ? _totalVideoFrames : long.MaxValue);
                long lastWindowFrame = windowEndFrame - 1;
                long fallbackFrameIndex = seekFrame;
                long cachedThroughFrame = startFrame - 1;

                while (!token.IsCancellationRequested &&
                       IsCurrentVideoDecodeGeneration(decodeGeneration) &&
                       cachedThroughFrame < lastWindowFrame &&
                       ffmpeg.av_read_frame(formatContext, packet) >= 0)
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
                            decodeGeneration,
                            token);

                        if (cachedThroughFrame >= lastWindowFrame)
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
            int decodeGeneration,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested &&
                   IsCurrentVideoDecodeGeneration(decodeGeneration) &&
                   ffmpeg.avcodec_receive_frame(codecContext, frame) == 0)
            {
                long frameIndex = GetFrameIndexFromTimestamp(frame, streamTimeBase, streamStartTime, fallbackFrameIndex);
                fallbackFrameIndex++;

                if (frameIndex < windowStartFrame || frameIndex >= windowEndFrame)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }

                WaitForVideoDecodeHeadroom(frameIndex, decodeGeneration, token);
                if (token.IsCancellationRequested || !IsCurrentVideoDecodeGeneration(decodeGeneration))
                {
                    ffmpeg.av_frame_unref(frame);
                    break;
                }

                bool displaySingleField = ShouldDisplaySingleField(frame);
                int fieldOffset = displaySingleField ? GetFirstFieldOffset(frame) : 0;
                ffmpeg.sws_scale(swsContext, frame->data, frame->linesize, 0, height, rgbFrame->data, rgbFrame->linesize);
                AddVideoFrameToCache(frameIndex, CreateBitmapFromFrame(rgbFrame, width, height, displaySingleField, fieldOffset), decodeGeneration);
                cachedThroughFrame = Math.Max(cachedThroughFrame, frameIndex);

                ffmpeg.av_frame_unref(frame);
            }
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

                if (frameIndex - currentFrame < GetVideoPreloadHighWaterFramesForRate(_videoRate))
                    return;

                Thread.Sleep(10);
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

        private void AddVideoFrameToCache(long frameIndex, Bitmap bitmap, int decodeGeneration)
        {
            lock (_lock)
            {
                if (!IsCurrentVideoDecodeGeneration(decodeGeneration))
                {
                    bitmap.Dispose();
                    return;
                }

                if (_videoFrameCache.ContainsKey(frameIndex))
                {
                    bitmap.Dispose();
                    return;
                }

                _videoFrameCache[frameIndex] = bitmap;
                TrimVideoFrameCacheLocked(GetMaxCachedVideoFramesForRate(_videoRate));
            }
        }

        private void UpdateVideoFrameCacheLimit(int width, int height)
        {
            long frameBytes = Math.Max(1L, width) * Math.Max(1L, height) * 3L;
            int targetFrames = (int)Math.Clamp(
                VideoFrameCacheBudgetBytes / frameBytes,
                MinCachedVideoFrames,
                MaxCachedVideoFrames);

            lock (_lock)
            {
                _maxCachedVideoFrames = targetFrames;
                TrimVideoFrameCacheLocked(targetFrames);
            }
        }

        private void TrimVideoFrameCacheLocked(int maxCachedFrames)
        {
            while (_videoFrameCache.Count > maxCachedFrames)
            {
                long victimIndex = _videoRate >= 0
                    ? _videoFrameCache.Keys
                        .Where(index => index < _currentFrameIndex - 3)
                        .DefaultIfEmpty(long.MinValue)
                        .Min()
                    : _videoFrameCache.Keys
                        .Where(index => index > _currentFrameIndex + 3)
                        .DefaultIfEmpty(long.MinValue)
                        .Max();

                if (victimIndex == long.MinValue)
                {
                    victimIndex = _videoFrameCache.Keys
                        .Where(index => Math.Abs(index - _currentFrameIndex) >= 3)
                        .OrderByDescending(index => Math.Abs(index - _currentFrameIndex))
                        .DefaultIfEmpty(long.MinValue)
                        .First();
                }

                if (victimIndex == long.MinValue)
                    break;

                if (_videoFrameCache.Remove(victimIndex, out var oldFrame))
                    oldFrame.Dispose();
            }
        }

        private void ClearVideoFrameCacheLocked()
        {
            foreach (var frame in _videoFrameCache.Values)
                frame.Dispose();

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

                    byte[] layoutName = new byte[64];
                    fixed (byte* pLayout = layoutName)
                    
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

              
                string filterDesc = BuildFilterDesc(rate);

                int ret = ffmpeg.avfilter_graph_parse_ptr(_filterGraph, filterDesc, &inputs, &outputs, null);
                
                if (ret < 0)
                {
                 
                    byte* errBuff = (byte*)Marshal.AllocHGlobal(256);
                    ffmpeg.av_strerror(ret, errBuff, 256);
                    string errMsg = Marshal.PtrToStringAnsi((IntPtr)errBuff);
                    Marshal.FreeHGlobal((IntPtr)errBuff);

                    System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] {errMsg}");
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

            if (_videoRate < 0 && _slidingAudioProvider != null)
                return _slidingAudioProvider.GetChannelPeakAtFrame(frameIndex, _audioFps, channel);

            if (_fileAudioProvider != null)
                return _fileAudioProvider.GetChannelPeakAtFrame(frameIndex, _audioFps, channel);

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
            _playbackClock.Stop();
            try { _waveOut?.Pause(); } catch { }

          
            _waveProvider?.ClearBuffer();
        }

        public void ResumeAudio(int audioBufferTimeoutMs = 3000)
        {
            ClearFrameAudioPeaks();
            SeekAudioByFrame(_currentFrameIndex, _audioFps);
            _playbackStartFrame = _currentFrameIndex;
            var waveOut = _waveOut;
            int generation = _audioCacheGeneration;

            if (audioBufferTimeoutMs > 0)
            {
                WaitForAudioBuffer(_currentFrameIndex, _audioFps, _videoRate, audioBufferTimeoutMs);
                PlayWaveOutIfCurrent(waveOut, generation);
            }
            else
            {
                long frameIndex = _currentFrameIndex;
                double fps = _audioFps;
                float rate = _videoRate;

                Task.Run(() =>
                {
                    WaitForAudioBuffer(frameIndex, fps, rate, 1000);
                    PlayWaveOutIfCurrent(waveOut, generation);
                });
            }

            _playbackClock.Restart();
            _isVideoPlaying = true;
            _isPlaybackStalledForVideo = false;
        }

        public void SetAudioRate(float rate)
        {
            float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;

            if (_isVideoPlaying && effectiveRate >= 0 && _fileAudioProvider == null)
            {
                StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, effectiveRate, false);
                WaitForAudioBuffer(_currentFrameIndex, _audioFps, effectiveRate, 1000);
                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
                return;
            }

            if (_fileAudioProvider != null)
                _fileAudioProvider.PlaybackRate = effectiveRate;
            if (_memoryAudioProvider != null)
                _memoryAudioProvider.PlaybackRate = effectiveRate;
            if (_slidingAudioProvider != null)
                _slidingAudioProvider.PlaybackRate = effectiveRate;

            if (_isVideoPlaying && _fileAudioProvider != null)
            {
                StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, effectiveRate, false);
                WaitForAudioBuffer(_currentFrameIndex, _audioFps, effectiveRate, 1000);
                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
            }
        }

        public void SetVideoRate(float rate)
        {
            float previousRate = _videoRate;
            AdvanceVideo(0);
            _videoRate = rate == 0 ? 1.0f : rate;
            SetAudioRate(_videoRate);
            _playbackStartFrame = _currentFrameIndex;
            if (Math.Sign(previousRate) != Math.Sign(_videoRate))
                EnsureVideoDecoderNearCurrentFrame(forceRestart: true);
            if (_isVideoPlaying)
                _playbackClock.Restart();
        }

        public void PrepareAudioForRate(float rate)
        {
            if (string.IsNullOrEmpty(CurrentPath))
                return;

            float effectiveRate = Math.Abs(rate) > 0 ? rate : 1.0f;

            if (effectiveRate < 0)
            {
                if (_slidingAudioProvider == null ||
                    !_slidingAudioProvider.IsReverseFrameDataAvailable(
                        _currentFrameIndex,
                        _audioFps,
                        GetInitialReverseAudioBehindMs(effectiveRate)))
                {
                    StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, effectiveRate, _isVideoPlaying);
                }

                return;
            }

            if (_fileAudioProvider == null ||
                !_fileAudioProvider.IsFrameDataAvailable(_currentFrameIndex, _audioFps, 250))
            {
                StartAudioCacheFromFrame(_currentFrameIndex, _audioFps, effectiveRate, _isVideoPlaying);
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

            double clockElapsedMs = Math.Max(0, _playbackClock.Elapsed.TotalMilliseconds - AudioOutputLatencyMs);
            // framesToMove = floor(elapsedMs * fps * playbackRate / 1000)
            long framesToMove = (long)Math.Floor(clockElapsedMs * _audioFps * Math.Abs(_videoRate) / 1000.0);
            long targetFrame;
            if (_videoRate >= 0)
                targetFrame = Math.Min(_totalVideoFrames - 1, _playbackStartFrame + framesToMove);
            else
                targetFrame = Math.Max(0, _playbackStartFrame - framesToMove);

            if (IsVideoFrameCached(targetFrame))
            {
                _currentFrameIndex = targetFrame;
            }
            else
            {
                StallPlaybackForVideoBuffer();
            }

            EnsureVideoDecoderNearCurrentFrame();
            EnsureAudioCacheForCurrentFrame();
            LogVideoBufferStatus();

            if ((_videoRate < 0 && _currentFrameIndex == 0) ||
                (_videoRate > 0 && _currentFrameIndex == _totalVideoFrames - 1))
                Pause();
        }

        private bool IsVideoFrameCached(long frameIndex)
        {
            lock (_lock)
                return _videoFrameCache.ContainsKey(frameIndex);
        }

        private void StallPlaybackForVideoBuffer()
        {
            if (_isPlaybackStalledForVideo)
                return;

            _isPlaybackStalledForVideo = true;
            _playbackStartFrame = _currentFrameIndex;
            _playbackClock.Stop();
            try { _waveOut?.Pause(); } catch { }
        }

        private void ResumePlaybackAfterVideoBufferIfReady()
        {
            if (!HasVideoStallResumeBuffer())
                return;

            SeekAudioByFrame(_currentFrameIndex, _audioFps);
            WaitForAudioBuffer(_currentFrameIndex, _audioFps, _videoRate, 1000);
            PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);
            _playbackStartFrame = _currentFrameIndex;
            _playbackClock.Restart();
            _isPlaybackStalledForVideo = false;
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
            int requestedFrames = (int)Math.Ceiling(_audioFps * rate * VideoStallResumeBufferSeconds);
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
            _currentFrameIndex = ClampFrameIndex(frameIndex);
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
                            continuousCachedFrame - _currentFrameIndex > GetRequiredVideoBufferFramesForRate(_videoRate))
                        {
                            return;
                        }
                    }
                    else if (continuousCachedFrame >= 0 &&
                             _currentFrameIndex - continuousCachedFrame > GetReverseVideoPreloadLowWaterFrames())
                    {
                        return;
                    }
                }

                restartLaggingDecoder = _videoRate >= 0
                    ? maxCachedFrame >= 0 && _currentFrameIndex > maxCachedFrame + VideoDecoderRestartGapFrames
                    : continuousCachedFrame >= 0 && _currentFrameIndex < continuousCachedFrame - VideoDecoderRestartGapFrames;
            }

            if (!forceRestart && _videoDecodeTask != null && !_videoDecodeTask.IsCompleted)
            {
                if (!restartLaggingDecoder ||
                    Math.Abs(_videoRate) <= 1.0f ||
                    _videoDecodeRestartClock.ElapsedMilliseconds < VideoDecoderRestartCooldownMs)
                {
                    return;
                }
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
                int reverseWindowFrames = Math.Min(ReverseVideoDecodeWindowFrames, GetMaxCachedVideoFramesForRate(_videoRate));
                startFrame = Math.Max(0, _currentFrameIndex - reverseWindowFrames + 1);
                endFrameExclusive = Math.Min(_totalVideoFrames, _currentFrameIndex + 1);
            }
            else
            {
                startFrame = forceRestart || restartLaggingDecoder || maxCachedFrame < _currentFrameIndex
                    ? _currentFrameIndex
                    : Math.Min(_totalVideoFrames - 1, maxCachedFrame + 1);
            }
            var token = _videoCts.Token;
            int decodeGeneration = Interlocked.Increment(ref _videoDecodeGeneration);
            _videoDecodeRestartClock.Restart();
            _videoDecodeTask = Task.Run(() => DecodeVideoFrameWindow(CurrentPath, startFrame, decodeGeneration, token, endFrameExclusive), token);
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

            bool hasData = _videoRate < 0
                ? (_slidingAudioProvider != null &&
                   _slidingAudioProvider.IsReverseFrameDataAvailable(frameIndex, fps, 250))
                : (_fileAudioProvider != null &&
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
                _slidingAudioProvider?.SeekFrame(frameIndex, fps);
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
                _fileAudioProvider?.SeekFrame(frameIndex, fps);
                CaptureFrameAudioPeaks(frameIndex, fps);

                if (_fileAudioProvider != null)
                    _fileAudioProvider.PlaybackRate = 1.0f;

                PlayWaveOutIfCurrent(_waveOut, _audioCacheGeneration);

                int frameMs = Math.Max(35, (int)Math.Ceiling(1000.0 / fps));
                Thread.Sleep(frameMs + AudioOutputLatencyMs);

                try { _waveOut?.Pause(); } catch { }

                _fileAudioProvider?.SeekFrame(frameIndex, fps);
                _videoRate = previousRate;
                _isVideoPlaying = wasPlaying;
            });
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

        private void EnsureAudioCacheForCurrentFrame()
        {
            if (_videoRate >= 0 || _slidingAudioProvider == null)
                return;

            if (_audioCacheTask != null && !_audioCacheTask.IsCompleted)
                return;

            if (!_slidingAudioProvider.IsReverseFrameDataAvailable(_currentFrameIndex, _audioFps, GetReverseAudioRefreshBehindMs(_videoRate)))
                StartReverseSlidingPrepend(_currentFrameIndex, _audioFps, _videoRate);
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

                _memoryAudioProvider = null;
                _slidingAudioProvider = null;
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
        }
    }
}

