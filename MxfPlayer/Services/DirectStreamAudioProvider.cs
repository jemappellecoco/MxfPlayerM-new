using NAudio.Wave;
using System;
using System.IO;

namespace MxfPlayer.Services
{
    public class DirectStreamAudioProvider : IWaveProvider, IDisposable
    {
        private readonly Stream _inputStream;
        public WaveFormat WaveFormat { get; private set; }
        public bool[] Mask { get; set; } = new bool[8] { true, true, true, true, true, true, true, true };

        public DirectStreamAudioProvider(Stream inputStream, int channels)
        {
            _inputStream = inputStream;
            // FFmpeg 輸出的是 s16le (16-bit PCM)，不是 Float
            WaveFormat = new WaveFormat(48000, 16, channels);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            // 直接從 FFmpeg 的 Pipe 讀取原始 byte
            int bytesRead = _inputStream.Read(buffer, offset, count);

            // 實作聲道遮罩 (若有勾選才保留聲音，沒勾選則填 0)
            if (bytesRead > 0 && Mask != null)
            {
                ApplyMask(buffer, offset, bytesRead);
            }

            return bytesRead;
        }

        private void ApplyMask(byte[] buffer, int offset, int length)
        {
            int channels = WaveFormat.Channels;
            int sampleSize = 2; // 16-bit = 2 bytes

            for (int i = 0; i < length; i += channels * sampleSize)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (ch < Mask.Length && !Mask[ch])
                    {
                        int pos = offset + i + (ch * sampleSize);
                        if (pos + 1 < buffer.Length)
                        {
                            buffer[pos] = 0;
                            buffer[pos + 1] = 0;
                        }
                    }
                }
            }
        }

        public void Dispose() => _inputStream?.Dispose();
    }
}