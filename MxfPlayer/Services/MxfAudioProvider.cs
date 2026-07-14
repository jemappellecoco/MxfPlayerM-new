using NAudio.Wave;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MxfPlayer.Services
{
    public class MxfAudioProvider : IWaveProvider, IDisposable
    {
        private readonly FileStream _fileStream;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly long _baseTimeMs; // 此快取檔的起始影片毫秒
        private readonly object _streamLock = new();
        private bool _disposed;
        private float _playbackRate = 1.0f;
        private double _tempoRate = 1.0;
        private int _underrun;
        private long _lastUnderrunLogTicks;

        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8] { true, true, true, true, true, true, true, true };
        public float PlaybackRate
        {
            get => _playbackRate;
            set => _playbackRate = Math.Abs(value) < 0.001f ? 0 : value;
        }

        public double TempoRate
        {
            get => _tempoRate;
            set => _tempoRate = value > 0.001 ? value : 1.0;
        }

        public bool ConsumeUnderrun()
        {
            return Interlocked.Exchange(ref _underrun, 0) != 0;
        }

        private void MarkUnderrun(string reason, long startFrame, int framesRequested, int framesRead, int framesWritten)
        {
            Interlocked.Exchange(ref _underrun, 1);

            long nowTicks = Stopwatch.GetTimestamp();
            long lastTicks = Interlocked.Read(ref _lastUnderrunLogTicks);
            if ((nowTicks - lastTicks) * 1000.0 / Stopwatch.Frequency < 1000)
                return;
            if (Interlocked.CompareExchange(ref _lastUnderrunLogTicks, nowTicks, lastTicks) != lastTicks)
                return;

            long lengthFrames = _fileStream.Length / Math.Max(1, _channels * 2);
            Debug.WriteLine(
                $"[AudioUnderrun] reason={reason} startFrame={startFrame} " +
                $"requested={framesRequested} read={framesRead} written={framesWritten} " +
                $"fileFrames={lengthFrames} rate={_playbackRate:0.###}");
        }

        public MxfAudioProvider(string pcmPath, int channels, long baseTimeMs, int sampleRate)
        {
            _fileStream = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _channels = channels;
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _baseTimeMs = baseTimeMs;
            WaveFormat = new WaveFormat(_sampleRate, 16, 2);
        }

        public bool IsDataAvailable(long timeMs)
        {
            if (timeMs < _baseTimeMs) return false;
            long targetOffsetBytes = MediaMsToSourceBytes(timeMs - _baseTimeMs);
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
            long targetOffsetBytes = MediaMsToSourceBytes(relativeMs);
            long requiredAheadBytes = MediaMsToSourceBytes(requiredAheadMs);
            long requiredBytes = requiredAheadMs <= 0
                ? _channels * 2L
                : requiredAheadBytes;
            return _fileStream.Length >= targetOffsetBytes + requiredBytes;
        }

        public bool IsReverseFrameDataAvailable(long frameIndex, double fps, int requiredBehindMs)
        {
            if (fps <= 0) fps = 29.97;

            long timeMs = (long)Math.Round(frameIndex * 1000.0 / fps);
            if (timeMs < _baseTimeMs) return false;

            long relativeMs = timeMs - _baseTimeMs;
            long targetOffsetBytes = MediaMsToSourceBytes(relativeMs);
            long requiredBehindBytes = MediaMsToSourceBytes(requiredBehindMs);
            return targetOffsetBytes >= requiredBehindBytes &&
                   _fileStream.Length > targetOffsetBytes + (_channels * 2);
        }

        public void Seek(long timeMs)
        {
            long relativeMs = timeMs - _baseTimeMs;
            if (relativeMs < 0) relativeMs = 0;

            long pos = MediaMsToSourceBytes(relativeMs);
            pos = (pos / (_channels * 2)) * (_channels * 2);

            _fileStream.Position = pos;
        }

        public void SeekFrame(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;

            long sampleIndex = MediaSampleToSourceSample((long)Math.Round(frameIndex * _sampleRate / fps));
            long baseSampleIndex = MediaSampleToSourceSample((long)Math.Round(_baseTimeMs * _sampleRate / 1000.0));
            sampleIndex = Math.Max(0, sampleIndex - baseSampleIndex);
            long pos = sampleIndex * _channels * 2;
            pos = (pos / (_channels * 2)) * (_channels * 2);

            _fileStream.Position = pos;
        }

        public float GetChannelPeakAtFrame(long frameIndex, double fps, int channel)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;
            if (channel < 0 || channel >= _channels)
                return 0f;

            lock (_streamLock)
            {
                if (_disposed)
                    return 0f;

                long originalPosition = _fileStream.Position;
                try
                {
                    long startSample = MediaSampleToSourceSample((long)Math.Round(frameIndex * _sampleRate / fps));
                    long endSample = MediaSampleToSourceSample((long)Math.Round((frameIndex + 1) * _sampleRate / fps));
                    long baseSampleIndex = MediaSampleToSourceSample((long)Math.Round(_baseTimeMs * _sampleRate / 1000.0));
                    startSample -= baseSampleIndex;
                    endSample -= baseSampleIndex;

                    if (startSample < 0)
                        return 0f;

                    long sourceFrames = Math.Max(1, endSample - startSample);
                    long pos = startSample * _channels * 2L;
                    pos = (pos / (_channels * 2L)) * (_channels * 2L);
                    if (pos < 0 || pos >= _fileStream.Length)
                        return 0f;

                    int bytesToRead = (int)Math.Min(sourceFrames * _channels * 2L, _fileStream.Length - pos);
                    bytesToRead = (bytesToRead / (_channels * 2)) * (_channels * 2);
                    if (bytesToRead <= 0)
                        return 0f;

                    byte[] rawBuffer = new byte[bytesToRead];
                    _fileStream.Position = pos;
                    int bytesRead = _fileStream.Read(rawBuffer, 0, rawBuffer.Length);
                    int framesRead = bytesRead / (_channels * 2);
                    float peak = 0f;

                    for (int i = 0; i < framesRead; i++)
                    {
                        int offset = (i * _channels * 2) + (channel * 2);
                        short sample = BitConverter.ToInt16(rawBuffer, offset);
                        peak = Math.Max(peak, GetInt16Peak(sample));
                    }

                    return Math.Min(1f, peak);
                }
                finally
                {
                    if (!_disposed)
                        _fileStream.Position = Math.Min(originalPosition, _fileStream.Length);
                }
            }
        }

        public byte[]? ReadFrameStereoPcm(long frameIndex, double fps, bool[] mask)
        {
            if (frameIndex < 0) frameIndex = 0;
            if (fps <= 0) fps = 29.97;

            lock (_streamLock)
            {
                if (_disposed)
                    return null;

                long originalPosition = _fileStream.Position;
                try
                {
                    long startSample = MediaSampleToSourceSample((long)Math.Round(frameIndex * _sampleRate / fps));
                    long endSample = MediaSampleToSourceSample((long)Math.Round((frameIndex + 1) * _sampleRate / fps));
                    long baseSampleIndex = MediaSampleToSourceSample((long)Math.Round(_baseTimeMs * _sampleRate / 1000.0));
                    startSample -= baseSampleIndex;
                    endSample -= baseSampleIndex;

                    if (startSample < 0)
                        return null;

                    long sourceFrames = Math.Max(1, endSample - startSample);
                    int bytesPerFrameIn = _channels * 2;
                    long pos = startSample * bytesPerFrameIn;
                    pos = (pos / bytesPerFrameIn) * bytesPerFrameIn;
                    if (pos < 0 || pos >= _fileStream.Length)
                        return null;

                    int bytesToRead = (int)Math.Min(sourceFrames * bytesPerFrameIn, _fileStream.Length - pos);
                    bytesToRead = (bytesToRead / bytesPerFrameIn) * bytesPerFrameIn;
                    if (bytesToRead <= 0)
                        return null;

                    byte[] rawBuffer = new byte[bytesToRead];
                    _fileStream.Position = pos;
                    int bytesRead = _fileStream.Read(rawBuffer, 0, rawBuffer.Length);
                    int framesRead = bytesRead / bytesPerFrameIn;
                    if (framesRead <= 0)
                        return null;

                    byte[] stereoPcm = new byte[framesRead * 4];
                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        int inOffset = frame * bytesPerFrameIn;
                        short left;
                        short right;

                        if (_channels == 1)
                        {
                            left = BitConverter.ToInt16(rawBuffer, inOffset);
                            right = left;
                        }
                        else if (_channels == 2)
                        {
                            left = BitConverter.ToInt16(rawBuffer, inOffset);
                            right = BitConverter.ToInt16(rawBuffer, inOffset + 2);
                        }
                        else
                        {
                            long mixed = 0;
                            int active = 0;
                            int channelLimit = Math.Min(_channels, mask.Length);
                            for (int channel = 0; channel < channelLimit; channel++)
                            {
                                if (!mask[channel])
                                    continue;

                                mixed += BitConverter.ToInt16(rawBuffer, inOffset + (channel * 2));
                                active++;
                            }

                            short sample = active > 0
                                ? (short)Math.Clamp(mixed, short.MinValue, short.MaxValue)
                                : (short)0;
                            left = sample;
                            right = sample;
                        }

                        int outOffset = frame * 4;
                        stereoPcm[outOffset] = (byte)(left & 0xff);
                        stereoPcm[outOffset + 1] = (byte)((left >> 8) & 0xff);
                        stereoPcm[outOffset + 2] = (byte)(right & 0xff);
                        stereoPcm[outOffset + 3] = (byte)((right >> 8) & 0xff);
                    }

                    return stereoPcm;
                }
                finally
                {
                    if (!_disposed)
                        _fileStream.Position = Math.Min(originalPosition, _fileStream.Length);
                }
            }
        }

        public unsafe int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                lock (_streamLock)
                {
                    if (_disposed || Math.Abs(_playbackRate) < 0.001f)
                    {
                        Array.Clear(buffer, offset, count);
                        return count;
                    }

                    int bytesPerFrameIn = _channels * 2;
                    int framesRequested = count / 4;
                    if (framesRequested <= 0) return 0;

                    long startFrame = _fileStream.Position / bytesPerFrameIn;
                    float rate = Math.Abs(_playbackRate);
                    int sourceFramesNeeded = Math.Max(1, (int)Math.Ceiling(framesRequested * rate) + 2);
                    long rawStartFrame = _playbackRate < 0
                        ? Math.Max(0, startFrame - sourceFramesNeeded + 1)
                        : startFrame;

                    _fileStream.Position = rawStartFrame * bytesPerFrameIn;
                    int rawBufferSize = sourceFramesNeeded * bytesPerFrameIn;
                    byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(rawBufferSize);
                    try
                    {
                        int bytesRead = _fileStream.Read(rawBuffer, 0, rawBufferSize);
                        int framesRead = bytesRead / bytesPerFrameIn;

                        if (framesRead == 0)
                        {
                            MarkUnderrun("no-data", startFrame, framesRequested, framesRead, 0);
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
                                short final = (active > 0)
                                ? (short)Math.Clamp(mixed, short.MinValue, short.MaxValue)
                                : (short)0;
                                //short final = (active > 0) ? unchecked((short)mixed) : (short)0;
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
                            {
                                MarkUnderrun("short-read", startFrame, framesRequested, framesRead, framesWritten);
                                Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);
                            }
                            return count;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rawBuffer);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            catch (IOException)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
        }

        private static float GetInt16Peak(short sample)
        {
            int magnitude = sample == short.MinValue ? 32768 : Math.Abs(sample);
            return magnitude / 32768f;
        }

        private long MediaMsToSourceBytes(long mediaMs)
        {
            if (mediaMs <= 0)
                return 0;

            long mediaSamples = (_sampleRate * mediaMs) / 1000;
            return MediaSampleToSourceSample(mediaSamples) * _channels * 2;
        }

        private long MediaSampleToSourceSample(long mediaSample)
        {
            return (long)Math.Round(mediaSample / _tempoRate);
        }

        public void Dispose()
        {
            lock (_streamLock)
            {
                if (_disposed) return;
                _disposed = true;
                _fileStream?.Close();
                _fileStream?.Dispose();
            }
        }
    }
}
