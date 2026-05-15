using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class MediaSpecService
    {
        public MediaSpecCheckResult CheckWeiLaiSpec(MediaInfoResult info, bool includeDecodeCheck = false)
        {
            var result = new MediaSpecCheckResult();

            if (info == null)
            {
                result.Errors.Add("MediaInfo 資料為空");
                result.IsPass = false;
                return result;
            }

            string specType = GetSpecType(info);

            if (specType == "HD")
            {
                AddHdSpecErrors(info, result);
            }
            else if (specType == "SD")
            {
                AddSdSpecErrors(info, result);
            }
            else
            {
                result.Errors.Add($"影格尺寸錯誤：目前是 {info.Width} x {info.Height}，不符合 HD 或 SD 入庫規格");
            }

            AddConformanceErrors(info, result);

            if (includeDecodeCheck)
                AddDecodeIntegrityErrors(info.FullPath, result);

            // 最後統一判斷：只要有任何錯誤，就不通過
            result.IsPass = result.Errors.Count == 0;

            return result;
        }

        public string GetSpecType(MediaInfoResult info)
        {
            if (info == null)
                return "Unknown";

            if (info.Width == "1920" && info.Height == "1080")
                return "HD";

            if (info.Width == "720" && info.Height == "480")
                return "SD";

            return "Unknown";
        }

        public MediaSpecCheckResult CheckWeiLaiHdSpec(MediaInfoResult info)
        {
            var result = new MediaSpecCheckResult();

            if (info == null)
            {
                result.Errors.Add("MediaInfo 資料為空");
                result.IsPass = false;
                return result;
            }

            AddHdSpecErrors(info, result);
            result.IsPass = result.Errors.Count == 0;

            return result;
        }

        public MediaSpecCheckResult CheckWeiLaiSdSpec(MediaInfoResult info)
        {
            var result = new MediaSpecCheckResult();

            if (info == null)
            {
                result.Errors.Add("MediaInfo 資料為空");
                result.IsPass = false;
                return result;
            }

            AddSdSpecErrors(info, result);
            result.IsPass = result.Errors.Count == 0;

            return result;
        }

        private void AddHdSpecErrors(MediaInfoResult info, MediaSpecCheckResult result)
        {
            // HD 格式：XDCAM HD422 1920x1080 59.94i

            if (!ContainsText(info.CommercialName, "XDCAM HD422"))
                result.Errors.Add($"格式錯誤：目前是 {DisplayValue(info.CommercialName)}，應為 XDCAM HD422");

            if (info.Width != "1920")
                result.Errors.Add($"寬度錯誤：目前是 {DisplayValue(info.Width)}，應為 1920");

            if (info.Height != "1080")
                result.Errors.Add($"高度錯誤：目前是 {DisplayValue(info.Height)}，應為 1080");

            if (!IsBitRate50Mbps(info.VideoBitRate))
                result.Errors.Add($"影片比特率錯誤：目前是 {DisplayValue(info.VideoBitRate)}，應為 50 Mbps");

            if (!EqualsText(info.DisplayAspect, "16:9"))
                result.Errors.Add($"長寬比錯誤：目前是 {DisplayValue(info.DisplayAspect)}，應為 16:9");

            if (!IsFrameRate2997(info.FrameRateValue, info.FrameRate))
                result.Errors.Add($"影格速率錯誤：目前是 {DisplayValue(info.FrameRateDisplay)}，應為 29.97");

            if (!EqualsText(info.ScanOrder, "Top Field First"))
                result.Errors.Add($"場次順序錯誤：目前是 {DisplayValue(info.ScanOrder)}，應為 Top Field First");

            if (!EqualsText(info.VideoBitDepth, "8"))
                result.Errors.Add($"視頻位元深度錯誤：目前是 {DisplayValue(info.VideoBitDepth)} bit，應為 8 bit");

            if (!EqualsText(info.AudioSamplingRate, "48000"))
                result.Errors.Add($"採樣速率錯誤：目前是 {DisplayValue(info.AudioSamplingRate)} Hz，應為 48000 Hz");

            if (!EqualsText(info.AudioCount, "8"))
                result.Errors.Add($"音頻通道錯誤：目前是 {DisplayValue(info.AudioCount)} ch，應為 8 ch");

            if (!EqualsText(info.AudioBitDepth, "24"))
                result.Errors.Add($"音頻位元深度錯誤：目前是 {DisplayValue(info.AudioBitDepth)} bit，應為 24 bit");

            if (!EqualsText(info.TimeCodeMode, "Drop Frame"))
                result.Errors.Add($"時間碼模式錯誤：目前是 {DisplayValue(info.TimeCodeMode)}，應為 Drop Frame");
        }

        private void AddSdSpecErrors(MediaInfoResult info, MediaSpecCheckResult result)
        {
            // SD 格式：DVCPRO50 MXF

            if (!ContainsText(info.CommercialName, "DVCPRO50"))
                result.Errors.Add($"格式錯誤：目前是 {DisplayValue(info.CommercialName)}，應為 DVCPRO50");

            if (info.Width != "720")
                result.Errors.Add($"寬度錯誤：目前是 {DisplayValue(info.Width)}，應為 720");

            if (info.Height != "480")
                result.Errors.Add($"高度錯誤：目前是 {DisplayValue(info.Height)}，應為 480");

            if (!IsBitRate50Mbps(info.VideoBitRate))
                result.Errors.Add($"影片比特率錯誤：目前是 {DisplayValue(info.VideoBitRate)}，應為 50 Mbps");

            if (!EqualsText(info.DisplayAspect, "4:3"))
                result.Errors.Add($"長寬比錯誤：目前是 {DisplayValue(info.DisplayAspect)}，應為 4:3");

            if (!IsFrameRate2997(info.FrameRateValue, info.FrameRate))
                result.Errors.Add($"影格速率錯誤：目前是 {DisplayValue(info.FrameRateDisplay)}，應為 29.97");

            if (!EqualsText(info.ScanOrder, "Bottom Field First"))
                result.Errors.Add($"場次順序錯誤：目前是 {DisplayValue(info.ScanOrder)}，應為 Bottom Field First");

            if (!EqualsText(info.VideoBitDepth, "8"))
                result.Errors.Add($"視頻位元深度錯誤：目前是 {DisplayValue(info.VideoBitDepth)} bit，應為 8 bit");

            if (!EqualsText(info.AudioSamplingRate, "48000"))
                result.Errors.Add($"採樣速率錯誤：目前是 {DisplayValue(info.AudioSamplingRate)} Hz，應為 48000 Hz");

            if (!EqualsText(info.AudioCount, "4"))
                result.Errors.Add($"音頻通道錯誤：目前是 {DisplayValue(info.AudioCount)} ch，應為 4 ch");

            if (!EqualsText(info.AudioBitDepth, "24"))
                result.Errors.Add($"音頻位元深度錯誤：目前是 {DisplayValue(info.AudioBitDepth)} bit，應為 24 bit");

            if (!EqualsText(info.TimeCodeMode, "Drop Frame"))
                result.Errors.Add($"時間碼模式錯誤：目前是 {DisplayValue(info.TimeCodeMode)}，應為 Drop Frame");
        }

        private void AddConformanceErrors(MediaInfoResult info, MediaSpecCheckResult result)
        {
            foreach (var error in info.ConformanceErrors)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    result.Errors.Add("MXF 檔案完整性錯誤：" + error);
            }
        }

        public MediaSpecCheckResult CheckDecodeIntegrity(string filePath, CancellationToken token = default)
        {
            var result = new MediaSpecCheckResult();
            AddDecodeIntegrityErrors(filePath, result, token);
            result.IsPass = result.Errors.Count == 0;
            return result;
        }

        private void AddDecodeIntegrityErrors(string filePath, MediaSpecCheckResult result, CancellationToken token = default)
        {
            string ffmpegPath = @"C:\ffmpeg-7.1.1-essentials_build\bin\ffmpeg.exe";

            Debug.WriteLine("[DecodeCheck] start: " + filePath);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                result.Errors.Add("影片路徑為空，無法檢查檔案是否可播放");
                return;
            }

            if (!File.Exists(filePath))
            {
                result.Errors.Add($"找不到影片檔案：{filePath}");
                return;
            }

            if (!File.Exists(ffmpegPath))
            {
                result.Errors.Add($"找不到 FFmpeg：{ffmpegPath}");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,

                    // -v error：只顯示錯誤
                    // -xerror：遇到 decode error 就直接失敗
                    // -f null -：不輸出檔案，只檢查能不能完整解碼
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-xerror");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(filePath);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("null");
                psi.ArgumentList.Add("-");

                using var process = Process.Start(psi);

                if (process == null)
                {
                    result.Errors.Add("FFmpeg 啟動失敗，無法檢查影片完整性");
                    return;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                while (!process.WaitForExit(200))
                {
                    if (!token.IsCancellationRequested)
                        continue;

                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new OperationCanceledException(token);
                }

                outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();

                Debug.WriteLine("[DecodeCheck] exit code: " + process.ExitCode);

                if (process.ExitCode != 0)
                {
                    string message = string.IsNullOrWhiteSpace(error)
                        ? "FFmpeg 解碼失敗，但沒有回傳錯誤訊息"
                        : SimplifyFfmpegError(error);

                    result.Errors.Add("影片檔案可能損壞或無法完整播放：" + message);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("檢查影片完整性時發生錯誤：" + ex.Message);
            }
        }

        private string SimplifyFfmpegError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return "";

            string[] lines = error
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return error.Trim();

            // 避免 UI 顯示超長錯誤，只取前幾行重點
            int take = Math.Min(lines.Length, 5);
            return string.Join(" / ", lines[..take]).Trim();
        }

        private bool EqualsText(string actual, string expected)
        {
            return string.Equals(
                actual?.Trim(),
                expected,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private bool ContainsText(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual))
                return false;

            return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsFrameRate2997(string frameRateValue, string fallbackFrameRate)
        {
            string value = !string.IsNullOrWhiteSpace(frameRateValue)
                ? frameRateValue
                : fallbackFrameRate;

            if (!double.TryParse(value, out double fps))
                return false;

            return Math.Abs(fps - 29.97) < 0.01;
        }

        private bool IsBitRate50Mbps(string bitRate)
        {
            if (string.IsNullOrWhiteSpace(bitRate))
                return false;

            return bitRate.Contains("50") &&
                   bitRate.Contains("Mb", StringComparison.OrdinalIgnoreCase);
        }

        private string DisplayValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "未取得" : value;
        }
    }
}
