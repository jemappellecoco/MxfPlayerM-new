using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using MxfPlayer.Models;

namespace MxfPlayer.Services
{
    public class MediaInfoService
    {
        private const int MediaInfoTimeoutMs = 15000;
        private readonly string _mediaInfoPath;

        public MediaInfoService()
        {
            _mediaInfoPath = ResolveMediaInfoPath(AppConfigService.Load().MediaInfoPath);
        }

        public MediaInfoResult GetInfo(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_mediaInfoPath))
                throw new FileNotFoundException("MediaInfoPath 未設定，請在 config.json 設定 MediaInfo.exe 或 MediaInfo.dll 路徑");

            if (!File.Exists(_mediaInfoPath))
                throw new FileNotFoundException("找不到 MediaInfo", _mediaInfoPath);

            if (!File.Exists(filePath))
                throw new FileNotFoundException("找不到影片檔案", filePath);

            string output = ReadMediaInfoJson(filePath);

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
        private string ReadMediaInfoJson(string filePath)
        {
            if (string.Equals(Path.GetExtension(_mediaInfoPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                string? directDllOutput = TryReadMediaInfoJsonFromDll(_mediaInfoPath, filePath, out string? directDllError);
                if (!string.IsNullOrWhiteSpace(directDllOutput))
                    return directDllOutput;

                throw new Exception("MediaInfo.dll 沒有輸出 JSON：" + directDllError);
            }

            if (IsLikelyGuiMediaInfoExecutable(_mediaInfoPath))
                throw new Exception("MediaInfoPath 指到 GUI 版 MediaInfo.exe，但找不到可用的 MediaInfo.dll。請安裝完整 MediaInfo 或改指向 MediaInfo_CLI\\MediaInfo.exe");

            string? shellError;
            string? shellOutput = TryReadMediaInfoJsonFromShell(filePath, out shellError);
            if (!string.IsNullOrWhiteSpace(shellOutput))
                return shellOutput;

            string? executableError;
            string? output = TryReadMediaInfoJsonFromExecutable(filePath, out executableError);
            if (!string.IsNullOrWhiteSpace(output))
                return output;

            string? dllPath = FindMediaInfoDll();
            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                string? dllOutput = TryReadMediaInfoJsonFromDll(dllPath, filePath, out string? dllError);
                if (!string.IsNullOrWhiteSpace(dllOutput))
                    return dllOutput;

                executableError += " / " + dllError;
            }

            throw new Exception("MediaInfo 沒有輸出 JSON：" + shellError + " / " + executableError);
        }

        private string ResolveMediaInfoPath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                if (IsLikelyGuiMediaInfoExecutable(configuredPath))
                {
                    string? configuredDll = FindMediaInfoDllNear(configuredPath);
                    if (!string.IsNullOrWhiteSpace(configuredDll))
                        return configuredDll;
                }

                return configuredPath;
            }

            foreach (string candidate in GetDefaultMediaInfoCandidates())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return configuredPath;
        }

        private string[] GetDefaultMediaInfoCandidates()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            return new[]
            {
                Path.Combine(programFiles, "MediaInfo", "MediaInfo.dll"),
                Path.Combine(programFiles, "MediaInfo", "MediaInfo.exe"),
                Path.Combine(programFilesX86, "MediaInfo", "MediaInfo.dll"),
                Path.Combine(programFilesX86, "MediaInfo", "MediaInfo.exe"),
                @"C:\Tools\MediaInfo_CLI\MediaInfo.exe"
            };
        }

        private string? TryReadMediaInfoJsonFromExecutable(string filePath, out string? errorMessage)
        {
            errorMessage = null;

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

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(MediaInfoTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                errorMessage = "MediaInfo exe 執行逾時，可能是 GUI 版本未輸出 JSON";
                return null;
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(output))
                return output;

            errorMessage = string.IsNullOrWhiteSpace(error)
                ? "MediaInfo exe 沒有輸出內容"
                : error;

            return null;
        }

        private string? TryReadMediaInfoJsonFromShell(string filePath, out string? errorMessage)
        {
            errorMessage = null;

            string command = $"\"{EscapeCmdArgument(_mediaInfoPath)}\" --Output=JSON \"{EscapeCmdArgument(filePath)}\"";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);

            using var process = Process.Start(psi);
            if (process == null)
                throw new Exception("MediaInfo shell 啟動失敗");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(MediaInfoTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                errorMessage = "MediaInfo shell 執行逾時";
                return null;
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(output))
                return output;

            errorMessage = string.IsNullOrWhiteSpace(error)
                ? "MediaInfo shell 沒有輸出內容"
                : error;
            return null;
        }

        private string EscapeCmdArgument(string value)
        {
            return value.Replace("\"", "\\\"");
        }

        private string? FindMediaInfoDll()
        {
            return FindMediaInfoDllNear(_mediaInfoPath);
        }

        private string? FindMediaInfoDllNear(string mediaInfoPath)
        {
            string? directory = Path.GetDirectoryName(mediaInfoPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            string sameDirectoryDll = Path.Combine(directory, "MediaInfo.dll");
            if (File.Exists(sameDirectoryDll))
                return sameDirectoryDll;

            try
            {
                foreach (string candidate in Directory.EnumerateFiles(directory, "MediaInfo.dll", SearchOption.AllDirectories))
                    return candidate;
            }
            catch { }

            return null;
        }

        private bool IsLikelyGuiMediaInfoExecutable(string mediaInfoPath)
        {
            if (!string.Equals(Path.GetExtension(mediaInfoPath), ".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            string normalized = mediaInfoPath.ToLowerInvariant();
            return normalized.Contains("\\program files\\mediainfo\\") ||
                   normalized.Contains("\\program files (x86)\\mediainfo\\");
        }

        private string? TryReadMediaInfoJsonFromDll(string dllPath, string filePath, out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                return ReadMediaInfoJsonFromDll(dllPath, filePath);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        private string ReadMediaInfoJsonFromDll(string dllPath, string filePath)
        {
            nint library = NativeLibrary.Load(dllPath);
            nint handle = 0;

            try
            {
                var mediaInfoNew = GetExport<MediaInfoNewDelegate>(library, "MediaInfo_New");
                var mediaInfoDelete = GetExport<MediaInfoDeleteDelegate>(library, "MediaInfo_Delete");
                var mediaInfoOpen = GetExport<MediaInfoOpenDelegate>(library, "MediaInfo_Open");
                var mediaInfoClose = GetExport<MediaInfoCloseDelegate>(library, "MediaInfo_Close");
                var mediaInfoInform = GetExport<MediaInfoInformDelegate>(library, "MediaInfo_Inform");
                var mediaInfoOption = GetExport<MediaInfoOptionDelegate>(library, "MediaInfo_Option");

                handle = mediaInfoNew();
                if (handle == 0)
                    throw new Exception("MediaInfo.dll 初始化失敗");

                mediaInfoOption(handle, "Inform", "JSON");

                if (mediaInfoOpen(handle, filePath) == 0)
                    throw new Exception("MediaInfo.dll 無法開啟影片檔案");

                nint result = mediaInfoInform(handle, 0);
                string output = Marshal.PtrToStringUni(result) ?? "";
                mediaInfoClose(handle);
                mediaInfoDelete(handle);
                handle = 0;

                if (string.IsNullOrWhiteSpace(output))
                    throw new Exception("MediaInfo.dll 沒有輸出 JSON");

                return output;
            }
            finally
            {
                if (handle != 0)
                {
                    try
                    {
                        var mediaInfoClose = GetExport<MediaInfoCloseDelegate>(library, "MediaInfo_Close");
                        var mediaInfoDelete = GetExport<MediaInfoDeleteDelegate>(library, "MediaInfo_Delete");
                        mediaInfoClose(handle);
                        mediaInfoDelete(handle);
                    }
                    catch { }
                }

                NativeLibrary.Free(library);
            }
        }

        private T GetExport<T>(nint library, string name) where T : Delegate
        {
            nint address = NativeLibrary.GetExport(library, name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nint MediaInfoNewDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void MediaInfoDeleteDelegate(nint handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate nint MediaInfoOpenDelegate(nint handle, [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void MediaInfoCloseDelegate(nint handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nint MediaInfoInformDelegate(nint handle, nint reserved);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate nint MediaInfoOptionDelegate(
            nint handle,
            [MarshalAs(UnmanagedType.LPWStr)] string option,
            [MarshalAs(UnmanagedType.LPWStr)] string value);

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
            if (!long.TryParse(frameCountText, out long frameCount))
                return "";

            if (!int.TryParse(fpsNumText, out int fpsNum))
                return "";

            if (!int.TryParse(fpsDenText, out int fpsDen) || fpsDen == 0)
                return "";

            double fps = (double)fpsNum / fpsDen;
            int nominalFps = (int)Math.Round(fps);

            if (nominalFps <= 0)
                return "";

            bool isDropFrame = string.Equals(dropFrame, "True", StringComparison.OrdinalIgnoreCase);
            long timecodeFrameNumber = frameCount;

            if (isDropFrame)
            {
                int droppedLabelsPerMinute = Math.Max(0, (int)Math.Round(nominalFps * 0.0666666667));
                long framesPerMinute = (nominalFps * 60L) - droppedLabelsPerMinute;
                long framesPer10Minutes = (nominalFps * 600L) - (droppedLabelsPerMinute * 9L);

                if (droppedLabelsPerMinute > 0 && framesPer10Minutes > 0)
                {
                    long tenMinuteBlocks = frameCount / framesPer10Minutes;
                    long remainingFrames = frameCount % framesPer10Minutes;
                    long droppedLabels = droppedLabelsPerMinute * 9L * tenMinuteBlocks;

                    if (remainingFrames >= droppedLabelsPerMinute)
                        droppedLabels += droppedLabelsPerMinute * ((remainingFrames - droppedLabelsPerMinute) / framesPerMinute);

                    timecodeFrameNumber += droppedLabels;
                }
            }

            long hours = timecodeFrameNumber / (nominalFps * 3600L);
            timecodeFrameNumber %= nominalFps * 3600L;
            long minutes = timecodeFrameNumber / (nominalFps * 60L);
            timecodeFrameNumber %= nominalFps * 60L;
            long seconds = timecodeFrameNumber / nominalFps;
            long frames = timecodeFrameNumber % nominalFps;

            string separator =
                isDropFrame
                    ? ";"
                    : ":";

            return $"{hours:00}:{minutes:00}:{seconds:00}{separator}{frames:00}";
        }
    }
}
