using System;
using NAudio.Wave;

namespace MxfPlayer.Services
{
    public class ReverseSegmentAudioProvider : IWaveProvider
    {
        private readonly byte[] _reversedPcmData;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _bytesPerSourceFrame;
        private readonly double _fps;
        private readonly double _tempoRate;
        private long _positionSourceSampleIndex;

        public long StartFrame { get; }
        public long EndFrame { get; }
        public float Rate { get; }
        public WaveFormat WaveFormat { get; }
        public bool[] Mask { get; set; } = new bool[8] { true, true, true, true, true, true, true, true };

        public ReverseSegmentAudioProvider(
            byte[] forwardPcmData,
            int channels,
            int sampleRate,
            long startFrame,
            long endFrame,
            double fps,
            float rate,
            double tempoRate)
        {
            _channels = Math.Max(1, channels);
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _bytesPerSourceFrame = _channels * 2;
            _fps = fps > 0 ? fps : 29.97;
            _tempoRate = tempoRate > 0.001 ? tempoRate : 1.0;
            StartFrame = Math.Max(0, startFrame);
            EndFrame = Math.Max(StartFrame, endFrame);
            Rate = rate;
            WaveFormat = new WaveFormat(_sampleRate, 16, 2);
            _reversedPcmData = ReversePcmFrames(forwardPcmData, _bytesPerSourceFrame);
        }

        public bool ContainsFrame(long frameIndex)
        {
            return frameIndex >= StartFrame && frameIndex <= EndFrame;
        }

        public bool HasFrameData(long frameIndex, int requiredBehindMs)
        {
            if (!ContainsFrame(frameIndex))
                return false;

            long sourceOffset = GetSourceSampleOffset(frameIndex);
            long requiredFrames = OriginalSamplesToSourceSamples((_sampleRate * (long)Math.Max(0, requiredBehindMs)) / 1000);
            return sourceOffset + requiredFrames < BufferedSourceFrames;
        }

        public void SeekFrame(long frameIndex)
        {
            frameIndex = Math.Clamp(frameIndex, StartFrame, EndFrame);
            _positionSourceSampleIndex = Math.Clamp(GetSourceSampleOffset(frameIndex), 0, BufferedSourceFrames);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int framesRequested = count / 4;
            if (framesRequested <= 0)
                return 0;

            long startSample = _positionSourceSampleIndex;
            int framesWritten = 0;

            for (int i = 0; i < framesRequested; i++)
            {
                long sourceFrame = startSample + i;
                if (sourceFrame < 0 || sourceFrame >= BufferedSourceFrames)
                    break;

                int sourceOffset = (int)(sourceFrame * _bytesPerSourceFrame);
                WriteStereoSample(buffer, offset + (i * 4), sourceOffset);
                framesWritten++;
            }

            _positionSourceSampleIndex = Math.Min(BufferedSourceFrames, _positionSourceSampleIndex + framesWritten);

            int bytesWritten = framesWritten * 4;
            if (bytesWritten < count)
                Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);

            return count;
        }

        private long BufferedSourceFrames => _reversedPcmData.Length / _bytesPerSourceFrame;

        private long GetSourceSampleOffset(long frameIndex)
        {
            long endSample = FrameToSample(EndFrame);
            long frameSample = FrameToSample(frameIndex);
            return OriginalSamplesToSourceSamples(Math.Max(0, endSample - frameSample));
        }

        private long OriginalSamplesToSourceSamples(long originalSamples)
        {
            return (long)Math.Round(originalSamples / _tempoRate);
        }

        private long FrameToSample(long frameIndex)
        {
            if (frameIndex < 0) frameIndex = 0;
            return (long)Math.Round(frameIndex * _sampleRate / _fps);
        }

        private void WriteStereoSample(byte[] buffer, int outOffset, int sourceOffset)
        {
            if (_channels == 1)
            {
                buffer[outOffset] = _reversedPcmData[sourceOffset];
                buffer[outOffset + 1] = _reversedPcmData[sourceOffset + 1];
                buffer[outOffset + 2] = _reversedPcmData[sourceOffset];
                buffer[outOffset + 3] = _reversedPcmData[sourceOffset + 1];
                return;
            }

            if (_channels == 2)
            {
                buffer[outOffset] = _reversedPcmData[sourceOffset];
                buffer[outOffset + 1] = _reversedPcmData[sourceOffset + 1];
                buffer[outOffset + 2] = _reversedPcmData[sourceOffset + 2];
                buffer[outOffset + 3] = _reversedPcmData[sourceOffset + 3];
                return;
            }

            long mixed = 0;
            int active = 0;
            for (int ch = 0; ch < Math.Min(Mask.Length, _channels); ch++)
            {
                if (!Mask[ch])
                    continue;

                mixed += BitConverter.ToInt16(_reversedPcmData, sourceOffset + (ch * 2));
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

        private static byte[] ReversePcmFrames(byte[] pcmData, int bytesPerFrame)
        {
            if (bytesPerFrame <= 0 || pcmData.Length == 0)
                return Array.Empty<byte>();

            int frameCount = pcmData.Length / bytesPerFrame;
            byte[] reversed = new byte[frameCount * bytesPerFrame];
            for (int i = 0; i < frameCount; i++)
            {
                Buffer.BlockCopy(
                    pcmData,
                    i * bytesPerFrame,
                    reversed,
                    (frameCount - i - 1) * bytesPerFrame,
                    bytesPerFrame);
            }

            return reversed;
        }
    }
}
