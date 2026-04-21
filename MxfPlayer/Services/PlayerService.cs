using LibVLCSharp.Shared;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MxfPlayer.Services
{
    public class PlayerService
    {
        private readonly string _ffmpegPath = @"C:\ffmpeg-7.1.1-essentials_build\bin\ffmpeg.exe";
        private readonly LibVLC _vlc;
        private readonly MediaPlayer _mp;
        private static readonly Dictionary<string, byte[]> _globalAudioCache = new();

        // ⭐ 改動 1：新增進程變數，以便隨時中斷
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

        public async Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0)
        {
            // 這裡會先執行 Stop，殺掉上一個還在解析的 FFmpeg
            StopAudioBridge();

            CurrentPath = path;
            CurrentAudioCount = audioCount;

            byte[]? audioData = null;
            if (_globalAudioCache.ContainsKey(path))
            {
                audioData = _globalAudioCache[path];
            }
            else
            {
                try
                {
                    // 執行解析
                    audioData = await Task.Run(() => PreloadAllAudio(path, audioCount));
                    if (audioData != null) _globalAudioCache[path] = audioData;
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[Audio] 解析任務被取消");
                    return;
                }
            }

            if (audioData == null) return;

            _currentProvider = new MxfAudioProvider(audioData, audioCount) { Mask = this.ChannelMask };
            _currentProvider.Seek(startTimeMs);

            _waveOut = new WaveOutEvent { DesiredLatency = 100 };
            _waveOut.Init(_currentProvider);

            using var media = new Media(_vlc, path, FromType.FromPath);
            _mp.Play(media);
            _mp.Time = startTimeMs;
            _waveOut.Play();
        }

        private byte[]? PreloadAllAudio(string path, int audioCount)
        {
            string filter = string.Concat(Enumerable.Range(0, audioCount).Select(i => $"[0:a:{i}]"));
            string args = $"-i \"{path}\" -filter_complex \"{filter}amerge=inputs={audioCount}\" -f s16le -ar 48000 -ac {audioCount} -vn pipe:1";

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            // ⭐ 修正點 1：先使用區域變數 localProcess
            Process? localProcess = null;

            try
            {
                localProcess = Process.Start(psi);
                if (localProcess == null) return null;

                // 將區域變數賦值給全域變數，以便 StopAudioBridge 可以 Kill 它
                _ffmpegProcess = localProcess;

                using var ms = new MemoryStream();
                // 讀取資料
                localProcess.StandardOutput.BaseStream.CopyTo(ms);

                // ⭐ 修正點 2：檢查時使用 localProcess 而非全域的 _ffmpegProcess
                // 這樣即便 _ffmpegProcess 被外部設為 null，這裡也不會報錯
                if (localProcess.HasExited && localProcess.ExitCode != 0)
                {
                    // 如果是被手動 Kill 的 (ExitCode 通常是 -1)，會進到這裡
                    return null;
                }

                localProcess.WaitForExit();
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpeg Exception] {ex.Message}");
                return null;
            }
            finally
            {
                // ⭐ 修正點 3：結束後，只有當前的進程確實是 localProcess 時才清空全域變數
                // 防止新啟動的進程被舊的任務清掉
                if (_ffmpegProcess == localProcess)
                {
                    _ffmpegProcess = null;
                }

                // 確保釋放 localProcess 資源
                localProcess?.Dispose();
            }
        }
        public void Pause()
        {
            _mp.Pause();
            _waveOut?.Pause();
        }

        public void StopAudioBridge()
        {
            // ⭐ 改動 3：切換檔案時，強制殺掉正在解析中的 FFmpeg
            // 這是讓 Loading 視窗能正常關閉的關鍵
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.Kill();
                    _ffmpegProcess.Dispose();
                }
                catch { }
                _ffmpegProcess = null;
            }

            _mp.Stop();
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
        }
    }
}