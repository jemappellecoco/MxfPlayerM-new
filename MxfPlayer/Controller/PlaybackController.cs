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

  
    
        public float CurrentRate { get; private set; } = 1.0f;
        public PlaybackController(PlayerService player, System.Windows.Forms.Timer meterTimer, Action resetMeters)
        {
            _player = player;
            _meterTimer = meterTimer;
            _resetMeters = resetMeters;
        }

       
        public async Task MoveFirst()
        {
            _player.SeekVideoByFrame(0);
            _player.SeekAudioByFrame(0, 29.97);
            await Task.CompletedTask;
        }

        public async Task MoveFirst(double fps)
        {
            _player.SeekVideoByFrame(0);
            _player.SeekAudioByFrame(0, fps);
            await Task.CompletedTask;
        }

        public async Task MoveLast()
        {
            long length = _player.LengthMs;
            _player.SeekAudio(length);
            _player.Seek(length);
            await Task.CompletedTask;
        }

        public async Task MoveLast(double fps)
        {
            long length = _player.LengthMs;
            long frame = PlayerService.FrameFromTimeMs(length, fps);
            _player.SeekVideoByFrame(frame);
            _player.SeekAudioByFrame(frame, fps);
            await Task.CompletedTask;
        }

        public float MoveBackForward()
        {
            if (CurrentRate > 0) CurrentRate = -1.0f;
            else CurrentRate *= 2;
            if (CurrentRate < -16f) CurrentRate = -1.0f;

            _player.SetVideoRate(CurrentRate);
            _meterTimer.Start();

            return CurrentRate;
        }

        public void NegativeLog(double fps)
        {
            if (fps <= 0) fps = 29.97;
            long current = _player.CurrentTimeMs;
            long target = Math.Max(0, current - PlayerService.TimeMsFromFrame(1, fps));

            _player.Seek(target);
            _player.SeekAudioByFrame(PlayerService.FrameFromTimeMs(target, fps), fps);
            // 逐幀後退通常建議暫停音訊
            _player.Pause();
        }
        public async Task Play()
        {
            _player.SetVideoRate(CurrentRate);
            _player.ResumeAudio();

            _meterTimer.Start();

            await Task.CompletedTask;
        }

        public void Pause()
        {
            _player.Pause();
            _meterTimer.Stop();
            _resetMeters?.Invoke();
        }

        public void SeekByTimelineValue(int value, int maxValue)
        {
            SeekByTimelineValue(value, maxValue, 29.97);
        }

        public void SeekByTimelineValue(int value, int maxValue, double fps)
        {
            if (maxValue <= 0) return;

            long length = _player.LengthMs;
            if (length <= 0) return;

            long totalFrames = PlayerService.FrameFromTimeMs(length, fps);
            long targetFrame = value * totalFrames / maxValue;
            long target = PlayerService.TimeMsFromFrame(targetFrame, fps);

            if (target < 0) target = 0;
            if (target > length) target = length;

            _player.Seek(target);
            _player.SeekAudioByFrame(targetFrame, fps);
        }

        public async Task Jump(int seconds)
        {
            await Jump(seconds, 29.97);
        }

        public async Task Jump(int seconds, double fps)
        {
            long length = _player.LengthMs;
            long currentFrame = PlayerService.FrameFromTimeMs(_player.CurrentTimeMs, fps);
            long jumpFrames = (long)Math.Round(seconds * fps);
            long targetFrame = Math.Max(0, currentFrame + jumpFrames);
            long totalFrames = PlayerService.FrameFromTimeMs(length, fps);

            if (length > 0 && targetFrame > totalFrames)
                targetFrame = totalFrames;

            long target = PlayerService.TimeMsFromFrame(targetFrame, fps);

            _player.Seek(target);
            _player.SeekAudioByFrame(targetFrame, fps);

            await Task.CompletedTask;
        }

        public float MoveFastForward()
        {
            if (CurrentRate < 0) CurrentRate = 1.0f;
            else CurrentRate *= 2;
            if (CurrentRate > 16.0f) CurrentRate = 1.0f;

            _player.SetVideoRate(CurrentRate);
            return CurrentRate;
        }

        public void ResetRate()
        {
            CurrentRate = 1.0f;
            _player.SetVideoRate(1.0f);
            _player.SetAudioRate(1.0f);
        }

        // 逐幀前進 (Positive Log)
        public async Task PositiveLog()
        {
            _player.Seek(PlayerService.TimeMsFromFrame(PlayerService.FrameFromTimeMs(_player.CurrentTimeMs, 29.97) + 1, 29.97));
            _player.Pause();
        }

        public long GetCurrentTime() => _player.CurrentTimeMs;
        public long GetLength() => _player.LengthMs;
        public int GetTimelineValue(int maxValue) => (int)(GetLength() > 0 ? GetCurrentTime() * maxValue / GetLength() : 0);
    }
}
