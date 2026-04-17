using LibVLCSharp.Shared;

namespace MxfPlayer.Services
{
    public class PlayerService
    {
        private LibVLC _vlc;
        private MediaPlayer _mp;

        public MediaPlayer MediaPlayer => _mp;

        public PlayerService()
        {
            _vlc = new LibVLC();
            _mp = new MediaPlayer(_vlc);
        }

        public void Play(string path)
        {
            var media = new Media(_vlc, path, FromType.FromPath);
            _mp.Play(media);
        }

        public void Pause() => _mp.Pause();
        public void Stop() => _mp.Stop();
    }
}