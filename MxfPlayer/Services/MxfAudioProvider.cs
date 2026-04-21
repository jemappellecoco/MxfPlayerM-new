using NAudio.Wave;
using System;

namespace MxfPlayer.Services
{
    public class MxfAudioProvider : IWaveProvider
    {
        private readonly byte[] _data;
        private readonly int _channels;
        private long _position = 0;
        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8];

        public MxfAudioProvider(byte[] data, int channels)
        {
            _data = data;
            _channels = channels;
            WaveFormat = new WaveFormat(48000, 16, 2); // 輸出立體聲
        }

        public void Seek(long timeMs)
        {
            long bytesPerMs = (48000 * _channels * 2) / 1000;
            _position = timeMs * bytesPerMs;
            // 確保對齊 Frame (每個 Frame 大小為 _channels * 2 bytes)
            _position = (_position / (_channels * 2)) * (_channels * 2);
        }

        public unsafe int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerFrameIn = _channels * 2;
            int bytesPerFrameOut = 4; // 2 channels * 16bit
            int framesRequested = count / bytesPerFrameOut;
            int bytesRead = 0;

            fixed (byte* pData = _data, pBuf = buffer)
            {
                short* outPtr = (short*)(pBuf + offset);

                for (int i = 0; i < framesRequested; i++)
                {
                    if (_position + bytesPerFrameIn > _data.Length) break;

                    short* inPtr = (short*)(pData + _position);
                    long mixed = 0;
                    int active = 0;

                    for (int ch = 0; ch < Math.Min(8, _channels); ch++)
                    {
                        if (Mask[ch]) { mixed += inPtr[ch]; active++; }
                    }

                    short final = (active > 0) ? (short)Math.Clamp(mixed / active, short.MinValue, short.MaxValue) : (short)0;

                    outPtr[i * 2] = final;     // L
                    outPtr[i * 2 + 1] = final; // R

                    _position += bytesPerFrameIn;
                    bytesRead += bytesPerFrameOut;
                }
            }
            return bytesRead;
        }
    }
}