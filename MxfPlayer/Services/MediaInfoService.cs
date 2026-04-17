using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class MediaInfoService
    {
        private readonly string _mediaInfoPath = @"C:\Tools\MediaInfo_CLI\MediaInfo.exe";

        public MediaInfoResult GetInfo(string filePath)
        {
            if (!File.Exists(_mediaInfoPath))
                throw new FileNotFoundException("找不到 MediaInfo.exe", _mediaInfoPath);

            if (!File.Exists(filePath))
                throw new FileNotFoundException("找不到影片檔案", filePath);

            var psi = new ProcessStartInfo
            {
                FileName = _mediaInfoPath,
                Arguments = $"--Output=JSON \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("MediaInfo 啟動失敗");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (string.IsNullOrWhiteSpace(output))
                throw new Exception("MediaInfo 沒有輸出內容：" + error);

            // 開發時可打開這行檢查 JSON
            // File.WriteAllText("debug.json", output);

            using var doc = JsonDocument.Parse(output);
            var tracks = doc.RootElement
                .GetProperty("media")
                .GetProperty("track");

            JsonElement general = default;
            JsonElement video = default;
            JsonElement timecode = default;

            foreach (var t in tracks.EnumerateArray())
            {
                string type = Get(t, "@type");

                if (type == "General")
                    general = t;
                else if (type == "Video")
                    video = t;
                else if (type == "Other" && t.TryGetProperty("TimeCode_FirstFrame", out _))
                    timecode = t;
            }

            string width = Get(video, "Width");
            string height = Get(video, "Height");
            string frameRate = Get(video, "FrameRate");
            string fpsNum = Get(video, "FrameRate_Num");
            string fpsDen = Get(video, "FrameRate_Den");
            string frameCount = Get(video, "FrameCount");

            string som = Get(video, "TimeCode_FirstFrame");
            string eom = Get(timecode, "TimeCode_LastFrame");
            string durationTc = BuildDurationTc(frameCount, fpsNum, fpsDen);

            if (string.IsNullOrWhiteSpace(durationTc))
                durationTc = Get(general, "Duration");

            return new MediaInfoResult
            {
                FileName = Path.GetFileName(filePath),
                FullPath = filePath,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                AudioCount = Get(general, "AudioCount"),
                CommercialName = Get(general, "Format_Commercial_IfAny"),
                ScanType = Get(video, "ScanType"),
                ScanOrder = ConvertScanOrder(Get(video, "ScanOrder")),
                Som = som,
                Eom = eom,
                DurationTc = durationTc,
                BitRate = FormatBitRate(Get(general, "OverallBitRate")),
                DisplayAspect = ConvertAspect(Get(video, "DisplayAspectRatio")),
                SpecCheck = BuildSpecCheck(width, height)
            };
        }

        private string Get(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
                return "";

            if (!element.TryGetProperty(propertyName, out var value))
                return "";

            return value.GetString() ?? "";
        }

        private string BuildSpecCheck(string width, string height)
        {
            if (width == "1920" && height == "1080")
                return "HD";

            return "Unknown";
        }

        private string ConvertScanOrder(string scanOrder)
        {
            return scanOrder switch
            {
                "TFF" => "Top Field First",
                "BFF" => "Bottom Field First",
                _ => scanOrder
            };
        }

        private string ConvertAspect(string aspect)
        {
            if (aspect == "1.778")
                return "16:9";

            return aspect;
        }

        private string FormatBitRate(string bitRateText)
        {
            if (!long.TryParse(bitRateText, out long bitRate))
                return bitRateText;

            if (bitRate >= 1_000_000)
                return $"{bitRate / 1_000_000.0:0.###} Mb/s";

            if (bitRate >= 1_000)
                return $"{bitRate / 1_000.0:0.###} kb/s";

            return $"{bitRate} b/s";
        }

        private string BuildDurationTc(string frameCountText, string fpsNumText, string fpsDenText)
        {
            if (!int.TryParse(frameCountText, out int frameCount))
                return "";

            if (!int.TryParse(fpsNumText, out int fpsNum))
                return "";

            if (!int.TryParse(fpsDenText, out int fpsDen) || fpsDen == 0)
                return "";

            double fps = (double)fpsNum / fpsDen;
            int nominalFps = (int)Math.Round(fps);

            if (nominalFps <= 0)
                return "";

            int totalFrames = frameCount;
            int frames = totalFrames % nominalFps;
            int totalSeconds = totalFrames / nominalFps;
            int seconds = totalSeconds % 60;
            int totalMinutes = totalSeconds / 60;
            int minutes = totalMinutes % 60;
            int hours = totalMinutes / 60;

            string separator = Math.Abs(fps - 29.97) < 0.01 ? ";" : ":";

            return $"{hours:00}:{minutes:00}:{seconds:00}{separator}{frames:00}";
        }
    }
}