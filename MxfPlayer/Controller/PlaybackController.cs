using System;
using System.Threading.Tasks; // 必須引用
using System.Windows.Forms;
using MxfPlayer.Services;

namespace MxfPlayer.Controllers
{
    public class PlaybackController
    {
        private readonly PlayerService _player;
        private readonly System.Windows.Forms.Timer _meterTimer;
        private readonly Action _resetMeters;

        private readonly float[] _forwardRates = { 1f, 2f, 4f, 8f, 16f };
        private readonly float[] _reverseRates = { -1f, -2f, -4f, -8f, -16f };

        private int _forwardIndex = 0;
        private int _reverseIndex = 0;

        public PlaybackController(PlayerService player, System.Windows.Forms.Timer meterTimer, Action resetMeters)
        {
            _player = player;
            _meterTimer = meterTimer;
            _resetMeters = resetMeters;
        }

        private async Task SyncAudioToCurrentTime()
        {
            if (string.IsNullOrEmpty(_player.CurrentPath)) return;
            long currentTime = _player.MediaPlayer.Time;
            // 等待 PlayerService 完成解析與初始化
            await _player.StartAudioBridge(_player.CurrentPath, _player.CurrentAudioCount, currentTime);
        }

        // ⭐ 修正：將 void 改為 Task，讓 MainForm 可以 await 它
        public async Task Play()
        {
            if (_player.MediaPlayer == null) return;
            _player.MediaPlayer.Play();
            ResetRate();
            _meterTimer.Start();
            if (_player.MediaPlayer.Time > 0) await SyncAudioToCurrentTime();
        }

        public void Pause()
        {
            _player.Pause();
            _meterTimer.Stop();
            _resetMeters?.Invoke();
        }

        // ⭐ 修正：回傳 Task
        public async Task MoveFirst()
        {
            if (_player.MediaPlayer == null) return;
            _player.MediaPlayer.Time = 0;
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        // ⭐ 修正：回傳 Task
        public async Task MoveLast()
        {
            if (_player.MediaPlayer == null) return;
            long length = _player.MediaPlayer.Length;
            if (length <= 0) return;

            _player.MediaPlayer.Time = Math.Max(0, length - 1000);
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        // ⭐ 修正：回傳 Task
        public async Task PositiveLog()
        {
            if (_player.MediaPlayer == null) return;
            _player.MediaPlayer.NextFrame();
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        // ⭐ 修正：因為呼叫了 async 的 JumpMilliseconds，所以這裡也要 async Task
        public async Task NegativeLog(double fps = 29.97)
        {
            if (_player.MediaPlayer == null) return;
            int frameMs = (int)Math.Round(1000.0 / fps);
            await JumpMilliseconds(-frameMs);
        }

        public float MoveFastForward()
        {
            if (_player.MediaPlayer == null) return 1f;
            if (!_player.MediaPlayer.IsPlaying) _player.MediaPlayer.Play();
            if (_forwardIndex < _forwardRates.Length - 1) _forwardIndex++;
            _reverseIndex = 0;
            float rate = _forwardRates[_forwardIndex];
            _player.MediaPlayer.SetRate(rate);
            return rate;
        }

        public float MoveBackForward()
        {
            if (_player.MediaPlayer == null) return -1f;
            if (!_player.MediaPlayer.IsPlaying) _player.MediaPlayer.Play();
            if (_reverseIndex < _reverseRates.Length - 1) _reverseIndex++;
            _forwardIndex = 0;
            float rate = _reverseRates[_reverseIndex];
            _player.MediaPlayer.SetRate(rate);
            return rate;
        }

        public async Task Jump(int seconds)
        {
            long current = _player.MediaPlayer.Time;
            _player.MediaPlayer.Time = current + (seconds * 1000L);
            await SyncAudioToCurrentTime();
        }

        // ⭐ 修正：回傳 Task
        private async Task JumpMilliseconds(long ms)
        {
            if (_player.MediaPlayer == null) return;
            long target = _player.MediaPlayer.Time + ms;
            if (target < 0) target = 0;
            long length = _player.MediaPlayer.Length;
            if (length > 0 && target > length) target = length;

            _player.MediaPlayer.Time = target;
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        private void ResetRate()
        {
            if (_player.MediaPlayer == null) return;
            _forwardIndex = 0;
            _reverseIndex = 0;
            _player.MediaPlayer.SetRate(1f);
        }

        public long GetCurrentTime() => _player.MediaPlayer?.Time ?? 0;
        public long GetLength() => _player.MediaPlayer?.Length ?? 0;

        public int GetTimelineValue(int maxValue)
        {
            if (_player.MediaPlayer == null) return 0;
            long length = _player.MediaPlayer.Length;
            if (length <= 0 || maxValue <= 0) return 0;
            return (int)(_player.MediaPlayer.Time * maxValue / length);
        }

        public async Task SeekByTimelineValue(int value, int maxValue)
        {
            if (_player.MediaPlayer == null || maxValue <= 0) return;
            long length = _player.MediaPlayer.Length;
            if (length <= 0) return;

            long target = (long)value * length / maxValue;
            _player.MediaPlayer.Time = target;

            await SyncAudioToCurrentTime();
        }
    }
}