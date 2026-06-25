using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace MxfPlayer.Services
{
    public class SlidingPcmAudioProvider : IWaveProvider
    {
        private readonly object _lock = new();
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bytesPerSourceFrame;
        private readonly List<byte> _pcmData = new();
        private long _baseSampleIndex;
        private long _positionSampleIndex;
        private float _playbackRate = 1.0f;

        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8] { true, true, true, true, true, true, true, true };

        public float PlaybackRate
        {
            get => _playbackRate;
            set => _playbackRate = Math.Abs(value) < 0.001f ? 0 : value;
        }

        public SlidingPcmAudioProvider(int channels, int sampleRate)
        {
            _channels = Math.Max(1, channels);
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _bytesPerSourceFrame = _channels * 2;
            WaveFormat = new WaveFormat(_sampleRate, 16, 2);
        }

        public void ResetWindow(long startFrame, double fps, byte[] pcmData)
        {
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                _baseSampleIndex = FrameToSample(startFrame, fps);
                _positionSampleIndex = _baseSampleIndex;
                _pcmData.Clear();
                _pcmData.AddRange(pcmData);
            }
        }

        public bool PrependWindow(long startFrame, double fps, byte[] pcmData)
        {
            if (pcmData.Length == 0)
                return false;
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                long startSample = FrameToSample(startFrame, fps);
                long currentStart = _baseSampleIndex;
                if (startSample >= currentStart)
                    return false;

                long prependFrames = currentStart - startSample;
                long prependBytes = prependFrames * _bytesPerSourceFrame;
                if (prependBytes <= 0)
                    return false;

                int bytesToUse = (int)Math.Min(prependBytes, pcmData.Length);
                bytesToUse = (bytesToUse / _bytesPerSourceFrame) * _bytesPerSourceFrame;
                if (bytesToUse <= 0)
                    return false;

                int startOffset = 0;
                _pcmData.InsertRange(0, new ArraySegment<byte>(pcmData, startOffset, bytesToUse));
                _baseSampleIndex = currentStart - (bytesToUse / _bytesPerSourceFrame);
                return true;
            }
        }

        public void TrimTailAfterFrame(long frameIndex, double fps, int keepAheadMs)
        {
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                long keepEndSample = FrameToSample(frameIndex, fps) +
                    (_sampleRate * (long)Math.Max(0, keepAheadMs)) / 1000;
                long keepSourceFrames = keepEndSample - _baseSampleIndex;
                if (keepSourceFrames < 0)
                {
                    _pcmData.Clear();
                    _baseSampleIndex = keepEndSample;
                    _positionSampleIndex = keepEndSample;
                    return;
                }

                long keepBytes = keepSourceFrames * _bytesPerSourceFrame;
                if (keepBytes >= _pcmData.Count)
                    return;

                int removeStart = (int)Math.Max(0, keepBytes);
                _pcmData.RemoveRange(removeStart, _pcmData.Count - removeStart);
                if (_positionSampleIndex > keepEndSample)
                    _positionSampleIndex = keepEndSample;
            }
        }

        public bool IsFrameDataAvailable(long frameIndex, double fps, int requiredAheadMs)
        {
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                long sample = FrameToSample(frameIndex, fps);
                long offsetFrames = sample - _baseSampleIndex;
                if (offsetFrames < 0)
                    return false;

                long requiredFrames = (_sampleRate * (long)Math.Max(0, requiredAheadMs)) / 1000;
                return offsetFrames + requiredFrames < BufferedSourceFrames;
            }
        }

        public bool IsReverseFrameDataAvailable(long frameIndex, double fps, int requiredBehindMs)
        {
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                long sample = FrameToSample(frameIndex, fps);
                long offsetFrames = sample - _baseSampleIndex;
                if (offsetFrames < 0 || offsetFrames >= BufferedSourceFrames)
                    return false;

                long requiredFrames = (_sampleRate * (long)Math.Max(0, requiredBehindMs)) / 1000;
                return offsetFrames >= requiredFrames;
            }
        }

        public void SeekFrame(long frameIndex, double fps)
        {
            if (fps <= 0) fps = 29.97;
            if (frameIndex < 0) frameIndex = 0;

            lock (_lock)
            {
                _positionSampleIndex = FrameToSample(frameIndex, fps);
            }
        }

        public float GetChannelPeakAtFrame(long frameIndex, double fps, int channel)
        {
            if (fps <= 0) fps = 29.97;
            if (channel < 0 || channel >= _channels)
                return 0f;

            lock (_lock)
            {
                long startSample = FrameToSample(frameIndex, fps);
                long endSample = FrameToSample(frameIndex + 1, fps);
                long startFrame = startSample - _baseSampleIndex;
                long endFrame = endSample - _baseSampleIndex;

                if (startFrame < 0 || startFrame >= BufferedSourceFrames)
                    return 0f;

                endFrame = Math.Min(endFrame, BufferedSourceFrames);
                float peak = 0f;

                for (long sourceFrame = startFrame; sourceFrame < endFrame; sourceFrame++)
                {
                    int sourceOffset = (int)(sourceFrame * _bytesPerSourceFrame + (channel * 2));
                    short sample = ReadInt16(sourceOffset);
                    peak = Math.Max(peak, GetInt16Peak(sample));
                }

                return Math.Min(1f, peak);
            }
        }

        public bool TryGetChannelPeaksAtFrame(long frameIndex, double fps, float[] peaks)
        {
            if (peaks.Length == 0)
                return false;

            Array.Clear(peaks, 0, peaks.Length);
            if (fps <= 0) fps = 29.97;

            lock (_lock)
            {
                long startSample = FrameToSample(frameIndex, fps);
                long endSample = FrameToSample(frameIndex + 1, fps);
                long startFrame = startSample - _baseSampleIndex;
                long endFrame = endSample - _baseSampleIndex;

                if (startFrame < 0 || startFrame >= BufferedSourceFrames)
                    return false;

                endFrame = Math.Min(endFrame, BufferedSourceFrames);
                int channelCount = Math.Min(peaks.Length, _channels);

                for (long sourceFrame = startFrame; sourceFrame < endFrame; sourceFrame++)
                {
                    int frameOffset = (int)(sourceFrame * _bytesPerSourceFrame);
                    for (int channel = 0; channel < channelCount; channel++)
                    {
                        int sourceOffset = frameOffset + (channel * 2);
                        short sample = ReadInt16(sourceOffset);
                        peaks[channel] = Math.Max(peaks[channel], GetInt16Peak(sample));
                    }
                }

                return true;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (Math.Abs(_playbackRate) < 0.001f)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                int framesRequested = count / 4;
                if (framesRequested <= 0)
                    return 0;

                float rate = Math.Abs(_playbackRate);
                long startSample = _positionSampleIndex;
                int framesWritten = 0;

                for (int i = 0; i < framesRequested; i++)
                {
                    long sourceSample = _playbackRate < 0
                        ? startSample - (long)Math.Round(i * rate)
                        : startSample + (long)Math.Round(i * rate);

                    long sourceFrame = sourceSample - _baseSampleIndex;
                    if (sourceFrame < 0 || sourceFrame >= BufferedSourceFrames)
                        break;

                    int sourceOffset = (int)(sourceFrame * _bytesPerSourceFrame);
                    WriteStereoSample(buffer, offset + (i * 4), sourceOffset);
                    framesWritten++;
                }

                long frameDelta = (long)Math.Round(framesWritten * rate);
                _positionSampleIndex = _playbackRate < 0
                    ? Math.Max(0, _positionSampleIndex - frameDelta)
                    : _positionSampleIndex + frameDelta;

                int bytesWritten = framesWritten * 4;
                if (bytesWritten < count)
                    Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);

                return count;
            }
        }

        private long BufferedSourceFrames => _pcmData.Count / _bytesPerSourceFrame;

        private long FrameToSample(long frameIndex, double fps)
        {
            if (frameIndex < 0) frameIndex = 0;
            return (long)Math.Round(frameIndex * _sampleRate / fps);
        }

        private void WriteStereoSample(byte[] buffer, int outOffset, int sourceOffset)
        {
            if (_channels == 1)
            {
                buffer[outOffset] = _pcmData[sourceOffset];
                buffer[outOffset + 1] = _pcmData[sourceOffset + 1];
                buffer[outOffset + 2] = _pcmData[sourceOffset];
                buffer[outOffset + 3] = _pcmData[sourceOffset + 1];
                return;
            }

            if (_channels == 2)
            {
                buffer[outOffset] = _pcmData[sourceOffset];
                buffer[outOffset + 1] = _pcmData[sourceOffset + 1];
                buffer[outOffset + 2] = _pcmData[sourceOffset + 2];
                buffer[outOffset + 3] = _pcmData[sourceOffset + 3];
                return;
            }

            long mixed = 0;
            int active = 0;
            for (int ch = 0; ch < Math.Min(Mask.Length, _channels); ch++)
            {
                if (!Mask[ch])
                    continue;

                int chOffset = sourceOffset + (ch * 2);
                short sample = ReadInt16(chOffset);
                mixed += sample;
                active++;
            }

            short final = active > 0
                ? (short)Math.Clamp(mixed, short.MinValue, short.MaxValue)
                : (short)0;

            buffer[outOffset] = (byte)(final & 0xff);
            buffer[outOffset + 1] = (byte)((final >> 8) & 0xff);
            buffer[outOffset + 2] = buffer[outOffset];
            buffer[outOffset + 3] = buffer[outOffset + 1];
        }

        private short ReadInt16(int offset)
        {
            return unchecked((short)(_pcmData[offset] | (_pcmData[offset + 1] << 8)));
        }

        private static float GetInt16Peak(short sample)
        {
            int magnitude = sample == short.MinValue ? 32768 : Math.Abs(sample);
            return magnitude / 32768f;
        }
    }
}
