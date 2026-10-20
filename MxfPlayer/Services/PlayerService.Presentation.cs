using MxfPlayer.Models;

namespace MxfPlayer.Services;

public unsafe partial class PlayerService
{
    private readonly object _presentationGate = new();
    private readonly object _mediaClockGate = new();
    private long _presentationEpoch;
    private long _presentedEpoch = -1;
    private long _presentedFrame = -1;
    private long _presentationLagStarted;
    private long _presentationLagEpoch = -1;
    private volatile float _requestedVideoRate = 1;

    public float RequestedVideoRate => _requestedVideoRate;
    public float EffectiveVideoRate => _videoRate;

    private static float NormalizePlaybackRate(float rate) => rate == 0 ? 1 : rate;

    // Transport state is serialized, but this lock is NEVER held during DXGI
    // Present: DXGI can send messages to the UI thread during window changes.
    public VideoPresentation? AcquireVideoPresentation(long previousFrame, long previousEpoch, bool repaint)
    {
        lock (_presentationGate)
        {
            if (_disposed) return null;
            AdvanceVideoCore(0);
            long epoch = Interlocked.Read(ref _presentationEpoch);
            if (!repaint && previousFrame == _currentFrameIndex && previousEpoch == epoch)
                return null;
            var frame = GetDisplayVideoFrameReference(out long frameIndex);
            return frame == null ? null : new VideoPresentation(frame, frameIndex, epoch);
        }
    }

    public void CompleteVideoPresentation(long frame, long epoch)
    {
        lock (_presentationGate)
        {
            if (epoch != Interlocked.Read(ref _presentationEpoch) || frame != _currentFrameIndex)
                return; // An old in-flight Present cannot acknowledge a seek/rate change.
            _presentedEpoch = epoch;
            _presentedFrame = frame;
        }
    }

    public void HoldVideoPresentation()
    {
        lock (_presentationGate)
        {
            if (_isVideoPlaying && !_isPlaybackStalledForVideo && !_isPlaybackStalledForAudio)
                StallPlaybackForVideoBuffer();
        }
    }

    private void EnsureVideoDecoderNearCurrentFrame(bool forceRestart = false)
    { lock (_presentationGate) EnsureVideoDecoderNearCurrentFrameCore(forceRestart); }

    public void AdvanceVideo(int elapsedMs) { lock (_presentationGate) AdvanceVideoCore(elapsedMs); }
    public void Pause() { lock (_presentationGate) PauseCore(); }
    public void ResumeAudio(int audioBufferTimeoutMs = 3000) { lock (_presentationGate) ResumeAudioCore(audioBufferTimeoutMs); }
    public void SetVideoRate(float rate) { lock (_presentationGate) SetVideoRateCore(rate); }
    public void SetAudioRate(float rate) { lock (_presentationGate) SetAudioRateCore(NormalizePlaybackRate(rate)); }
    public void PrepareAudioForRate(float rate) { lock (_presentationGate) PrepareAudioForRateCore(NormalizePlaybackRate(rate)); }
    public void SeekVideoByFrame(long frame) { lock (_presentationGate) SeekVideoByFrameCore(frame); }
    public void SeekAudioByFrame(long frame, double fps, bool waitForPreviousCache = true)
    { lock (_presentationGate) SeekAudioByFrameCore(frame, fps, waitForPreviousCache); }
    public void StopAudioBridge() { lock (_presentationGate) StopAudioBridgeCore(); }
    public Task StartAudioBridge(string path, int audioCount, long startTimeMs = 0, float rate = 1,
        double fps = 0, int sampleRate = 48000)
    {
        lock (_presentationGate)
            return StartAudioBridgeCore(path, audioCount, startTimeMs, rate, fps, sampleRate);
    }
}
