using System.Diagnostics;
using MxfPlayer.Models;
using MxfPlayer.Services;

namespace MxfPlayer;

internal sealed class Direct3DVideoView : Control
{
    private VideoFrameData? _frame;
    private Direct3D11FrameRenderer? _renderer;
    private bool _uploadPending;
    private long _retryAfter;
    private string? _error;
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 2000 };
    private readonly Stopwatch _paintClock = Stopwatch.StartNew();
    private double _maxPaintMs;
    private int _paintCount;

    public Direct3DVideoView()
    {
        // A swap chain presents to this HWND directly. WinForms double buffering
        // would place a second GDI surface over the Direct3D output.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        _retryTimer.Tick += (_, _) => Invalidate();
    }

    // Takes ownership of the reference returned by PlayerService.
    public void SetFrame(VideoFrameData? frame)
    {
        if (ReferenceEquals(_frame, frame))
        {
            frame?.Dispose();
            return;
        }
        var previous = _frame;
        _frame = frame;
        _uploadPending = frame != null;
        previous?.Dispose();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        long start = Stopwatch.GetTimestamp();
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_renderer == null && Environment.TickCount64 < _retryAfter)
        {
            PaintError(e);
            return;
        }
        try
        {
            if (_renderer == null)
            {
                _renderer = new Direct3D11FrameRenderer(Handle, ClientSize.Width, ClientSize.Height);
                _uploadPending = _frame != null;
                _error = null;
                _retryTimer.Stop();
            }
            _renderer.Resize(ClientSize.Width, ClientSize.Height);
            if (_uploadPending && _frame != null)
            {
                _renderer.Upload(_frame);
                _uploadPending = false;
            }
            _renderer.Draw(_frame != null, BackColor);
        }
        catch (Exception ex)
        {
            // Recreate the device after a lost/reset device or display change.
            // Keep the retained CPU frame so paused video can be uploaded again.
            _renderer?.Dispose();
            _renderer = null;
            _retryAfter = Environment.TickCount64 + 2000;
            _error = ex.Message;
            _retryTimer.Start();
            Trace.WriteLine($"[Direct3D11] {ex}");
            PaintError(e);
        }
        _paintCount++;
        _maxPaintMs = Math.Max(_maxPaintMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (_paintClock.ElapsedMilliseconds >= 1000)
        {
            if (PlaybackDiagnostics.Enabled)
                Debug.WriteLine($"[VideoPaintPerf] renderer=D3D11 paints={_paintCount} maxPaintMs={_maxPaintMs:0.0}");
            _paintCount = 0; _maxPaintMs = 0; _paintClock.Restart();
        }
    }

    private void PaintError(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, $"Direct3D 11 顯示失敗\n{_error}", Font,
            ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _renderer?.Dispose();
        _renderer = null;
        _retryTimer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retryTimer.Dispose();
            _renderer?.Dispose(); _renderer = null;
            _frame?.Dispose(); _frame = null;
        }
        base.Dispose(disposing);
    }
}
