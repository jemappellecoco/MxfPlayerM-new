using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using NAudio.Wave;
using FFmpeg.AutoGen;
using System.Linq;

namespace MxfPlayer.Services
{
    public class ConfigModel
    {
        public string FFmpegPath { get; set; }
    }

    public unsafe class PlayerService : IDisposable
    {
        private readonly LibVLC _vlc;
        private readonly MediaPlayer _mp;
        private bool _filterReady = false; // 關鍵：確保解碼執行緒不會在重建期間推入資料
        private Media? _currentMedia;
        // --- 核心解碼變數 ---
        private AVFormatContext* _formatContext;
        private Dictionary<int, PointerWrapper<AVCodecContext>> _audioDecoders = new();
        private List<int> _audioStreamIndices = new();

        // --- 濾鏡核心變數 ---
        private AVFilterGraph* _filterGraph;
        private AVFilterContext** _srcContexts;
        private AVFilterContext* _sinkContext;
        private int _activeAudioTrackCount = 0;

        private const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;
        private IWavePlayer _waveOut;
        private BufferedWaveProvider _waveProvider;

        private CancellationTokenSource _cts;
        private bool _isDecoding;
        private readonly object _lock = new object();

        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };
        public MediaPlayer MediaPlayer => _mp;
        public string CurrentPath { get; private set; }
        public int CurrentAudioCount { get; private set; }

        private class PointerWrapper<T> where T : unmanaged { public T* Ptr; }

        public PlayerService()
        {
            LoadFFmpegFromConfig();
            _vlc = new LibVLC();
            _mp = new MediaPlayer(_vlc);
            _mp.Mute = true;

            _waveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(3000),
                DiscardOnBufferOverflow = true
            };
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

        public async Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0, float rate = 1.0f)
        {
            StopAudioBridge(); // 確保乾淨的開始
            CurrentPath = path;
            CurrentAudioCount = audioCount;
            _cts = new CancellationTokenSource();

            // 1. 初始化 FFmpeg
            InitFFmpeg(path, rate);

            // 2. 啟動音訊輸出 (NAudio)
            _waveOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            _waveOut.Init(_waveProvider);
            _waveOut.Play(); // 必須 Play 才有聲音

            // 3. 啟動解碼執行緒
            _isDecoding = true; // 必須設為 true
            Task.Run(() => DecodeLoop(startTimeMs, _cts.Token)); // 啟動背景解碼

            // 4. 初始化 VLC 媒體
            _currentMedia = new Media(_vlc, path, FromType.FromPath);

            // 高倍速優化：增加緩存並降低硬體解碼抖動
            _currentMedia.AddOption(":hwdec=auto");
            _currentMedia.AddOption(":file-caching=2000"); // 增加快取有助於高倍速穩定
            _currentMedia.AddOption($":start-time={startTimeMs / 1000.0}");
            _currentMedia.AddOption(":clock-synchro=0"); // 告訴 VLC 不要因為音訊同步而等待

            // 5. 使用事件回調，絕對不使用 Wait() 阻塞 UI
            EventHandler<MediaPlayerMediaChangedEventArgs> handler = null!;
            handler = (s, e) => {
                _mp.MediaChanged -= handler;
                // 在背景執行緒或非同步上下文設定 Rate，避免干擾渲染
                Task.Run(() => {
                    Thread.Sleep(100); // 給予底層解碼器極短的緩衝時間
                    _mp.SetRate(rate);
                });
            };
            _mp.MediaChanged += handler;

            _mp.Play(_currentMedia);
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

        public void UpdateFilterGraph(float rate)
        {
            lock (_lock)
            {
                _filterReady = false;

                // A. 重建前先清空緩衝區，確保下一秒聽到的就是新設定的聲音
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

                if (_activeAudioTrackCount == 0) return;

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

                // B. 使用修正後的 BuildFilterDesc
                string filterDesc = BuildFilterDesc(rate);

                int ret = ffmpeg.avfilter_graph_parse_ptr(_filterGraph, filterDesc, &inputs, &outputs, null);
                
                if (ret < 0)
                {
                    // 取得 FFmpeg 錯誤訊息
                    byte* errBuff = (byte*)Marshal.AllocHGlobal(256);
                    ffmpeg.av_strerror(ret, errBuff, 256);
                    string errMsg = Marshal.PtrToStringAnsi((IntPtr)errBuff);
                    Marshal.FreeHGlobal((IntPtr)errBuff);

                    System.Diagnostics.Debug.WriteLine($"[FFmpeg Filter Error] {errMsg}");
                    return; // 這裡失敗了，DecodeLoop 就不會運作，導致無聲
                }
                if (ret >= 0)
                {
                    ffmpeg.avfilter_graph_config(_filterGraph, null);
                    _filterReady = true; // 重建完成，放行 DecodeLoop
                }

                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
            }
        }

        private string BuildFilterDesc(float rate)
        {
            // 1. 建立基礎混音：將所有音軌合併成一個 merged 串流
            string amerge = "";
            for (int i = 0; i < _activeAudioTrackCount; i++) amerge += $"[in{i}]";
            amerge += $"amerge=inputs={_activeAudioTrackCount}[merged]";

            // 2. 聲道路由 (pan)：根據 ChannelMask 決定哪些聲道進 L/R
            List<string> leftChannels = new List<string>();
            List<string> rightChannels = new List<string>();
            for (int i = 0; i < _activeAudioTrackCount; i++)
            {
                if (i < ChannelMask.Length && ChannelMask[i])
                {
                    if (i % 2 == 0) leftChannels.Add($"c{i}");
                    else rightChannels.Add($"c{i}");
                }
            }
            string leftMap = leftChannels.Count > 0 ? string.Join("+", leftChannels) : "0";
            string rightMap = rightChannels.Count > 0 ? string.Join("+", rightChannels) : "0";
            string pan = $"[merged]pan=stereo|c0={leftMap}|c1={rightMap}[panned]";

            // 3. 處理變速濾鏡鏈
            string tempoFilters = "";
            if (rate <= 0)
            {
                // 暫時將 0x 或負數速設為靜音，直到實作 areverse
                tempoFilters = "volume=0";
            }
            else
            {
                List<string> listatempo = new List<string>();
                float tempRate = rate;

                // 當倍速大於 2.0，不斷疊加 2.0x 濾鏡
                while (tempRate > 2.0f)
                {
                    listatempo.Add("atempo=2.0");
                    tempRate /= 2.0f;
                }

                // 當倍速小於 0.5，不斷疊加 0.5x 濾鏡
                while (tempRate < 0.5f)
                {
                    listatempo.Add("atempo=0.5");
                    tempRate /= 0.5f;
                }

                // 最後補上剩餘的倍速（如果是 1.0 則會自動忽略或微調）
                if (tempRate != 1.0f || listatempo.Count == 0)
                {
                    listatempo.Add($"atempo={tempRate:F2}");
                }

                tempoFilters = string.Join(",", listatempo);
            }

            // 4. 最終組合：混合 -> 聲道路由 -> 變速鏈 -> 強制轉碼
            return $"{amerge};{pan};[panned]{tempoFilters},aresample=48000,aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo";
        }
        private void DecodeLoop(long startTimeMs, CancellationToken token)
        {
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            AVFrame* filtFrame = ffmpeg.av_frame_alloc();

            try
            {
                // 1. 尋求起始時間點
                lock (_lock)
                {
                    if (_formatContext != null)
                        ffmpeg.av_seek_frame(_formatContext, -1, startTimeMs * 1000, ffmpeg.AVSEEK_FLAG_BACKWARD);
                }

                while (!token.IsCancellationRequested && _isDecoding)
                {
                    int readRet = -1;

                    // 2. 讀取封包 (僅在讀取時鎖定)
                    lock (_lock)
                    {
                        if (_formatContext != null)
                            readRet = ffmpeg.av_read_frame(_formatContext, packet);
                    }

                    if (readRet >= 0)
                    {
                        if (_audioDecoders.TryGetValue(packet->stream_index, out var ctxWrapper))
                        {
                            if (ffmpeg.avcodec_send_packet(ctxWrapper.Ptr, packet) >= 0)
                            {
                                while (ffmpeg.avcodec_receive_frame(ctxWrapper.Ptr, frame) >= 0)
                                {
                                    byte[] pcmData = null;

                                    // 3. 濾鏡處理 (僅針對 FFmpeg 指標操作進行鎖定)
                                    lock (_lock)
                                    {
                                        int idx = _audioStreamIndices.IndexOf(packet->stream_index);
                                        if (_filterReady && _srcContexts != null && _sinkContext != null && idx >= 0)
                                        {
                                            ffmpeg.av_buffersrc_add_frame_flags(_srcContexts[idx], frame, AV_BUFFERSRC_FLAG_KEEP_REF);

                                            if (ffmpeg.av_buffersink_get_frame(_sinkContext, filtFrame) >= 0)
                                            {
                                                pcmData = ExtractPcm(filtFrame);
                                                ffmpeg.av_frame_unref(filtFrame);
                                            }
                                        }
                                    }

                                    // 4. 關鍵優化：將 AddSamples 移出 lock，防止阻塞渲染執行緒
                                    if (pcmData != null && pcmData.Length > 0)
                                    {
                                        _waveProvider.AddSamples(pcmData, 0, pcmData.Length);
                                    }

                                    ffmpeg.av_frame_unref(frame);
                                }
                            }
                        }
                        ffmpeg.av_packet_unref(packet);
                    }
                    else if (readRet == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    // 5. 動態頻率控制：根據倍速調整睡眠時間
                    // 取得目前的播放倍速 (避免頻繁跨執行緒讀取，可考慮在 PlayerService 存一個 float _currentRate)
                    float currentRate = _mp.Rate;

                    // 緩衝區控制：高倍速下允許更多的預載 (2000ms)，一般速度維持 500ms
                    int bufferLimit = currentRate > 2.0f ? 2000 : 500;

                    if (_waveProvider.BufferedDuration.TotalMilliseconds > bufferLimit)
                    {
                        // 緩衝足夠時，根據倍速決定睡眠長度
                        // 倍速越高，睡眠越短，以免來不及填補消耗
                        int sleepMs = currentRate > 4.0f ? 5 : 15;
                        Thread.Sleep(sleepMs);
                    }
                    else
                    {
                        // 緩衝不足時，全力解碼，僅釋放 CPU 時間片
                        Thread.Sleep(0);
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
            }
        }
        private byte[] ExtractPcm(AVFrame* frame)
        {
            int channels = frame->ch_layout.nb_channels;
            int size = ffmpeg.av_samples_get_buffer_size(null, channels, frame->nb_samples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
            byte[] res = new byte[size];
            Marshal.Copy((IntPtr)frame->data[0], res, 0, size);
            return res;
        }

        public void Pause() { _mp.Pause(); _waveOut?.Pause(); }

        public void StopAudioBridge()
        {
            // A. 立即發出停止訊號，讓 DecodeLoop 知道該跳出了
            _isDecoding = false;
            _cts?.Cancel();

            // B. 給予背景執行緒極短的緩衝時間來感知停止訊號
            Thread.Sleep(50);

            // C. 進入鎖定區塊進行資源物理釋放
            lock (_lock)
            {
                _filterReady = false;

                // 停止 NAudio 輸出
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                _waveProvider?.ClearBuffer();

                // 釋放解碼器上下文
                foreach (var decoder in _audioDecoders.Values)
                {
                    var p = decoder.Ptr;
                    ffmpeg.avcodec_free_context(&p);
                }
                _audioDecoders.Clear();

                // 核心修正：釋放 FormatContext 並設為 null
                if (_formatContext != null)
                {
                    var p = _formatContext;
                    ffmpeg.avformat_close_input(&p);
                    _formatContext = null;
                }

                // 釋放濾鏡資源
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
            _currentMedia?.Dispose();
            _currentMedia = null;
            // 停止影像播放
            _mp?.Stop();
        }

        public void Dispose() { StopAudioBridge(); _vlc?.Dispose(); _mp?.Dispose(); }
    }
}