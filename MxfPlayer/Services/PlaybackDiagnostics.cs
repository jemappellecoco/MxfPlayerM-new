using System.Threading;

namespace MxfPlayer.Services
{
    internal static class PlaybackDiagnostics
    {
        private static int _enabled;

        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }
    }
}
