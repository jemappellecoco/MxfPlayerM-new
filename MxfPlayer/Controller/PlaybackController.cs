using System;
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

            // 這裡加上 await，確保 Start 邏輯執行完畢
            await _player.StartAudioBridge(_player.CurrentPath, _player.CurrentAudioCount, currentTime);
        }

        public async void Play()
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

        public async void MoveFirst()
        {
            if (_player.MediaPlayer == null) return;
            _player.MediaPlayer.Time = 0;
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        public async void MoveLast()
        {
            if (_player.MediaPlayer == null) return;
            long length = _player.MediaPlayer.Length;
            if (length <= 0) return;

            _player.MediaPlayer.Time = Math.Max(0, length - 1000);
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        public async void PositiveLog()
        {
            if (_player.MediaPlayer == null) return;
            _player.MediaPlayer.NextFrame();
            ResetRate();
            await SyncAudioToCurrentTime();
        }

        public void NegativeLog(double fps = 29.97)
        {
            if (_player.MediaPlayer == null) return;
            int frameMs = (int)Math.Round(1000.0 / fps);
            JumpMilliseconds(-frameMs);
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

        public void Jump(int seconds)
        {
            JumpMilliseconds(seconds * 1000L);
        }

        private async void JumpMilliseconds(long ms)
        {
            if (_player.MediaPlayer == null) return;
            long target = _player.MediaPlayer.Time + ms;
            if (target < 0) target = 0;
            long length = _player.MediaPlayer.Length;
            if (length > 0 && target > length) target = length;

            _player.MediaPlayer.Time = target;
            ResetRate();
            await  SyncAudioToCurrentTime();
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

        public async void SeekByTimelineValue(int value, int maxValue)
        {
            if (_player.MediaPlayer == null || maxValue <= 0) return;
            long length = _player.MediaPlayer.Length;
            if (length <= 0) return;

            long target = (long)value * length / maxValue;
            _player.MediaPlayer.Time = target;

            ResetRate();
            await SyncAudioToCurrentTime(); // 拖動後重啟音訊對齊
        }
    }
}