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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--Output=JSON");
            psi.ArgumentList.Add(filePath);
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
            JsonElement audio = default;
            JsonElement timecode = default;
            foreach (var t in tracks.EnumerateArray())
            {
                string type = Get(t, "@type");

                if (type == "General")
                {
                    general = t;
                }
                else if (type == "Video")
                {
                    video = t;
                }
                else if (type == "Audio" && audio.ValueKind == JsonValueKind.Undefined)
                {
                    // 只抓第一軌 Audio，因為 A1~A8 都是 1152 kb/s
                    audio = t;
                }
                else if (type == "Other")
                {
                    if (t.TryGetProperty("TimeCode_LastFrame", out _))
                        timecode = t;
                }
            }

            string width = Get(video, "Width");
            string height = Get(video, "Height");
            string frameRate = Get(video, "FrameRate");
            string fpsNum = Get(video, "FrameRate_Num");
            string fpsDen = Get(video, "FrameRate_Den");
            string frameCount = Get(video, "FrameCount");

            string som = Get(video, "TimeCode_FirstFrame");
            string eom = Get(timecode, "TimeCode_LastFrame");
            string dropFrame = DetectDropFrame(video, timecode);
            string durationTc = BuildDurationTc(frameCount, fpsNum, fpsDen, dropFrame);
            string frameRateDisplay = FormatFrameRate(frameRate, fpsNum, fpsDen);
            var conformanceErrors = ExtractConformanceErrors(general);

            if (string.IsNullOrWhiteSpace(durationTc))
                durationTc = Get(general, "Duration");

            return new MediaInfoResult
            {
                FileName = Path.GetFileName(filePath),
                FullPath = filePath,
                Width = width,
                Height = height,
                FrameRate = frameRate,                 // 純數字，給 TryParse 用
                FrameRateValue = frameRate,            // 純數字
                FrameRateNum = fpsNum,
                FrameRateDen = fpsDen,
                FrameRateDisplay = frameRateDisplay,   // 顯示用：29.970 (30000/1001)
                DropFrame = dropFrame,
                AudioCount = Get(general, "AudioCount"),
                CommercialName = Get(general, "Format_Commercial_IfAny"),
                ScanType = Get(video, "ScanType"),
                ScanOrder = ConvertScanOrder(Get(video, "ScanOrder")),
                Som = som,
                Eom = eom,
                DurationTc = durationTc,
                //BitRate = FormatBitRate(Get(video, "BitRate")),
                VideoBitRate = FormatBitRate(Get(video, "BitRate")),
                AudioBitRate = FormatBitRate(Get(audio, "BitRate")),
                OverallBitRate = FormatBitRate(Get(general, "OverallBitRate")),
                VideoBitDepth = Get(video, "BitDepth"),
                AudioSamplingRate = Get(audio, "SamplingRate"),
                AudioBitDepth = Get(audio, "BitDepth"),
                TimeCodeMode = string.Equals(dropFrame, "True", StringComparison.OrdinalIgnoreCase)
                ? "Drop Frame"
                : "Non Drop Frame",
                DisplayAspect = ConvertAspect(Get(video, "DisplayAspectRatio")),
                ConformanceErrors = conformanceErrors,
              
            };
        }
        private string FormatFrameRate(string frameRate, string fpsNum, string fpsDen)
        {
            if (string.IsNullOrWhiteSpace(frameRate))
                return "";

            if (!string.IsNullOrWhiteSpace(fpsNum) && !string.IsNullOrWhiteSpace(fpsDen))
                return $"{frameRate} ({fpsNum}/{fpsDen})";

            return frameRate;
        }
        private string DetectDropFrame(JsonElement video, JsonElement timecode)
        {
            string tcSom = Get(timecode, "TimeCode_FirstFrame");
            if (!string.IsNullOrWhiteSpace(tcSom))
                return tcSom.Contains(';') ? "True" : "False";

            string videoSom = Get(video, "TimeCode_FirstFrame");
            if (!string.IsNullOrWhiteSpace(videoSom))
                return videoSom.Contains(';') ? "True" : "False";

            return "False";
        }
        private string Get(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
                return "";

            if (!element.TryGetProperty(propertyName, out var value))
                return "";

            return value.GetString() ?? "";
        }

        private System.Collections.Generic.List<string> ExtractConformanceErrors(JsonElement general)
        {
            var errors = new System.Collections.Generic.List<string>();

            if (general.ValueKind == JsonValueKind.Undefined ||
                !general.TryGetProperty("extra", out var extra))
            {
                return errors;
            }

            bool isTruncated = string.Equals(Get(extra, "IsTruncated"), "Yes", StringComparison.OrdinalIgnoreCase);

            if (extra.TryGetProperty("ConformanceErrors", out var conformanceErrors))
                AddConformanceErrorValues(conformanceErrors, errors);

            if (isTruncated && errors.Count == 0)
                errors.Add("MediaInfo 回報檔案不完整：IsTruncated=Yes");

            return errors;
        }

        private void AddConformanceErrorValues(JsonElement element, System.Collections.Generic.List<string> errors, string path = "")
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        string nextPath = string.IsNullOrWhiteSpace(path)
                            ? property.Name
                            : $"{path}.{property.Name}";
                        AddConformanceErrorValues(property.Value, errors, nextPath);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        AddConformanceErrorValues(item, errors, path);
                    break;

                case JsonValueKind.String:
                    string? message = element.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        errors.Add(string.IsNullOrWhiteSpace(path)
                            ? message
                            : $"{path}: {message}");
                    }
                    break;
            }
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

            if (aspect == "1.333")
                return "4:3";

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

        private string BuildDurationTc(string frameCountText, string fpsNumText, string fpsDenText, string dropFrame)
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

            string separator =
                string.Equals(dropFrame, "True", StringComparison.OrdinalIgnoreCase)
                    ? ";"
                    : ":";

            return $"{hours:00}:{minutes:00}:{seconds:00}{separator}{frames:00}";
        }
    }
}
