using System.Diagnostics;
using MxfPlayer.Services;

namespace MxfPlayer;

internal sealed class Direct3DVideoView : Control
{
    private sealed class RenderSession
    {
        public readonly AutoResetEvent Wake = new(false);
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Stop;
    }

    private readonly PlayerService _player;
    private readonly object _stateGate = new();
    private readonly List<Task> _renderTasks = new();
    private RenderSession? _session;
    private int _width, _height;
    private Color _background = Color.Black;
    private bool _repaint = true, _suspended, _windowVisible = true;
    private string? _error;

    // Raised on the presentation thread after a successful Present. Useful for
    // diagnostics; UI consumers must marshal their own work to the UI thread.
    public event Action<long, long>? FramePresented;

    public Direct3DVideoView(PlayerService player)
    {
        _player = player;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
    }

    public void RequestFrame()
    {
        lock (_stateGate) { _suspended = false; WakeLocked(); }
    }

    public void ClearFrame()
    {
        lock (_stateGate) { _suspended = true; _repaint = true; WakeLocked(); }
    }

    public void SetWindowVisible(bool visible)
    {
        lock (_stateGate) { _windowVisible = visible; _repaint = true; WakeLocked(); }
    }

    private void WakeLocked() => _session?.Wake.Set();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        IntPtr window = Handle;
        var session = new RenderSession();
        lock (_stateGate)
        {
            _width = ClientSize.Width; _height = ClientSize.Height; _background = BackColor;
            _session = session; _repaint = true;
            _renderTasks.Add(session.Finished.Task);
        }
        new Thread(() => RenderLoop(session, window))
        { IsBackground = true, Name = "Video presentation" }.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        lock (_stateGate)
        {
            _width = ClientSize.Width; _height = ClientSize.Height;
            _repaint = true; WakeLocked();
        }
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        lock (_stateGate) { _background = BackColor; _repaint = true; WakeLocked(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        string? error;
        lock (_stateGate) { error = _error; _repaint = true; WakeLocked(); }
        if (error == null) return;
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, $"Direct3D 11 顯示失敗\n{error}", Font,
            ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private void RenderLoop(RenderSession session, IntPtr window)
    {
        Direct3D11FrameRenderer? renderer = null;
        long lastFrame = -1, lastEpoch = -1;
        bool occluded = false;
        var perf = Stopwatch.StartNew();
        int presents = 0;
        double maxPresentMs = 0;
        try
        {
            using var waiter = new PresentationWaiter(session.Wake);
            while (!session.Stop)
            {
                int width, height;
                Color background;
                bool repaint, suspended, windowVisible;
                lock (_stateGate)
                {
                    width = _width; height = _height; background = _background;
                    repaint = _repaint; _repaint = false; suspended = _suspended;
                    windowVisible = _windowVisible;
                }
                if (width <= 0 || height <= 0 || !windowVisible)
                {
                    _player.HoldVideoPresentation(); waiter.Wait(50); continue;
                }
                try
                {
                    if (renderer == null)
                    {
                        renderer = new Direct3D11FrameRenderer(window, width, height);
                        repaint = true; lastFrame = lastEpoch = -1;
                        lock (_stateGate) _error = null;
                    }
                    renderer.Resize(width, height);
                    if (occluded && !renderer.IsVisible())
                    {
                        _player.HoldVideoPresentation(); waiter.Wait(50); continue;
                    }
                    occluded = false;
                    if (suspended)
                    {
                        if (repaint) renderer.Draw(false, background);
                        waiter.Wait(50); continue;
                    }
                    using var presentation = _player.AcquireVideoPresentation(lastFrame, lastEpoch, repaint);
                    if (presentation == null)
                    {
                        if (lastFrame < 0 && repaint) renderer.Draw(false, background);
                        waiter.Wait(_player.IsPlaying ? 1 : 10); continue;
                    }
                    if (session.Stop) break;
                    if (presentation.FrameIndex != lastFrame || presentation.Epoch != lastEpoch)
                        renderer.Upload(presentation.Frame);
                    long started = Stopwatch.GetTimestamp();
                    if (!renderer.Draw(true, background))
                    {
                        occluded = true;
                        _player.HoldVideoPresentation();
                        waiter.Wait(50); continue;
                    }
                    _player.CompleteVideoPresentation(presentation.FrameIndex, presentation.Epoch);
                    lastFrame = presentation.FrameIndex; lastEpoch = presentation.Epoch;
                    FramePresented?.Invoke(lastFrame, lastEpoch);
                    maxPresentMs = Math.Max(maxPresentMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    presents++;
                    if (perf.ElapsedMilliseconds >= 1000)
                    {
                        if (PlaybackDiagnostics.Enabled)
                            Debug.WriteLine($"[VideoPresentPerf] presents={presents} maxPresentMs={maxPresentMs:0.0} requestedRate={_player.RequestedVideoRate:0.###} effectiveRate={_player.EffectiveVideoRate:0.###}");
                        presents = 0; maxPresentMs = 0; perf.Restart();
                    }
                }
                catch (Exception ex)
                {
                    renderer?.Dispose(); renderer = null;
                    _player.HoldVideoPresentation();
                    lock (_stateGate) _error = ex.Message;
                    Trace.WriteLine($"[Direct3D11] {ex}");
                    try { BeginInvoke((Action)Invalidate); } catch (InvalidOperationException) { }
                    waiter.Wait(100);
                }
            }
        }
        catch (Exception ex) { Trace.WriteLine($"[Video presentation] {ex}"); }
        finally
        {
            renderer?.Dispose();
            lock (_stateGate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
                session.Wake.Dispose();
            }
            session.Finished.TrySetResult();
        }
    }

    public Task StopRenderingAsync()
    {
        lock (_stateGate)
        {
            if (_session != null) { _session.Stop = true; _session.Wake.Set(); }
            return Task.WhenAll(_renderTasks);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _ = StopRenderingAsync();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = StopRenderingAsync();
        base.Dispose(disposing);
    }

}
