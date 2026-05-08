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
        private float _playbackRate = 1.0f;

        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8] { true, true, true, true, true, true, true, true };
        public float PlaybackRate
        {
            get => _playbackRate;
            set => _playbackRate = Math.Abs(value) < 0.001f ? 0 : value;
        }

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

        public bool IsFrameDataAvailable(long frameIndex, double fps)
        {
            return IsFrameDataAvailable(frameIndex, fps, 250);
        }

        public bool IsFrameDataAvailable(long frameIndex, double fps, int requiredAheadMs)
        {
            if (fps <= 0) fps = 29.97;

            long timeMs = (long)Math.Round(frameIndex * 1000.0 / fps);
            if (timeMs < _baseTimeMs) return false;

            long relativeMs = timeMs - _baseTimeMs;
            long targetOffsetBytes = (48000 * _channels * 2 * relativeMs) / 1000;
            long requiredAheadBytes = (48000 * _channels * 2 * requiredAheadMs) / 1000;
            return _fileStream.Length > targetOffsetBytes + requiredAheadBytes;
        }

        public bool IsReverseFrameDataAvailable(long frameIndex, double fps, int requiredBehindMs)
        {
            if (fps <= 0) fps = 29.97;

            long timeMs = (long)Math.Round(frameIndex * 1000.0 / fps);
            if (timeMs < _baseTimeMs) return false;

            long relativeMs = timeMs - _baseTimeMs;
            long targetOffsetBytes = (48000 * _channels * 2 * relativeMs) / 1000;
            long requiredBehindBytes = (48000 * _channels * 2 * requiredBehindMs) / 1000;
            return targetOffsetBytes >= requiredBehindBytes &&
                   _fileStream.Length > targetOffsetBytes + (_channels * 2);
        }

        public void Seek(long timeMs)
        {
            long relativeMs = timeMs - _baseTimeMs;
            if (relativeMs < 0) relativeMs = 0;

            long pos = (48000 * _channels * 2 * relativeMs) / 1000;
            pos = (pos / (_channels * 2)) * (_channels * 2);

            _fileStream.Position = pos;
        }

        public void SeekFrame(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;

            long sampleIndex = (long)Math.Round(frameIndex * 48000.0 / fps);
            long baseSampleIndex = (long)Math.Round(_baseTimeMs * 48000.0 / 1000.0);
            sampleIndex = Math.Max(0, sampleIndex - baseSampleIndex);
            long pos = sampleIndex * _channels * 2;
            pos = (pos / (_channels * 2)) * (_channels * 2);

            _fileStream.Position = pos;
        }

        public unsafe int Read(byte[] buffer, int offset, int count)
        {
            if (Math.Abs(_playbackRate) < 0.001f)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int bytesPerFrameIn = _channels * 2;
            int framesRequested = count / 4;
            if (framesRequested <= 0) return 0;

            if (_playbackRate > 0 && Math.Abs(_playbackRate - 1.0f) < 0.001f && _channels == 2)
            {
                int directBytesRead = _fileStream.Read(buffer, offset, count);
                if (directBytesRead < count)
                    Array.Clear(buffer, offset + directBytesRead, count - directBytesRead);
                return count;
            }

            long startFrame = _fileStream.Position / bytesPerFrameIn;
            float rate = Math.Abs(_playbackRate);
            int sourceFramesNeeded = Math.Max(1, (int)Math.Ceiling(framesRequested * rate) + 2);
            long rawStartFrame = _playbackRate < 0
                ? Math.Max(0, startFrame - sourceFramesNeeded + 1)
                : startFrame;

            _fileStream.Position = rawStartFrame * bytesPerFrameIn;
            byte[] rawBuffer = new byte[sourceFramesNeeded * bytesPerFrameIn];
            int bytesRead = _fileStream.Read(rawBuffer, 0, rawBuffer.Length);
            int framesRead = bytesRead / bytesPerFrameIn;

            if (framesRead == 0)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            fixed (byte* pRaw = rawBuffer, pBuf = buffer)
            {
                short* outPtr = (short*)(pBuf + offset);
                int framesWritten = 0;

                for (int i = 0; i < framesRequested; i++)
                {
                    int sourceFrame = _playbackRate < 0
                        ? (int)(startFrame - rawStartFrame - (long)Math.Round(i * rate))
                        : (int)Math.Round(i * rate);

                    if (sourceFrame < 0 || sourceFrame >= framesRead) break;

                    short* inPtr = (short*)(pRaw + (sourceFrame * bytesPerFrameIn));
                    long mixed = 0;
                    int active = 0;

                    if (_channels == 2)
                    {
                        outPtr[i * 2] = inPtr[0];
                        outPtr[i * 2 + 1] = inPtr[1];
                        framesWritten++;
                        continue;
                    }

                    for (int ch = 0; ch < Math.Min(Mask.Length, _channels); ch++)
                    {
                        if (Mask[ch]) { mixed += inPtr[ch]; active++; }
                    }

                    short final = (active > 0) ? (short)Math.Clamp(mixed / active, short.MinValue, short.MaxValue) : (short)0;
                    outPtr[i * 2] = final;
                    outPtr[i * 2 + 1] = final;
                    framesWritten++;
                }

                long frameDelta = (long)Math.Round(framesWritten * rate);
                long nextFrame = _playbackRate < 0
                    ? Math.Max(0, startFrame - frameDelta)
                    : startFrame + frameDelta;
                _fileStream.Position = Math.Min(nextFrame * bytesPerFrameIn, _fileStream.Length);
                int bytesWritten = framesWritten * 4;
                if (bytesWritten < count)
                    Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);
                return count;
            }
        }

        public void Dispose()
        {
            _fileStream?.Close();
            _fileStream?.Dispose();
        }
    }
}
