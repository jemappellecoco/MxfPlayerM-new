using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MxfPlayer.Services;

// A worker-owned high-resolution waitable timer avoids both WM_TIMER granularity
// and a busy-spin loop. The wake event interrupts waits for seek/resize/shutdown.
internal sealed class PresentationWaiter : IDisposable
{
    private readonly EventWaitHandle _timer;
    private readonly WaitHandle[] _handles;

    public PresentationWaiter(WaitHandle wake)
    {
        SafeWaitHandle handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
        }
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        _timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        SafeWaitHandle original = _timer.SafeWaitHandle;
        _timer.SafeWaitHandle = handle;
        original.Dispose();
        _handles = new[] { wake, _timer };
    }

    public void Wait(double milliseconds)
    {
        long due = -(long)(Math.Clamp(milliseconds, .25, 100) * 10000);
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(_handles);
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
        IntPtr routine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
