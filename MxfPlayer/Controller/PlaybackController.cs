using System;
using System.Threading.Tasks;
using MxfPlayer.Services;

namespace MxfPlayer.Controllers
{
    public class PlaybackController
    {
        private readonly PlayerService _player;
        private readonly System.Windows.Forms.Timer _meterTimer;
        private readonly Action _resetMeters;

        // 增加一個變數紀錄當前倍速，避免 Seek 後倍速跑掉
        public float _currentRate = 1.0f;
        public float CurrentRate { get; private set; } = 1.0f;
        public PlaybackController(PlayerService player, System.Windows.Forms.Timer meterTimer, Action resetMeters)
        {
            _player = player;
            _meterTimer = meterTimer;
            _resetMeters = resetMeters;
        }

        /// <summary>
        /// 核心同步：確保 FFmpeg 解碼器跟上 VLC 的時間點與倍速
        /// </summary>
        private async Task SyncAudio(long timeMs)
        {
            if (string.IsNullOrEmpty(_player.CurrentPath)) return;

            // 呼叫 Service 的 StartAudioBridge
            // 該方法內部已經包含了 Stop 舊連結、設定 VLC Rate、啟動 FFmpeg 濾鏡的邏輯
            await _player.StartAudioBridge(_player.CurrentPath, _player.CurrentAudioCount, timeMs, _currentRate);
        }
        public async Task MoveFirst()
        {
            _player.MediaPlayer.Time = 0;
            await SyncAudio(0);
        }

        public async Task MoveLast()
        {
            long length = _player.MediaPlayer.Length;
            _player.MediaPlayer.Time = length;
            await SyncAudio(length);
        }

        public float MoveBackForward()
        {
            // 倒帶邏輯：-1x -> -2x -> -4x ... -> -16x -> -1x
            if (_currentRate > 0) _currentRate = -1.0f;
            else _currentRate *= 2;

            if (_currentRate < -16f) _currentRate = -1.0f;

            _ = SyncAudio(_player.MediaPlayer.Time);
            return _currentRate;
        }

        public void NegativeLog(double fps)
        {
            if (fps <= 0) fps = 29.97;
            long current = _player.MediaPlayer.Time;
            long frameMs = (long)(1000.0 / fps);
            long target = Math.Max(0, current - frameMs);

            _player.MediaPlayer.Time = target;
            // 逐幀後退通常建議暫停音訊
            _player.Pause();
        }
        public async Task Play()
        {
            if (_player.MediaPlayer == null) return;

            // 先啟動影像
            _player.MediaPlayer.Play();
            _player.MediaPlayer.SetRate(_currentRate);

            _meterTimer.Start();

            // 同步音訊橋接
            await SyncAudio(_player.MediaPlayer.Time);
        }

        public void Pause()
        {
            _player.Pause();
            _meterTimer.Stop();
            _resetMeters?.Invoke();
        }

        public async Task SeekByTimelineValue(int value, int maxValue)
        {
            if (_player.MediaPlayer == null || maxValue <= 0) return;
            long length = _player.MediaPlayer.Length;
            if (length <= 0) return;

            long target = (long)value * length / maxValue;

            // 1. 先讓 VLC 影像跳過去 (影像響應最重要)
            _player.MediaPlayer.Time = target;

            // 2. 如果正在播放中，則同步音訊
            if (_player.MediaPlayer.IsPlaying)
            {
                await SyncAudio(target);
            }
            else
            {
                // 如果是暫停狀態下 Seek，只需確保下一次 Play 時從正確位置開始
                // 這裡可以選擇不啟動音訊橋接以節省效能
            }
        }

        public async Task Jump(int seconds)
        {
            if (_player.MediaPlayer == null) return;

            long target = _player.MediaPlayer.Time + (seconds * 1000L);
            if (target < 0) target = 0;
            if (target > _player.MediaPlayer.Length) target = _player.MediaPlayer.Length;

            _player.MediaPlayer.Time = target;

            if (_player.MediaPlayer.IsPlaying)
            {
                await SyncAudio(target);
            }
        }

        public float MoveFastForward()
        {
            // 倍速循環：1 -> 2 -> 4 -> 8 -> 16 -> 1
            _currentRate = _currentRate * 2;
            if (_currentRate > 16f) _currentRate = 1f;

            // 立即更新影像與音訊橋接（含 atempo 濾鏡更新）
            _ = SyncAudio(_player.MediaPlayer.Time);

            return _currentRate;
        }

        public void ResetRate()
        {
            _currentRate = 1.0f;
            if (_player.MediaPlayer.IsPlaying)
            {
                _ = SyncAudio(_player.MediaPlayer.Time);
            }
            else
            {
                _player.MediaPlayer.SetRate(1.0f);
            }
        }

        // 逐幀前進 (Positive Log)
        public async Task PositiveLog()
        {
            if (_player.MediaPlayer == null) return;

            // 逐幀時通常不需要音訊橋接持續運作
            // 但為了精準，我們讓 VLC 走一幀，然後暫停音訊橋接
            _player.MediaPlayer.NextFrame();

            // 如果你希望逐幀時也能聽到「吱」一聲的短促音訊：
            // await SyncAudio(_player.MediaPlayer.Time);
            // 但通常建議逐幀時 Pause 音訊以防緩衝堆積
            _player.Pause();
        }

        public long GetCurrentTime() => _player.MediaPlayer?.Time ?? 0;
        public long GetLength() => _player.MediaPlayer?.Length ?? 0;
        public int GetTimelineValue(int maxValue) => (int)(GetLength() > 0 ? GetCurrentTime() * maxValue / GetLength() : 0);
    }
}