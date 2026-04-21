using NAudio.Wave;
using System;
using System.IO;

namespace MxfPlayer.Services
{
    public class MxfAudioProvider : IWaveProvider, IDisposable
    {
        private readonly FileStream _fileStream;
        private readonly int _channels;
        private readonly long _baseTimeMs; // 此快取檔的起始影片毫秒

        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8];

        // ⭐ 修正點：建構子必須包含 baseTimeMs
        public MxfAudioProvider(string pcmPath, int channels, long baseTimeMs)
        {
            _fileStream = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _channels = channels;
            _baseTimeMs = baseTimeMs;
            WaveFormat = new WaveFormat(48000, 16, 2);
        }

        public bool IsDataAvailable(long timeMs)
        {
            if (timeMs < _baseTimeMs) return false;
            long targetOffsetBytes = (48000 * _channels * 2 * (timeMs - _baseTimeMs)) / 1000;
            return _fileStream.Length >= targetOffsetBytes;
        }

        public void Seek(long timeMs)
        {
            long relativeMs = timeMs - _baseTimeMs;
            if (relativeMs < 0) relativeMs = 0;

            long pos = (48000 * _channels * 2 * relativeMs) / 1000;
            pos = (pos / (_channels * 2)) * (_channels * 2);

            _fileStream.Position = Math.Min(pos, _fileStream.Length);
        }

        public unsafe int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerFrameIn = _channels * 2;
            int framesRequested = count / 4;

            byte[] rawBuffer = new byte[framesRequested * bytesPerFrameIn];
            int bytesRead = _fileStream.Read(rawBuffer, 0, rawBuffer.Length);
            int framesRead = bytesRead / bytesPerFrameIn;

            if (framesRead == 0) return 0;

            fixed (byte* pRaw = rawBuffer, pBuf = buffer)
            {
                short* outPtr = (short*)(pBuf + offset);
                for (int i = 0; i < framesRead; i++)
                {
                    short* inPtr = (short*)(pRaw + (i * bytesPerFrameIn));
                    long mixed = 0;
                    int active = 0;

                    for (int ch = 0; ch < Math.Min(8, _channels); ch++)
                    {
                        if (Mask[ch]) { mixed += inPtr[ch]; active++; }
                    }

                    short final = (active > 0) ? (short)Math.Clamp(mixed / active, short.MinValue, short.MaxValue) : (short)0;
                    outPtr[i * 2] = final;
                    outPtr[i * 2 + 1] = final;
                }
            }
            return framesRead * 4;
        }

        public void Dispose()
        {
            _fileStream?.Close();
            _fileStream?.Dispose();
        }
    }
}