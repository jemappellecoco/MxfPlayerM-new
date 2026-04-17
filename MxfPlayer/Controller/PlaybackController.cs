using System;
using MxfPlayer.Services;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace MxfPlayer.Controllers
{
    public class PlaybackController
    {
        private readonly PlayerService _player;
        private readonly WinFormsTimer _meterTimer;
        private readonly Action _resetMeters;
        private bool _isLooping;

        public PlaybackController(PlayerService player, WinFormsTimer meterTimer, Action resetMeters)
        {
            _player = player;
            _meterTimer = meterTimer;
            _resetMeters = resetMeters;
        }

        public bool IsLooping => _isLooping;

        public void Play()
        {
            _player.MediaPlayer.Play();
            _meterTimer.Start();
        }

        public void Pause()
        {
            _player.Pause();
            _meterTimer.Stop();
        }

        public void Stop()
        {
            _player.Stop();
            _meterTimer.Stop();
            _resetMeters();
        }

        public void Jump(int seconds)
        {
            var mediaPlayer = _player.MediaPlayer;
            if (mediaPlayer == null) return;

            long current = mediaPlayer.Time;
            long target = current + seconds * 1000L;

            if (target < 0)
                target = 0;

            long length = mediaPlayer.Length;
            if (length > 0 && target > length)
                target = length;

            mediaPlayer.Time = target;
        }

        public void RewindFast()
        {
            Jump(-30);
        }

        public void Rewind()
        {
            Jump(-10);
        }

        public void Forward()
        {
            Jump(10);
        }

        public bool ToggleLoop()
        {
            _isLooping = !_isLooping;
            return _isLooping;
        }
    }
}