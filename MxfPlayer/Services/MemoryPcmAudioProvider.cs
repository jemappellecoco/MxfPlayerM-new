using System;
using NAudio.Wave;

namespace MxfPlayer.Services
{
    public class MemoryPcmAudioProvider : IWaveProvider
    {
        private readonly byte[] _pcmData;
        private readonly int _channels;
        private readonly int _sampleRate;
        private long _position;
        private float _playbackRate = 1.0f;

        public WaveFormat WaveFormat { get; }

        public float PlaybackRate
        {
            get => _playbackRate;
            set => _playbackRate = Math.Abs(value) < 0.001f ? 0 : value;
        }

        public MemoryPcmAudioProvider(byte[] pcmData, int channels, int sampleRate)
        {
            _pcmData = pcmData;
            _channels = Math.Max(1, channels);
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            WaveFormat = new WaveFormat(_sampleRate, 16, 2);
        }

        public void SeekFrame(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;

            long sampleIndex = (long)Math.Round(frameIndex * _sampleRate / fps);
            long pos = sampleIndex * _channels * 2;
            pos = (pos / (_channels * 2)) * (_channels * 2);
            _position = Math.Min(pos, _pcmData.Length);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (Math.Abs(_playbackRate) < 0.001f)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int bytesPerFrameIn = _channels * 2;
            int framesRequested = count / 4;
            int framesWritten = 0;
            if (framesRequested <= 0) return 0;
            long startFrame = _position / bytesPerFrameIn;
            float rate = Math.Abs(_playbackRate);

            for (int i = 0; i < framesRequested; i++)
            {
                long sourceFrame = _playbackRate < 0
                    ? startFrame - (long)Math.Round(i * rate)
                    : startFrame + (long)Math.Round(i * rate);
                long sourcePos = sourceFrame * bytesPerFrameIn;

                if (sourceFrame < 0)
                    break;

                if (sourcePos + bytesPerFrameIn > _pcmData.Length)
                    break;

                short left;
                short right;

                if (_channels == 1)
                {
                    left = BitConverter.ToInt16(_pcmData, (int)sourcePos);
                    right = left;
                }
                else
                {
                    left = BitConverter.ToInt16(_pcmData, (int)sourcePos);
                    right = BitConverter.ToInt16(_pcmData, (int)sourcePos + 2);
                }

                int outPos = offset + (i * 4);
                buffer[outPos] = (byte)(left & 0xff);
                buffer[outPos + 1] = (byte)((left >> 8) & 0xff);
                buffer[outPos + 2] = (byte)(right & 0xff);
                buffer[outPos + 3] = (byte)((right >> 8) & 0xff);
                framesWritten++;
            }

            _position = Math.Min(
                _playbackRate < 0
                    ? Math.Max(0, _position - (long)Math.Round(framesWritten * rate) * bytesPerFrameIn)
                    : _position + (long)Math.Round(framesWritten * rate) * bytesPerFrameIn,
                _pcmData.Length);

            int bytesWritten = framesWritten * 4;
            if (bytesWritten < count)
                Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);

            return count;
        }
    }
}
