using System;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class MediaSpecService
    {
        public MediaSpecCheckResult CheckWeiLaiSpec(MediaInfoResult info)
        {
            if (info == null)
            {
                var result = new MediaSpecCheckResult();
                result.Errors.Add("MediaInfo 資料為空");
                result.IsPass = false;
                return result;
            }

            string specType = GetSpecType(info);

            if (specType == "HD")
                return CheckWeiLaiHdSpec(info);

            if (specType == "SD")
                return CheckWeiLaiSdSpec(info);

            var unknownResult = new MediaSpecCheckResult();
            unknownResult.Errors.Add($"影格尺寸錯誤：目前是 {info.Width} x {info.Height}，不符合 HD 或 SD 入庫規格");
            unknownResult.IsPass = false;
            return unknownResult;
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

            // HD 格式：XDCAM HD422 1920x1080 59.94i

            if (!ContainsText(info.CommercialName, "XDCAM HD422"))
                result.Errors.Add($"格式錯誤：目前是 {info.CommercialName}，應為 XDCAM HD422");

            if (info.Width != "1920")
                result.Errors.Add($"寬度錯誤：目前是 {info.Width}，應為 1920");

            if (info.Height != "1080")
                result.Errors.Add($"高度錯誤：目前是 {info.Height}，應為 1080");

            if (!IsBitRate50Mbps(info.VideoBitRate))
                result.Errors.Add($"影片比特率錯誤：目前是 {info.VideoBitRate}，應為 50 Mbps");

            if (!EqualsText(info.DisplayAspect, "16:9"))
                result.Errors.Add($"長寬比錯誤：目前是 {info.DisplayAspect}，應為 16:9");

            if (!IsFrameRate2997(info.FrameRateValue, info.FrameRate))
                result.Errors.Add($"影格速率錯誤：目前是 {info.FrameRateDisplay}，應為 29.97");

            if (!EqualsText(info.ScanOrder, "Top Field First"))
                result.Errors.Add($"場次順序錯誤：目前是 {info.ScanOrder}，應為 Top Field First");

            if (!EqualsText(info.VideoBitDepth, "8"))
                result.Errors.Add($"視頻位元深度錯誤：目前是 {info.VideoBitDepth} bit，應為 8 bit");

            if (!EqualsText(info.AudioSamplingRate, "48000"))
                result.Errors.Add($"採樣速率錯誤：目前是 {info.AudioSamplingRate} Hz，應為 48000 Hz");

            if (!EqualsText(info.AudioCount, "8"))
                result.Errors.Add($"音頻通道錯誤：目前是 {info.AudioCount} ch，應為 8 ch");

            if (!EqualsText(info.AudioBitDepth, "24"))
                result.Errors.Add($"音頻位元深度錯誤：目前是 {info.AudioBitDepth} bit，應為 24 bit");

            if (!EqualsText(info.TimeCodeMode, "Drop Frame"))
                result.Errors.Add($"時間碼模式錯誤：目前是 {info.TimeCodeMode}，應為 Drop Frame");

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

            // SD 格式：DVCPRO50 MXF

            if (!ContainsText(info.CommercialName, "DVCPRO50"))
                result.Errors.Add($"格式錯誤：目前是 {info.CommercialName}，應為 DVCPRO50");

            if (info.Width != "720")
                result.Errors.Add($"寬度錯誤：目前是 {info.Width}，應為 720");

            if (info.Height != "480")
                result.Errors.Add($"高度錯誤：目前是 {info.Height}，應為 480");

            if (!IsBitRate50Mbps(info.VideoBitRate))
                result.Errors.Add($"影片比特率錯誤：目前是 {info.VideoBitRate}，應為 50 Mbps");

            if (!EqualsText(info.DisplayAspect, "4:3"))
                result.Errors.Add($"長寬比錯誤：目前是 {info.DisplayAspect}，應為 4:3");

            if (!IsFrameRate2997(info.FrameRateValue, info.FrameRate))
                result.Errors.Add($"影格速率錯誤：目前是 {info.FrameRateDisplay}，應為 29.97");

            if (!EqualsText(info.ScanOrder, "Bottom Field First"))
                result.Errors.Add($"場次順序錯誤：目前是 {info.ScanOrder}，應為 Bottom Field First");

            if (!EqualsText(info.VideoBitDepth, "8"))
                result.Errors.Add($"視頻位元深度錯誤：目前是 {info.VideoBitDepth} bit，應為 8 bit");

            if (!EqualsText(info.AudioSamplingRate, "48000"))
                result.Errors.Add($"採樣速率錯誤：目前是 {info.AudioSamplingRate} Hz，應為 48000 Hz");

            if (!EqualsText(info.AudioCount, "4"))
                result.Errors.Add($"音頻通道錯誤：目前是 {info.AudioCount} ch，應為 4 ch");

            if (!EqualsText(info.AudioBitDepth, "24"))
                result.Errors.Add($"音頻位元深度錯誤：目前是 {info.AudioBitDepth} bit，應為 24 bit");

            if (!EqualsText(info.TimeCodeMode, "Drop Frame"))
                result.Errors.Add($"時間碼模式錯誤：目前是 {info.TimeCodeMode}，應為 Drop Frame");

            result.IsPass = result.Errors.Count == 0;
            return result;
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
    }
}