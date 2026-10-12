using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MxfPlayer.Services
{
    internal static class PcmCacheCleanup
    {
        public static void DeleteAfterWriter(string path, Task? writer)
        {
            _ = Task.Run(async () =>
            {
                if (writer != null)
                {
                    try { await writer.ConfigureAwait(false); }
                    catch { /* Cleanup also runs after cancellation or failure. */ }
                }
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (TryDeleteUnused(path)) return;
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                Debug.WriteLine($"[AudioCacheCleanup] Could not delete {path}; startup cleanup will retry.");
            });
        }

        public static void CleanOldCaches()
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(Path.GetTempPath(), "MxfPlayer_*.pcm"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!Guid.TryParseExact(name.Substring("MxfPlayer_".Length), "N", out _)) continue;
                    // Avoid a different player's brief gap between creating and opening a cache.
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) continue;
                    TryDeleteUnused(path);
                }
            }
            catch (IOException ex) { Debug.WriteLine($"[AudioCacheCleanup] {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine($"[AudioCacheCleanup] {ex.Message}"); }
        }

        private static bool TryDeleteUnused(string path)
        {
            try
            {
                // Exclusive open makes checking and deleting atomic: an active reader
                // or writer prevents deletion, including in another player process.
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
