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
    public class PlayerService
    {
        private readonly string _ffmpegPath = @"C:\ffmpeg-7.1.1-essentials_build\bin\ffmpeg.exe";
        private readonly LibVLC _vlc;
        private readonly MediaPlayer _mp;

        // 快取目前正在播放的暫存檔路徑
        private static readonly Dictionary<string, string> _globalAudioCache = new();

        private Process? _ffmpegProcess;
        private WaveOutEvent? _waveOut;
        private MxfAudioProvider? _currentProvider;

        public bool[] ChannelMask = new bool[8] { true, true, true, true, true, true, true, true };
        public MediaPlayer MediaPlayer => _mp;
        public string? CurrentPath { get; private set; }
        public int CurrentAudioCount { get; private set; }

        public PlayerService()
        {
            _vlc = new LibVLC();
            _mp = new MediaPlayer(_vlc);
            _mp.Mute = true;
        }

        // ⭐ 修正：這是唯一的 StartAudioBridge 定義
        public async Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0)
        {
            bool needNewTranscode = true;

            // 檢查是否為同一個檔案且時間點已被目前的快取覆蓋
            if (CurrentPath == path && _currentProvider != null && _currentProvider.IsDataAvailable(startTimeMs))
            {
                needNewTranscode = false;
            }

            if (needNewTranscode)
            {
                StopAudioBridge(); // 停止舊任務，釋放檔案
                CurrentPath = path;
                CurrentAudioCount = audioCount;

                // 執行隨機點解析
                string? pcmPath = await Task.Run(() => PreloadFromPoint(path, audioCount, startTimeMs));

                if (string.IsNullOrEmpty(pcmPath)) return;

                // ⭐ 修正點：傳入三個參數 (路徑, 聲道數, 起始偏移時間)
                _currentProvider = new MxfAudioProvider(pcmPath, audioCount, startTimeMs) { Mask = this.ChannelMask };
            }

            // 對齊音軌位置
            _currentProvider?.Seek(startTimeMs);

            if (_waveOut == null)
            {
                _waveOut = new WaveOutEvent { DesiredLatency = 100 };
                _waveOut.Init(_currentProvider);
            }

            _waveOut.Play();

            // 啟動影片
            using var media = new Media(_vlc, path, FromType.FromPath);
            _mp.Play(media);
            _mp.Time = startTimeMs;
        }

        private string? PreloadFromPoint(string path, int audioCount, long startTimeMs)
        {
            double startSec = startTimeMs / 1000.0;
            string filter = string.Concat(Enumerable.Range(0, audioCount).Select(i => $"[0:a:{i}]"));
            // 使用極速跳轉參數 -ss 在 -i 之前
            string args = $"-ss {startSec:F3} -i \"{path}\" -filter_complex \"{filter}amerge=inputs={audioCount}\" -f s16le -ar 48000 -ac {audioCount} -vn pipe:1";

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"mxf_cache_{Guid.NewGuid()}.pcm");

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            Process? localProcess = Process.Start(psi);
            if (localProcess == null) return null;
            _ffmpegProcess = localProcess;

            // 背景寫入磁碟
            _ = Task.Run(() => {
                try
                {
                    using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    localProcess.StandardOutput.BaseStream.CopyTo(fs);
                }
                catch { }
            });

            // 等待初步資料產出
            int retry = 0;
            while (retry < 20)
            {
                if (File.Exists(tempFilePath) && new FileInfo(tempFilePath).Length > 4096) break;
                Thread.Sleep(50);
                retry++;
            }

            return tempFilePath;
        }

        public void Pause()
        {
            _mp.Pause();
            _waveOut?.Pause();
        }

        public void StopAudioBridge()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(); } catch { }
                _ffmpegProcess = null;
            }

            _mp.Stop();
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            _currentProvider?.Dispose();
            _currentProvider = null;
        }

        public void ClearCache()
        {
            // 關閉程式時清理所有暫存
            _globalAudioCache.Clear();
        }
    }
}