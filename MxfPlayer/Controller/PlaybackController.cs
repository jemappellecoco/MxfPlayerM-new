using System;
using System.Windows.Forms;
using MxfPlayer.Services;

namespace MxfPlayer.Controllers
{
    public class PlaybackController
    {
        private readonly PlayerService _player;
        private readonly Timer _meterTimer;
        private readonly Action _resetMeters;

        public PlaybackController(PlayerService player, Timer meterTimer, Action resetMeters)
        {
            _player = player;
            _meterTimer = meterTimer;
            _resetMeters = resetMeters;
        }

        public void Play()
        {
            _player.MediaPlayer.Play();
            _meterTimer.Start();
        }

        public void Pause()
        {
            _player.Pause();
        }

        public void Stop()
        {
            _player.Stop();
            _meterTimer.Stop();
            _resetMeters();
        }
    }
}