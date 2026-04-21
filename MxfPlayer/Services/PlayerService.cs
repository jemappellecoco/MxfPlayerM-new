using LibVLCSharp.Shared;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxfPlayer.Services
{
    // ⭐ 修正：類別不標註 unsafe，只在特定方法標註
    public class PlayerService
    {
        private readonly string _ffmpegPath = @"C:\ffmpeg-7.1.1-essentials_build\bin\ffmpeg.exe";
        private readonly LibVLC _vlc;
        private readonly MediaPlayer _mp;

        // 全域音訊快取：Key = 檔案路徑, Value = 原始 PCM 資料
        private static readonly Dictionary<string, byte[]> _globalAudioCache = new();

        private string? _currentPath;
        private byte[]? _activeAudioData;
        private int _currentAudioCount = 8;

        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _audioBuffer;
        private CancellationTokenSource? _feedCts;

        // 聲道遮罩 (與 UI CheckBox 連動)
        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };

        public MediaPlayer MediaPlayer => _mp;
        public string? CurrentPath => _currentPath;
        public int CurrentAudioCount => _currentAudioCount;

        public PlayerService()
        {
            // 初始化 LibVLC
            _vlc = new LibVLC();
            _mp = new MediaPlayer(_vlc);
            _mp.Mute = true; // VLC 本身靜音，由我們控制的 NAudio 輸出
        }

        /// <summary>
        /// 啟動影音橋接：包含快取檢查與 FFmpeg 預載入
        /// </summary>
        public async Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0)
        {
            StopAudioBridge();
            _currentPath = path;
            _currentAudioCount = audioCount;

            // 1. 初始化 NAudio 輸出 (立體聲 48kHz)
            var outFormat = new WaveFormat(48000, 16, 2);
            _audioBuffer = new BufferedWaveProvider(outFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true
            };

            _waveOut = new WaveOutEvent { DesiredLatency = 100 };
            _waveOut.Init(_audioBuffer);

            // 2. 獲取音訊資料 (快取或解析)
            if (_globalAudioCache.ContainsKey(path))
            {
                Debug.WriteLine($"[Audio] 命中快取: {Path.GetFileName(path)}");
                _activeAudioData = _globalAudioCache[path];
            }
            else
            {
                Debug.WriteLine($"[Audio] 解析中: {Path.GetFileName(path)}");
                _activeAudioData = await Task.Run(() => PreloadAllAudio(path, audioCount));
                if (_activeAudioData != null)
                {
                    _globalAudioCache[path] = _activeAudioData;
                }
            }

            if (_activeAudioData == null)
            {
                Debug.WriteLine("[Audio] 錯誤：無法獲取音訊資料");
                return;
            }

            // 3. 啟動 VLC 影像
            using var media = new Media(_vlc, path, FromType.FromPath);
            _mp.Play(media);
            if (startTimeMs > 0) _mp.Time = startTimeMs;

            _waveOut.Play();

            // 4. 啟動音訊資料饋送任務 (Feed Loop)
            _feedCts = new CancellationTokenSource();
            _ = Task.Run(() => AudioFeedLoop(_feedCts.Token));
        }

        private byte[]? PreloadAllAudio(string path, int audioCount)
        {
            // 動態生成 FFmpeg 合併 8 聲道指令
            string filterInputs = string.Concat(Enumerable.Range(0, audioCount).Select(i => $"[0:a:{i}]"));
            string args = $"-i \"{path}\" -filter_complex \"{filterInputs}amerge=inputs={audioCount}\" -f s16le -ar 48000 -ac {audioCount} -vn pipe:1";

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return null;

                using var ms = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(ms); // 一次性解析至記憶體
                process.WaitForExit();

                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpeg Error] {ex.Message}");
                return null;
            }
        }

        private async Task AudioFeedLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 若未播放或緩衝區已滿，則稍後再檢查
                if (_audioBuffer == null || _activeAudioData == null || !_mp.IsPlaying)
                {
                    await Task.Delay(30, token);
                    continue;
                }

                // 維持緩衝區在 1 秒左右的儲備量
                if (_audioBuffer.BufferedDuration.TotalMilliseconds < 1000)
                {
                    PushAudioChunk();
                }

                await Task.Delay(30, token);
            }
        }

        private void PushAudioChunk()
        {
            if (_activeAudioData == null || _audioBuffer == null) return;

            // 核心同步邏輯：根據 VLC 的播放毫秒計算記憶體中的 byte 偏移量
            long vlcTimeMs = _mp.Time;
            // 計算每毫秒消耗的 byte 數 (48000Hz * 聲道數 * 2 bytes) / 1000ms
            long bytesPerMs = (48000 * _currentAudioCount * 2) / 1000;

            // 計算目前緩衝區末尾應該從記憶體哪個位址開始抓資料
            long startPos = (vlcTimeMs + (long)_audioBuffer.BufferedDuration.TotalMilliseconds) * bytesPerMs;

            // 每次提取 500 毫秒的長度進行混音
            int chunkMs = 500;
            int lengthToRead = (int)(chunkMs * bytesPerMs);

            // 邊界校正
            if (startPos < 0) startPos = 0;
            if (startPos + lengthToRead > _activeAudioData.Length)
            {
                lengthToRead = (int)(_activeAudioData.Length - startPos);
            }

            if (lengthToRead <= 0) return;

            // 提取資料段
            byte[] chunk = new byte[lengthToRead];
            Array.Copy(_activeAudioData, startPos, chunk, 0, lengthToRead);

            // 進入混音處理 (需 unsafe)
            ProcessFfmpegData(chunk, lengthToRead, _currentAudioCount);
        }

        /// <summary>
        /// 指標混音處理：將 8 聲道依遮罩混合為 2 聲道
        /// </summary>
        private unsafe void ProcessFfmpegData(byte[] buffer, int length, int audioCount)
        {
            if (_audioBuffer == null) return;

            int frames = length / (audioCount * 2);
            if (frames <= 0) return;

            short[] outSamples = new short[frames * 2];

            fixed (byte* pRaw = buffer)
            {
                short* inPtr = (short*)pRaw;
                for (int i = 0; i < frames; i++)
                {
                    int baseIdx = i * audioCount;
                    long mixed = 0;
                    int active = 0;

                    for (int ch = 0; ch < Math.Min(8, audioCount); ch++)
                    {
                        if (ChannelMask[ch])
                        {
                            mixed += inPtr[baseIdx + ch];
                            active++;
                        }
                    }

                    short final = (active > 0) ? (short)Math.Clamp(mixed / active, short.MinValue, short.MaxValue) : (short)0;
                    outSamples[i * 2] = final;     // L
                    outSamples[i * 2 + 1] = final; // R
                }
            }

            byte[] outBytes = new byte[outSamples.Length * 2];
            Buffer.BlockCopy(outSamples, 0, outBytes, 0, outBytes.Length);
            _audioBuffer.AddSamples(outBytes, 0, outBytes.Length);
        }

        public void Pause()
        {
            _mp.Pause();
            _waveOut?.Pause();
        }

        public void StopAudioBridge()
        {
            _feedCts?.Cancel();
            _mp.Stop();
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _audioBuffer = null;
        }

        public void ClearCache()
        {
            _globalAudioCache.Clear();
        }
    }
}