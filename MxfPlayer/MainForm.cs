using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MxfPlayer.Models;
using MxfPlayer.Services;
using MxfPlayer.Controllers;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Threading;
namespace MxfPlayer
{
    public class MainForm : Form
    {
        private bool _isDraggingTimeline = false;
        private bool _isUpdatingTimeline = false;
        private readonly PlayerService _player = new();
        private readonly FolderService _folder = new();
        private readonly MediaInfoService _mediaInfo = new();
        private readonly MediaSpecService _mediaSpec = new();
        private readonly Dictionary<string, MediaInfoResult> _mediaCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CachedMediaAnalysis> _analysisMemory = new(StringComparer.OrdinalIgnoreCase);
        private readonly AudioMixerService _audioMixer = new();
        private readonly PlaybackController _playbackController;
        private Dictionary<HotKeyAction, Keys> _hotKeyBindings = HotKeySettings.Load();
        private Panel _timelineLabelsPanel = null!;
        private readonly Random _rnd = new();
        private PictureBox _videoView = null!;
        private TextBox _txtPath = null!;
        private DataGridView _gridFiles = null!;
        private RichTextBox _txtInfo = null!;
        private Label _lblCurrentFile = null!;
        private TextBox _lblNow = null!;
        private Label _lblStart = null!;
        private Label _lblDur = null!;
        private Label _lblRemain = null!;
        private Button _btnInlineMeters = null!;
        private Label _lblFileCount = null!;
        private Label _lblTotalSize = null!;
        private TrackBar _timeline = null!;
        private System.Windows.Forms.Timer _meterTimer = null!;
        private Image? _displayedVideoFrame;
        private long _displayedVideoFrameIndex = -1;
        private readonly List<Panel> _meterBars = new();
        private readonly List<CheckBox> _channelChecks = new();
        private TableLayoutPanel _videoAndMetersLayout = null!;
        private Control _metersPanel = null!;
        private Form? _metersWindow;
        private bool _isStartingPlayback = false;
        private bool _isEditingNowTimecode = false;
        private bool _isBuffering = false;
        private MediaInfoResult? _currentMediaInfo;
        private readonly AudioMeterScaleService _meterScale = new();
        private int _meterAreaHeight = 0;
        private int _meterUpdateElapsedMs = 0;
        private int _timelineUpdateElapsedMs = 0;
        private bool _isFrameStepping = false;
        private bool _isBoundarySeeking = false;
        private int _pendingFrameStepDelta = 0;
        private bool _areInlineMetersVisible = true;
        private const int MeterUpdateIntervalMs = 100;
        private const int TimelineUpdateIntervalMs = 100;
        private const int MainMetersWidth = 170;
        private const int PlaybackPrebufferFrames = 120;
        private const int PlaybackPrebufferTimeoutMs = 30000;
        public MainForm()
        {
            Text = "Offline xPlayer";
            Width = 1680;
            Height = 930;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1400, 820);
            BackColor = Color.FromArgb(45, 48, 52);
            ForeColor = Color.White;
            KeyPreview = true;

            InitUI();
            ConfigureFileDrop(this);
            InitTimer();
            _playbackController = new PlaybackController(_player, _meterTimer, ResetMeters);
            this.FormClosing += (s, e) =>
            {
                _player.Dispose();
            };
        }

        private void ConfigureFileDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += OnFileDragEnter;
            control.DragDrop += OnFileDragDrop;

            foreach (Control child in control.Controls)
                ConfigureFileDrop(child);
        }

        private void OnFileDragEnter(object? sender, DragEventArgs e)
        {
            e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private async void OnFileDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                return;

            try
            {
                await LoadDroppedFilesAsync(paths);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拖放檔案失敗：{ex.Message}");
            }
        }
        private void InitUI()
        {
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(45, 48, 52),
                SplitterWidth = 6
            };
            Controls.Add(mainSplit);

            BuildMenu();

            BuildLeftPlayerArea(mainSplit.Panel1);
            BuildRightBrowserArea(mainSplit.Panel2);

            Load += (_, _) =>
            {
                mainSplit.Panel1MinSize = 760;
                mainSplit.Panel2MinSize = 450;
                int safeDistance = ClientSize.Width - mainSplit.Panel2MinSize - 20;
                mainSplit.SplitterDistance = Math.Max(mainSplit.Panel1MinSize, Math.Min(880, safeDistance));
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_lblNow != null && _lblNow.Focused)
            {
                Keys focusedKeyCode = keyData & Keys.KeyCode;
                if (focusedKeyCode == Keys.Enter || focusedKeyCode == Keys.Return)
                {
                    _ = SeekFromNowInputAsync();
                    return true;
                }

                if (ShouldHandleHotKey(keyData) && TryExecuteHotKey(keyData))
                    return true;

                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (ShouldHandleHotKey(keyData) && TryExecuteHotKey(keyData))
                return true;

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessKeyPreview(ref Message m)
        {
            const int wmKeyDown = 0x0100;
            const int wmSysKeyDown = 0x0104;

            if (m.Msg is wmKeyDown or wmSysKeyDown)
            {
                Keys keyData = (Keys)(int)m.WParam | ModifierKeys;
                if (ShouldHandleHotKey(keyData) && TryExecuteHotKey(keyData))
                    return true;
            }

            return base.ProcessKeyPreview(ref m);
        }

        private bool TryExecuteHotKey(Keys keyData)
        {
            Keys shortcut = HotKeySettings.Normalize(keyData);
            foreach ((HotKeyAction action, Keys binding) in _hotKeyBindings)
            {
                if (binding != shortcut)
                    continue;

                ExecuteHotKeyAction(action);
                return true;
            }

            return false;
        }

        private bool ShouldHandleHotKey(Keys keyData)
        {
            if (_lblNow != null && _lblNow.Focused)
                return !_isEditingNowTimecode;

            return !IsPlainTextEntryKey(keyData);
        }

        private bool IsPlainTextEntryKey(Keys keyData)
        {
            if (ActiveControl is not TextBoxBase textBox || textBox == _lblNow)
                return false;

            Keys modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
            if (modifiers != Keys.None)
                return false;

            Keys keyCode = keyData & Keys.KeyCode;
            return keyCode is >= Keys.A and <= Keys.Z
                || keyCode is >= Keys.D0 and <= Keys.D9
                || keyCode is Keys.Space
                || keyCode is Keys.OemMinus
                || keyCode is Keys.Oemplus
                || keyCode is Keys.Oemcomma
                || keyCode is Keys.OemPeriod
                || keyCode is Keys.OemQuestion
                || keyCode is Keys.OemSemicolon
                || keyCode is Keys.OemQuotes
                || keyCode is Keys.OemOpenBrackets
                || keyCode is Keys.OemCloseBrackets
                || keyCode is Keys.OemPipe
                || keyCode is Keys.Oemtilde;
        }

        private void ExecuteHotKeyAction(HotKeyAction action)
        {
            switch (action)
            {
                case HotKeyAction.OpenFiles:
                    _ = OpenFilesFromMenuAsync();
                    break;
                case HotKeyAction.OpenFolder:
                    OnSelectFolder(this, EventArgs.Empty);
                    break;
                case HotKeyAction.Quit:
                    Close();
                    break;
                case HotKeyAction.Play:
                    HandlePlay();
                    break;
                case HotKeyAction.Pause:
                    HandlePause();
                    break;
                case HotKeyAction.PlayPause:
                    if (_meterTimer.Enabled)
                        HandlePause();
                    else
                        HandlePlay();
                    break;
                case HotKeyAction.StepBackward:
                    StepFrameByKeyboard(-1);
                    break;
                case HotKeyAction.StepForward:
                    StepFrameByKeyboard(1);
                    break;
                case HotKeyAction.MoveFirst:
                    HandleMoveFirst();
                    break;
                case HotKeyAction.MoveLast:
                    HandleMoveLast();
                    break;
                case HotKeyAction.Rewind:
                    HandleMoveBackForward();
                    break;
                case HotKeyAction.FastForward:
                    HandleMoveFastForward();
                    break;
                case HotKeyAction.JumpBackward10:
                    _ = HandleJump(-10);
                    break;
                case HotKeyAction.JumpForward10:
                    _ = HandleJump(10);
                    break;
                case HotKeyAction.JumpBackward5:
                    _ = HandleJump(-5);
                    break;
                case HotKeyAction.JumpForward5:
                    _ = HandleJump(5);
                    break;
                case HotKeyAction.ToggleMetersWindow:
                    ToggleMetersWindow();
                    break;
                case HotKeyAction.ToggleInlineMeters:
                    ToggleInlineMeters();
                    break;
                case HotKeyAction.ToggleChannel1:
                case HotKeyAction.ToggleChannel2:
                case HotKeyAction.ToggleChannel3:
                case HotKeyAction.ToggleChannel4:
                case HotKeyAction.ToggleChannel5:
                case HotKeyAction.ToggleChannel6:
                case HotKeyAction.ToggleChannel7:
                case HotKeyAction.ToggleChannel8:
                    ToggleAudioChannel((int)action - (int)HotKeyAction.ToggleChannel1);
                    break;
            }
        }

        private void ShowMediaInfo(MediaInfoResult info, CachedMediaAnalysis? analysis = null)
        {
            _currentMediaInfo = info;

            UpdateTimeLabels(info);

            BeginInvoke(new Action(() =>
            {
                RefreshTimelineTicks(info);
            }));
            ShowColoredMediaInfo(info, analysis);
        }

        private void ShowColoredMediaInfo(MediaInfoResult info, CachedMediaAnalysis? analysis)
        {
            string specStatus = analysis == null
                ? "未檢查"
                : GetDisplaySpecCheck(analysis);
            var displayErrors = GetDisplayErrors(analysis);
            string errorText = displayErrors.Count > 0
                ? string.Join(Environment.NewLine + "                    ", displayErrors)
                : "無";

            _txtInfo.Clear();
            AppendMediaInfoLine("寬度", $"{info.Width} pixels", HasSpecError(analysis, "寬度錯誤", "影格尺寸錯誤"));
            AppendMediaInfoLine("高度", $"{info.Height} pixels", HasSpecError(analysis, "高度錯誤", "影格尺寸錯誤"));
            AppendMediaInfoLine("影格率", $"{info.FrameRateDisplay} FPS", HasSpecError(analysis, "影格速率錯誤"));
            AppendMediaInfoLine("Drop Frame", info.DropFrame, HasSpecError(analysis, "時間碼模式錯誤"));
            AppendMediaInfoLine("音訊聲道", info.AudioCount, HasSpecError(analysis, "音頻通道錯誤"));
            AppendMediaInfoLine("格式名稱", info.CommercialName, HasSpecError(analysis, "格式錯誤"));
            AppendMediaInfoLine("掃描方式", info.ScanType, false);
            AppendMediaInfoLine("掃描順序", info.ScanOrder, HasSpecError(analysis, "場次順序錯誤"));
            AppendMediaInfoLine("SOM", info.Som, false);
            AppendMediaInfoLine("EOM", info.Eom, false);
            AppendMediaInfoLine("長度", info.DurationTc, false);
            AppendMediaInfoLine("影片位元率", info.VideoBitRate, HasSpecError(analysis, "影片比特率錯誤"));
            AppendMediaInfoLine("音訊單軌位元率", info.AudioBitRate, false);
            AppendMediaInfoLine("整體位元率", info.OverallBitRate, false);
            AppendMediaInfoLine("顯示比例", info.DisplayAspect, HasSpecError(analysis, "長寬比錯誤"));
            AppendMediaInfoLine("格式檢查", specStatus, IsDisplaySpecError(analysis));
            AppendMediaInfoLine("錯誤原因", errorText, displayErrors.Count > 0);
            AppendMediaInfoLine("檔案名稱", info.FileName, false);
            AppendMediaInfoLine("完整路徑", info.FullPath, false);
        }

        private string GetDisplaySpecCheck(CachedMediaAnalysis analysis)
        {
            if (!analysis.SpecIsPass)
                return "Error";

            if (analysis.DecodeCheckStatus == "Checking")
                return "檢查中";

            if (analysis.DecodeCheckStatus == "Failed")
                return "Error";

            return analysis.SpecType;
        }

        private bool IsDisplaySpecError(CachedMediaAnalysis? analysis)
        {
            return analysis != null &&
                   (!analysis.SpecIsPass || analysis.DecodeCheckStatus == "Failed");
        }

        private List<string> GetDisplayErrors(CachedMediaAnalysis? analysis)
        {
            if (analysis == null)
                return new List<string>();

            var errors = new List<string>(analysis.SpecErrors);
            if (analysis.DecodeCheckStatus == "Failed" && !string.IsNullOrWhiteSpace(analysis.DecodeCheckError))
                errors.Add(analysis.DecodeCheckError);
            else if (analysis.DecodeCheckStatus == "Checking")
                errors.Add("影片完整性檢查中");

            return errors;
        }

        private void AppendMediaInfoLine(string label, string value, bool isError)
        {
            _txtInfo.SelectionColor = isError ? Color.Red : Color.White;
            _txtInfo.AppendText($"{label}:".PadRight(20) + value + Environment.NewLine);
            _txtInfo.SelectionColor = Color.White;
        }

        private bool HasSpecError(CachedMediaAnalysis? analysis, params string[] keywords)
        {
            var errors = GetDisplayErrors(analysis);
            if (errors.Count == 0)
                return false;

            foreach (string error in errors)
            {
                foreach (string keyword in keywords)
                {
                    if (error.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
        private bool TryGetSelectedMediaFile(out MediaFile? file)
        {
            file = null;

            if (_gridFiles.CurrentRow == null) return false;
            if (_gridFiles.CurrentRow.Tag is not MediaFile mediaFile) return false;

            file = mediaFile;
            return true;
        }

        private void LoadAndShowMedia(string filePath)
        {
            var analysis = GetOrAnalyzeMedia(filePath);

            ShowMediaInfo(analysis.Info, analysis);
        }

        private CachedMediaAnalysis GetOrAnalyzeMedia(string filePath)
        {
            if (_analysisMemory.TryGetValue(filePath, out var memoryCached) &&
                IsAnalysisValid(filePath, memoryCached))
            {
                _mediaCache[filePath] = memoryCached.Info;
                return memoryCached;
            }

            var info = _mediaInfo.GetInfo(filePath);
            var check = _mediaSpec.CheckWeiLaiSpec(info, includeDecodeCheck: false);
            var fileInfo = new FileInfo(filePath);

            var analysis = new CachedMediaAnalysis
            {
                FullPath = filePath,
                FileLength = fileInfo.Length,
                CreationTimeUtc = fileInfo.CreationTimeUtc,
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                Info = info,
                SpecType = _mediaSpec.GetSpecType(info),
                SpecIsPass = check.IsPass,
                SpecErrors = check.Errors,
                DecodeCheckStatus = "NotChecked",
                DecodeCheckError = ""
            };

            _mediaCache[filePath] = info;
            _analysisMemory[filePath] = analysis;
            return analysis;
        }

        private bool IsAnalysisValid(string filePath, CachedMediaAnalysis analysis)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var fileInfo = new FileInfo(filePath);
            return analysis.FileLength == fileInfo.Length &&
                   analysis.CreationTimeUtc == fileInfo.CreationTimeUtc &&
                   analysis.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc;
        }

        private async Task<bool> StartPlaybackForFile(MediaFile file, long startTimeMs = 0, bool showLoading = true)
        {
            if (_isStartingPlayback) return false;
            _isStartingPlayback = true;

            try
            {
                int audioCount = 8;
                double fps = 0;

                if (!_mediaCache.ContainsKey(file.FullPath))
                    GetOrAnalyzeMedia(file.FullPath);

                if (_mediaCache.TryGetValue(file.FullPath, out var info))
                {
                    if (int.TryParse(info.AudioCount, out int parsedAudioCount) && parsedAudioCount > 0)
                        audioCount = parsedAudioCount;
                }

                fps = GetSelectedFps();
                if (fps <= 0)
                {
                    MessageBox.Show("讀不到影片 FPS，無法播放。");
                    return false;
                }

                for (int i = 0; i < 8; i++)
                {
                    _player.ChannelMask[i] = _channelChecks[i].Checked;
                }

                int sampleRate = GetAudioSamplingRate(file.FullPath);

                if (showLoading)
                {
                    using (var loading = new LoadingForm(this, file.FileName))
                    {
                        loading.TopMost = true;
                        loading.Show();
                        loading.Refresh();

                        try
                        {
                            _displayedVideoFrameIndex = -1;
                            await _player.StartAudioBridge(file.FullPath, audioCount, startTimeMs, 1.0f, fps, sampleRate);
                            await _player.WaitForVideoBufferAheadAsync(
                                _player.CurrentFrameIndex,
                                GetPlaybackPrebufferFrames(1.0f),
                                PlaybackPrebufferTimeoutMs);
                            await _player.WaitForAudioBufferAsync(
                                _player.CurrentFrameIndex,
                                fps,
                                1.0f,
                                PlaybackPrebufferTimeoutMs);
                            UpdateVideoFrame();
                        }
                        finally
                        {
                            loading.Close();
                        }
                    }
                }
                else
                {
                    _displayedVideoFrameIndex = -1;
                    await _player.StartAudioBridge(file.FullPath, audioCount, startTimeMs, 1.0f, fps, sampleRate);
                    await _player.WaitForVideoBufferAheadAsync(
                        _player.CurrentFrameIndex,
                        GetPlaybackPrebufferFrames(1.0f),
                        PlaybackPrebufferTimeoutMs);
                    await _player.WaitForAudioBufferAsync(
                        _player.CurrentFrameIndex,
                        fps,
                        1.0f,
                        PlaybackPrebufferTimeoutMs);
                    UpdateVideoFrame();
                }

                ResetUiUpdateThrottle();
                return true;
            }
            finally
            {
                _isStartingPlayback = false;
            }
        }

        private async Task PlaySelectedFileAsync(MediaFile file, bool resetRateToNormal = false)
        {
            ReleaseNowTimecodeInputFocus();

            long startTimeMs = _player.CurrentPath == file.FullPath
                ? _playbackController.GetCurrentTime()
                : 0;

            bool needsStart = _player.CurrentPath != file.FullPath || !_player.IsAudioReady;
            if (needsStart)
            {
                _playbackController.Pause();
                ResetUiUpdateThrottle();
                bool started = await StartPlaybackForFile(file, startTimeMs);
                if (!started) return;
            }

            ResetUiUpdateThrottle();
            if (resetRateToNormal)
            {
                _lblRate.Text = "1x";
                await _playbackController.PlayNormal();
            }
            else
            {
                await _playbackController.Play();
            }

            _lblNow.ForeColor = Color.Orange;
            UpdateTimelineUI(-1);
        }
        private double GetSelectedFps()
        {
            if (!TryGetSelectedMediaFile(out var file) ||
                file == null ||
                !_mediaCache.TryGetValue(file.FullPath, out var info))
            {
                return 0;
            }

            return GetRealFpsFromInfo(info);
        }
   
        private void UpdateTimelineUI()
        {
           
            if (_isDraggingTimeline) return;

            _isUpdatingTimeline = true;

            try
            {
                long current = _playbackController.GetCurrentTime();
                long length = _playbackController.GetLength();

                if (length > 0)
                {
               
                    int timelineValue = Math.Clamp(_playbackController.GetTimelineValue(_timeline.Maximum), _timeline.Minimum, _timeline.Maximum);
                    if (_timeline.Value != timelineValue)
                        _timeline.Value = timelineValue;


                    double fps = GetSelectedFps();
                    if (fps <= 0) return;

                    bool dropFrame = IsSelectedDropFrame();
                    long somFrame = 0;
                    if (TryGetSelectedMediaFile(out var file) &&
                        file != null &&
                        _mediaCache.TryGetValue(file.FullPath, out var info))
                    {
                        somFrame = TimecodeToFrame(info.Som, fps, dropFrame);
                    }


                    if (!_isEditingNowTimecode)
                        SetNowTimecodeText(FrameToTimecode(somFrame + _player.CurrentFrameIndex, fps, dropFrame), dropFrame);

                    long lastFrame = PlayerService.FrameFromTimeMs(length, fps);
                    long remainFrames = Math.Max(0, lastFrame - _player.CurrentFrameIndex);
                    _lblRemain.Text = $"REM {FrameToTimecode(remainFrames, fps, dropFrame)}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI Update Error] {ex.Message}");
            }
            finally
            {
                _isUpdatingTimeline = false;
            }
        }
     
        private void LoadFolderToGrid(string folderPath)
        {
            _txtPath.Text = folderPath;

            var files = _folder.LoadFolder(folderPath);
            PopulateGrid(files, "正在檢查資料夾影片");

            double totalGB = CalculateTotalSizeGB(files);
            UpdateRightSummary(files.Count, totalGB);
            ClearSelectedMediaInfo();
        }

        private void ClearSelectedMediaInfo()
        {
            _gridFiles.ClearSelection();
            _gridFiles.CurrentCell = null;
            _currentMediaInfo = null;
            _txtInfo.Clear();
            _timelineLabelsPanel.Controls.Clear();
        }

        private void PopulateGrid(List<MediaFile> files, string loadingTitle = "正在檢查影片")
        {
            _gridFiles.Rows.Clear();

            AddMediaFilesWithProgress(files, loadingTitle);
        }

        private void AddMediaFilesWithProgress(List<MediaFile> files, string loadingTitle)
        {
            if (files.Count == 0)
                return;

            using var loading = new LoadingForm(this, "");
            loading.SetProgressMode(files.Count);
            loading.Show();
            loading.Refresh();

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                loading.UpdateProgress(loadingTitle, file.FileName, i + 1, files.Count);
                AddMediaFileRow(file);
                Application.DoEvents();
            }
        }

        private int AddMediaFileRow(MediaFile file)
        {
            string som = "00:00:00;00";
            string eom = "00:00:00;00";
            string duration = "00:00:00;00";
            string specCheck = "Error";
            string specErrorText = "";

            try
            {
                var analysis = GetOrAnalyzeMedia(file.FullPath);
                var info = analysis.Info;

                som = string.IsNullOrWhiteSpace(info.Som) ? "00:00:00;00" : info.Som;
                eom = string.IsNullOrWhiteSpace(info.Eom) ? "00:00:00;00" : info.Eom;
                duration = string.IsNullOrWhiteSpace(info.DurationTc) ? "00:00:00;00" : info.DurationTc;

                specCheck = GetDisplaySpecCheck(analysis);
                specErrorText = string.Join(Environment.NewLine, GetDisplayErrors(analysis));

                if (IsDisplaySpecError(analysis))
                {
                    // 先印到 Output 視窗，方便你 debug
                    System.Diagnostics.Debug.WriteLine($"[Spec Error] {file.FileName}");
                    System.Diagnostics.Debug.WriteLine(specErrorText);
                }
            }
            catch (Exception ex)
            {
                specCheck = "Error";
                specErrorText = $"讀取 MediaInfo 失敗：{ex.Message}";

                System.Diagnostics.Debug.WriteLine($"[MediaInfo Error] {file.FileName}");
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }

            int rowIndex = _gridFiles.Rows.Add(
                file.FileName,
                som,
                eom,
                duration,
                Path.GetExtension(file.FileName),
                specCheck
            );

            var row = _gridFiles.Rows[rowIndex];
            row.Tag = file;

            // 把錯誤原因放在格式檢查欄的 Tooltip
            row.Cells[5].ToolTipText = specErrorText;

            if (_analysisMemory.TryGetValue(file.FullPath, out var rowAnalysis) && IsDisplaySpecError(rowAnalysis))
            {
                row.Cells[5].Style.ForeColor = Color.Red;
                row.Cells[5].Style.Font = new Font(_gridFiles.Font, FontStyle.Bold);
            }
            else if (_analysisMemory.TryGetValue(file.FullPath, out rowAnalysis) &&
                     rowAnalysis.DecodeCheckStatus == "Checking")
            {
                row.Cells[5].Style.ForeColor = Color.Orange;
                row.Cells[5].Style.Font = new Font(_gridFiles.Font, FontStyle.Bold);
            }
            else
            {
                row.Cells[5].Style.ForeColor = Color.White;
                row.Cells[5].Style.Font = new Font(_gridFiles.Font, FontStyle.Regular);
            }

            return rowIndex;
        }

        private double CalculateTotalSizeGB(List<MediaFile> files)
        {
            long totalBytes = 0;

            foreach (var file in files)
            {
                try
                {
                    totalBytes += new FileInfo(file.FullPath).Length;
                }
                catch
                {
                }
            }

            return totalBytes / 1024.0 / 1024.0 / 1024.0;
        }

        private async Task LoadDroppedFilesAsync(string[] paths)
        {
            var droppedFiles = GetDroppedMediaFiles(paths);
            if (droppedFiles.Count == 0)
                return;

            string? firstPath = null;

            var filesToAdd = new List<MediaFile>();

            foreach (var file in droppedFiles)
            {
                if (firstPath == null)
                    firstPath = file.FullPath;

                if (SelectFileInGrid(file.FullPath))
                    continue;

                filesToAdd.Add(file);
            }

            AddMediaFilesWithProgress(filesToAdd, "正在檢查拖放影片");

            var allFiles = GetGridMediaFiles();
            UpdateRightSummary(allFiles.Count, CalculateTotalSizeGB(allFiles));

            if (firstPath == null || !SelectFileInGrid(firstPath))
                return;

            await Task.CompletedTask;
        }

        private List<MediaFile> GetDroppedMediaFiles(string[] paths)
        {
            var files = new List<MediaFile>();

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (Directory.Exists(path))
                {
                    files.AddRange(_folder.LoadFolder(path));
                    continue;
                }

                if (!File.Exists(path))
                    continue;

                files.Add(new MediaFile
                {
                    FileName = Path.GetFileName(path),
                    FullPath = path
                });
            }

            return files
                .GroupBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private List<MediaFile> GetGridMediaFiles()
        {
            var files = new List<MediaFile>();

            foreach (DataGridViewRow row in _gridFiles.Rows)
            {
                if (row.Tag is MediaFile file)
                    files.Add(file);
            }

            return files;
        }

        private void UpdateTimeLabels(MediaInfoResult info)
        {
            bool dropFrame = IsDropFrame(info);
            _lblCurrentFile.Text = info.FileName;
            _lblStart.Text = $"START {info.Som}";
            SetNowTimecodeText(info.Som, dropFrame);
            _lblDur.Text = $"DUR {info.DurationTc}";
            _lblRemain.Text = $"REM {info.Eom}";
        }
        private void InitTimer()
        {
            _meterTimer = new System.Windows.Forms.Timer();
     
            _meterTimer.Interval = 33;
            _meterTimer.Tick += async (_, _) =>
            {
                if (_isBuffering)
                    return;

                _player.AdvanceVideo(_meterTimer.Interval);

                UpdateVideoFrame();

                _meterUpdateElapsedMs += _meterTimer.Interval;
                if (_meterUpdateElapsedMs >= MeterUpdateIntervalMs)
                {
                    _meterUpdateElapsedMs = 0;
                    UpdateMetersFromAudioLevel();
                }

                _timelineUpdateElapsedMs += _meterTimer.Interval;
                if (_timelineUpdateElapsedMs >= TimelineUpdateIntervalMs)
                {
                    _timelineUpdateElapsedMs = 0;
                    UpdateTimelineFromPlayer();
                }
            };
        }

        private void ResetUiUpdateThrottle()
        {
            _meterUpdateElapsedMs = 0;
            _timelineUpdateElapsedMs = 0;
        }

        private void UpdateVideoFrame()
        {
            long frameIndex = _player.GetDisplayFrameIndex();

            if (frameIndex < 0)
                return;

            if (frameIndex == _displayedVideoFrameIndex)
                return;

            var nextFrame = _player.CreateDisplayVideoFrameSnapshot(out var snapshotFrameIndex);
            if (nextFrame == null)
                return;

            var previousFrame = _displayedVideoFrame;

            _displayedVideoFrame = nextFrame;
            _displayedVideoFrameIndex = snapshotFrameIndex;
            _videoView.Image = nextFrame;

            previousFrame?.Dispose();
        }

        private void BuildMenu()
        {
            var menu = new MenuStrip
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(36, 39, 43),
                ForeColor = Color.White,
                Renderer = new ToolStripProfessionalRenderer(new DarkColorTable())
            };

            var fileMenu = CreateTopMenu("File");
            fileMenu.DropDownItems.Add(CreateMenuItem("Open Files...", MenuIconKind.FileOpen, async (_, _) => await OpenFilesFromMenuAsync()));
            fileMenu.DropDownItems.Add(CreateMenuItem("Open Folder...", MenuIconKind.FolderOpen, OnSelectFolder));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateMenuItem("Quit", MenuIconKind.Power, (_, _) => Close()));

            var playbackMenu = CreateTopMenu("Playback");
            playbackMenu.DropDownItems.Add(CreateMenuItem("Play", MenuIconKind.Play, (_, _) => HandlePlay()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Pause", MenuIconKind.Pause, (_, _) => HandlePause()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Negative Jog", MenuIconKind.NegativeJog, (_, _) => HandleNegativeLog()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Positive Jog", MenuIconKind.PositiveJog, (_, _) => HandlePositiveLog()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Rewind", MenuIconKind.Rewind, (_, _) => HandleMoveBackForward()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Fast Forward", MenuIconKind.FastForward, (_, _) => HandleMoveFastForward()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Move First", MenuIconKind.MoveFirst, (_, _) => HandleMoveFirst()));
            playbackMenu.DropDownItems.Add(CreateMenuItem("Move Last", MenuIconKind.MoveLast, (_, _) => HandleMoveLast()));

            var toolsMenu = CreateTopMenu("Tools");
            toolsMenu.DropDownItems.Add(CreateMenuItem("HotKey...", MenuIconKind.Keyboard, OnHotKeyMenuClicked));

            menu.Items.Add(fileMenu);
            menu.Items.Add(playbackMenu);
            menu.Items.Add(toolsMenu);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private ToolStripMenuItem CreateTopMenu(string text)
        {
            return new ToolStripMenuItem(text)
            {
                ForeColor = Color.White
            };
        }

        private ToolStripMenuItem CreateMenuItem(string text, MenuIconKind iconKind, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text)
            {
                ForeColor = Color.White,
                Image = CreateMenuIcon(iconKind),
                ImageScaling = ToolStripItemImageScaling.None
            };
            item.Click += onClick;
            return item;
        }

        private async Task OpenFilesFromMenuAsync()
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Media Files|*.mxf;*.MXF;*.mp4;*.MP4;*.mov;*.MOV;*.avi;*.AVI;*.mkv;*.MKV|All Files|*.*",
                Multiselect = true,
                Title = "Open Files"
            };

            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            await LoadDroppedFilesAsync(dialog.FileNames);
        }

        private static Bitmap CreateMenuIcon(MenuIconKind kind)
        {
            var bitmap = new Bitmap(18, 18);
            Color accent = kind == MenuIconKind.Play ? Color.FromArgb(235, 124, 24) : Color.FromArgb(185, 198, 202);

            using var g = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(accent);
            using var pen = new Pen(accent, 2f);
            g.Clear(Color.Transparent);

            switch (kind)
            {
                case MenuIconKind.FileOpen:
                    g.DrawRectangle(pen, 3, 4, 12, 12);
                    g.DrawLine(pen, 3, 7, 7, 7);
                    g.DrawLine(pen, 7, 7, 7, 4);
                    break;
                case MenuIconKind.FolderOpen:
                    g.DrawLine(pen, 2, 6, 7, 6);
                    g.DrawLine(pen, 7, 6, 9, 8);
                    g.DrawLine(pen, 9, 8, 16, 8);
                    g.DrawRectangle(pen, 2, 8, 14, 9);
                    break;
                case MenuIconKind.Power:
                    g.DrawArc(pen, 3, 5, 12, 12, 35, 290);
                    g.DrawLine(pen, 9, 2, 9, 9);
                    break;
                case MenuIconKind.Play:
                    g.FillPolygon(brush, new[] { new Point(5, 3), new Point(15, 9), new Point(5, 15) });
                    break;
                case MenuIconKind.Pause:
                    g.FillRectangle(brush, 5, 4, 4, 12);
                    g.FillRectangle(brush, 11, 4, 4, 12);
                    break;
                case MenuIconKind.NegativeJog:
                    g.FillPolygon(brush, new[] { new Point(13, 3), new Point(5, 9), new Point(13, 15) });
                    g.FillRectangle(brush, 3, 3, 2, 12);
                    break;
                case MenuIconKind.PositiveJog:
                    g.FillPolygon(brush, new[] { new Point(5, 3), new Point(13, 9), new Point(5, 15) });
                    g.FillRectangle(brush, 15, 3, 2, 12);
                    break;
                case MenuIconKind.Rewind:
                    g.FillPolygon(brush, new[] { new Point(9, 3), new Point(3, 9), new Point(9, 15) });
                    g.FillPolygon(brush, new[] { new Point(16, 3), new Point(10, 9), new Point(16, 15) });
                    break;
                case MenuIconKind.FastForward:
                    g.FillPolygon(brush, new[] { new Point(2, 3), new Point(8, 9), new Point(2, 15) });
                    g.FillPolygon(brush, new[] { new Point(9, 3), new Point(15, 9), new Point(9, 15) });
                    break;
                case MenuIconKind.MoveFirst:
                    g.FillRectangle(brush, 3, 3, 2, 12);
                    g.FillPolygon(brush, new[] { new Point(15, 3), new Point(6, 9), new Point(15, 15) });
                    break;
                case MenuIconKind.MoveLast:
                    g.FillPolygon(brush, new[] { new Point(3, 3), new Point(12, 9), new Point(3, 15) });
                    g.FillRectangle(brush, 14, 3, 2, 12);
                    break;
                case MenuIconKind.Keyboard:
                    g.DrawRectangle(pen, 2, 5, 14, 9);
                    using (var keyBrush = new SolidBrush(accent))
                    {
                        for (int y = 8; y <= 11; y += 3)
                        {
                            for (int x = 5; x <= 13; x += 4)
                                g.FillRectangle(keyBrush, x, y, 1, 1);
                        }
                    }
                    break;
            }

            return bitmap;
        }

        private void OnHotKeyMenuClicked(object? sender, EventArgs e)
        {
            using var dialog = new HotKeySettingsForm(_hotKeyBindings);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _hotKeyBindings = HotKeySettings.CloneBindings(dialog.Bindings);
            HotKeySettings.Save(_hotKeyBindings);
            _hotKeyBindings = HotKeySettings.Load();
        }

        private void BuildLeftPlayerArea(Control parent)
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Color.FromArgb(45, 48, 52),
                Padding = new Padding(6)
            };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            parent.Controls.Add(outer);

            outer.Controls.Add(BuildPlayerTopBar(), 0, 0);

            var videoWrap = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67),
                Padding = new Padding(6)
            };
            outer.Controls.Add(videoWrap, 0, 1);

            _videoAndMetersLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(58, 62, 67)
            };
            _videoAndMetersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _videoAndMetersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MainMetersWidth));
            videoWrap.Controls.Add(_videoAndMetersLayout);

            _videoView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Margin = new Padding(0),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            _videoAndMetersLayout.Controls.Add(_videoView, 0, 0);

            _metersPanel = BuildMetersPanel();
            _videoAndMetersLayout.Controls.Add(_metersPanel, 1, 0);

            outer.Controls.Add(BuildPlaybackBar(), 0, 2);
        }

        private Control BuildPlayerTopBar()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 6),
                BackColor = Color.FromArgb(58, 62, 67)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                BackColor = Color.FromArgb(58, 62, 67),
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            // 檔名區會吃掉剩餘空間
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // 其他時間欄位固定寬度，縮小時比較不會被切掉
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); // START
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); // NOW
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); // DUR
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); // REM
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));  // meters toggle

            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _lblCurrentFile = new Label
            {
                Text = "尚未選擇檔案",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Regular),
                AutoEllipsis = true,
                Margin = new Padding(0, 0, 8, 0)
            };

            _lblStart = new Label
            {
                Text = "START 00:00:00:00",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.Gainsboro,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0)
            };

            _lblNow = new TextBox
            {
                Text = "00:00:00:00",
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(58, 62, 67),
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                MaxLength = 11,
                TextAlign = HorizontalAlignment.Center,
                Margin = new Padding(0, 3, 0, 0)
            };

            _lblNow.GotFocus += (_, _) =>
            {
                _isEditingNowTimecode = true;
                _lblNow.SelectAll();
            };

            _lblNow.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    ZeroNowTimecodeDigit(e.KeyCode == Keys.Back);
                    return;
                }

                if (e.KeyCode != Keys.Enter)
                {
                    _isEditingNowTimecode = true;
                    return;
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
                await SeekFromNowInputAsync();
            };

            _lblNow.KeyPress += async (_, e) =>
            {
                if (e.KeyChar == '\b')
                {
                    e.Handled = true;
                    return;
                }

                if (char.IsDigit(e.KeyChar))
                {
                    e.Handled = true;
                    ReplaceNowTimecodeDigitAtCursor(e.KeyChar);
                    return;
                }

                if (e.KeyChar != '\r')
                {
                    e.Handled = true;
                    return;
                }

                e.Handled = true;
                await SeekFromNowInputAsync();
            };

            _lblNow.Leave += (_, _) =>
            {
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);
            };

            _lblDur = new Label
            {
                Text = "DUR 00:00:00:00",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.Gainsboro,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0)
            };

            _lblRemain = new Label
            {
                Text = "REM 00:00:00:00",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.Orange,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0)
            };

            _btnInlineMeters = new Button
            {
                Text = "",
                Image = CreateInlineMetersIcon(Color.LimeGreen),
                ImageAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.LimeGreen,
                BackColor = Color.FromArgb(72, 76, 82),
                Margin = new Padding(6, 0, 0, 0),
                TabStop = false
            };
            _btnInlineMeters.FlatAppearance.BorderColor = Color.FromArgb(95, 100, 106);
            _btnInlineMeters.Click += (_, _) => ToggleInlineMeters();

            layout.Controls.Add(_lblCurrentFile, 0, 0);
            layout.Controls.Add(_lblStart, 1, 0);
            layout.Controls.Add(_lblNow, 2, 0);
            layout.Controls.Add(_lblDur, 3, 0);
            layout.Controls.Add(_lblRemain, 4, 0);
            layout.Controls.Add(_btnInlineMeters, 5, 0);

            panel.Controls.Add(layout);

            return panel;
        }

        private Control BuildMetersPanel()
        {
            var host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67),
                Padding = new Padding(6)
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));  
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.Controls.Add(root);

           
            var scalePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67)
            };
            root.Controls.Add(scalePanel, 0, 0);

            double[] dbLabels = { 0, -6, -12, -18, -24, -30, -36, -42, -48, -54, -60 };

            scalePanel.Resize += (_, _) =>
            {
                scalePanel.Controls.Clear();

                int h = scalePanel.ClientSize.Height;
                if (h <= 0) return;

                _meterAreaHeight = Math.Max(12, h - 4);

                foreach (double db in dbLabels)
                {
                    int y = _meterScale.DbToY(db, _meterAreaHeight);

                    var lbl = new Label
                    {
                        Text = db.ToString("0"),
                        ForeColor = Color.White,
                        AutoSize = false,
                        Width = 42,
                        Height = 16,
                        Left = 0,
                        Top = Math.Max(0, Math.Min(y - 8, h - 16)),
                        TextAlign = ContentAlignment.MiddleRight,
                        Font = new Font("Segoe UI", 8f, FontStyle.Regular)
                    };

                    scalePanel.Controls.Add(lbl);
                }

                var dbUnit = new Label
                {
                    Text = "dB",
                    ForeColor = Color.White,
                    AutoSize = false,
                    Width = 42,
                    Height = 16,
                    Left = 0,
                    Top = h - 18,
                    TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Segoe UI", 8f, FontStyle.Regular)
                };

                scalePanel.Controls.Add(dbUnit);
            };
            var barsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 8,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            for (int i = 0; i < 8; i++)
                barsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));

            root.Controls.Add(barsLayout, 1, 0);

            _meterBars.Clear();
            _channelChecks.Clear();

            for (int i = 0; i < 8; i++)
            {
          
                var channelLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    RowCount = 2,
                    ColumnCount = 1,
                    Margin = new Padding(1, 0, 1, 0),
                    Padding = new Padding(0)
                };
                channelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                channelLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                var chk = new CheckBox
                {
                    Text = $"CH{i + 1}",
                    Dock = DockStyle.Fill,
                    Checked = true,
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(58, 62, 67),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                    Margin = new Padding(0)
                };

                int channelIndex = i;
                chk.CheckedChanged += (_, _) => OnChannelCheckChanged(channelIndex, chk.Checked);

                _channelChecks.Add(chk);

                var barBack = new Panel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(1, 0, 1, 0),
                    BackColor = Color.FromArgb(36, 39, 43),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var barFill = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 8,
                    BackColor = Color.LimeGreen
                };

                barBack.Controls.Add(barFill);
                _meterBars.Add(barFill);

                channelLayout.Controls.Add(chk, 0, 0);
                channelLayout.Controls.Add(barBack, 0, 1);

                barsLayout.Controls.Add(channelLayout, i, 0);
            }

            return host;
        }
        private async void OnChannelCheckChanged(int channelIndex, bool isChecked)
        {
            // 1. ?湔?桃蔗
            _player.ChannelMask[channelIndex] = isChecked;
            _audioMixer.SetChannelEnabled(channelIndex, isChecked);

            Console.WriteLine($"[Audio] Channel {channelIndex + 1} changed.");
            await Task.CompletedTask;
        }

        private void ToggleAudioChannel(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _channelChecks.Count)
                return;

            _channelChecks[channelIndex].Checked = !_channelChecks[channelIndex].Checked;
        }
   
        private Button CreatePlaybackButton(string text, int width, bool highlight = false)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                ForeColor = highlight ? Color.Orange : Color.Gainsboro,
                BackColor = Color.FromArgb(72, 76, 82),
                Margin = new Padding(4, 0, 0, 0),
                Font = new Font("Segoe UI Symbol", 10f, FontStyle.Bold),
                TabStop = false
            };

            btn.FlatAppearance.BorderColor = Color.FromArgb(95, 100, 106);
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(90, 94, 100);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(62, 66, 72);

            return btn;
        }
        private Control BuildPlaybackTimeRow()
        {
            _timelineLabelsPanel = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                Height = 22,
                BackColor = Color.FromArgb(58, 62, 67)
            };

            _timelineLabelsPanel.Resize += (_, _) =>
            {
                if (_currentMediaInfo != null)
                    RefreshTimelineTicks(_currentMediaInfo);
            };

            return _timelineLabelsPanel;
        }
        private void RefreshTimelineTicks(MediaInfoResult info)
        {
            if (_timelineLabelsPanel.ClientSize.Width <= 80)
                return;

            if (string.IsNullOrWhiteSpace(info.Som) || string.IsNullOrWhiteSpace(info.DurationTc))
                return;

            double fps = GetFpsFromInfo(info);
            if (fps <= 0) return;

            bool dropFrame = IsDropFrame(info);
            long durationFrames = TimecodeToFrame(info.DurationTc, fps, dropFrame);
            long somFrame = TimecodeToFrame(info.Som, fps, dropFrame);

            if (durationFrames <= 0)
                return;

            int tickCount = 8;
            int panelWidth = _timelineLabelsPanel.ClientSize.Width;

            _timelineLabelsPanel.SuspendLayout();
            _timelineLabelsPanel.Controls.Clear();

            for (int i = 0; i < tickCount; i++)
            {
                long tickFrame = somFrame + (durationFrames * i / (tickCount - 1));

                var lbl = new Label
                {
                    Text = FrameToTimecode(tickFrame, fps, dropFrame),
                    ForeColor = Color.White,
                    AutoSize = true,
                    Top = 2,
                    Font = new Font("Segoe UI", 8.5f),
                    BackColor = Color.Transparent
                };

                _timelineLabelsPanel.Controls.Add(lbl);

                int x = (int)((panelWidth - 1) * i / (double)(tickCount - 1));

                if (i == 0)
                    lbl.Left = 0;
                else if (i == tickCount - 1)
                    lbl.Left = Math.Max(0, panelWidth - lbl.Width);
                else
                    lbl.Left = Math.Max(0, x - lbl.Width / 2);
            }

            _timelineLabelsPanel.ResumeLayout();
        }
        private Label _lblRate = null!;
        private Control BuildPlaybackBar()
        {
            _lblRate = new Label
            {
                Text = "1x",
                Width = 42,
                Height = 30,
                ForeColor = Color.Orange,
                BackColor = Color.FromArgb(58, 62, 67),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Margin = new Padding(0, 4, 8, 0)
            };
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67),
                Padding = new Padding(8, 6, 8, 6)
            };

            var timeRow = BuildPlaybackTimeRow();

            _timeline = new TrackBar
            {
                Dock = DockStyle.Top,
                Height = 26,
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(58, 62, 67),
                Margin = new Padding(0)
            };
            _timeline.MouseDown += (_, e) =>
            {
                _isDraggingTimeline = true;
                SetTimelineValueFromMouse(e.X);
            };

            _timeline.MouseMove += (_, e) =>
            {
                if (_isDraggingTimeline)
                    SetTimelineValueFromMouse(e.X);
            };

            _timeline.MouseUp += async (_, _) =>
            {
                _isDraggingTimeline = false;
                await SeekFromTimeline();  
            };
            var btnRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0, 4, 0, 0),
                Margin = new Padding(0),
                BackColor = Color.FromArgb(58, 62, 67)
            };

            var btnMoveFirst = CreatePlaybackButton("⏮", 36);
            var btnMoveBackForward = CreatePlaybackButton("⏪", 36);
            var btnNegativeLog = CreatePlaybackButton("|◂", 36);
            var btnPlay = CreatePlaybackButton("▶", 40, true);
            var btnPause = CreatePlaybackButton("▌▌", 36);
            var btnPositiveLog = CreatePlaybackButton("▸|", 36);
            var btnMoveFastForward = CreatePlaybackButton("⏩", 36);
            var btnMoveLast = CreatePlaybackButton("⏭", 36);
            var btnMinus10 = CreatePlaybackButton("-5", 60);
            var btnPlus10 = CreatePlaybackButton("+5", 60);
            var btnMeters = CreatePlaybackButton("", 40);
            btnMeters.Image = CreateInlineMetersIcon(Color.LimeGreen);
            btnMeters.ImageAlign = ContentAlignment.MiddleCenter;

            BindPlaybackEvents(
                btnPlay,
                btnPause,
                btnMoveFirst,
                btnMoveLast,
                btnMoveBackForward,
                btnMoveFastForward,
                btnNegativeLog,
                btnPositiveLog,
                btnMinus10,
                btnPlus10
            );
            btnRow.Controls.Add(_lblRate);
            btnRow.Controls.Add(btnMoveFirst);
            btnRow.Controls.Add(btnMoveBackForward);
            btnRow.Controls.Add(btnNegativeLog);
            btnRow.Controls.Add(btnPlay);
            btnRow.Controls.Add(btnPause);
            btnRow.Controls.Add(btnPositiveLog);
            btnRow.Controls.Add(btnMoveFastForward);
            btnRow.Controls.Add(btnMoveLast);
            btnRow.Controls.Add(btnMinus10);
            btnRow.Controls.Add(btnPlus10);
            btnRow.Controls.Add(btnMeters);
            btnMeters.Click += (_, _) => ToggleMetersWindow();

            var buttonHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(58, 62, 67)
            };

            buttonHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            buttonHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            buttonHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            buttonHost.Controls.Add(btnRow, 1, 0);

            var playbackLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.FromArgb(58, 62, 67),
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            playbackLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // 時間刻度
            playbackLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // TrackBar
            playbackLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 按鈕

            timeRow.Dock = DockStyle.Fill;
            _timeline.Dock = DockStyle.Fill;
            buttonHost.Dock = DockStyle.Fill;

            playbackLayout.Controls.Add(timeRow, 0, 0);
            playbackLayout.Controls.Add(_timeline, 0, 1);
            playbackLayout.Controls.Add(buttonHost, 0, 2);

            panel.Controls.Add(playbackLayout);

            return panel;
        }
        private void BindPlaybackEvents(
            Button btnPlay,
            Button btnPause,
            Button btnMoveFirst,
            Button btnMoveLast,
            Button btnMoveBackForward,
            Button btnMoveFastForward,
            Button btnNegativeLog,
            Button btnPositiveLog,
            Button btnMinus10,
            Button btnPlus10)
        {
            btnPlay.Click += (_, _) => HandlePlay();
            btnPause.Click += (_, _) => HandlePause();
            btnMoveFirst.Click += (_, _) => HandleMoveFirst();
            btnMoveLast.Click += (_, _) => HandleMoveLast();
            btnMoveBackForward.Click += (_, _) => HandleMoveBackForward();
            btnMoveFastForward.Click += (_, _) => HandleMoveFastForward();
            btnNegativeLog.Click += (_, _) => HandleNegativeLog();
            btnPositiveLog.Click += (_, _) => HandlePositiveLog();
            btnMinus10.Click += async (_, _) => await HandleJump(-10);
            btnPlus10.Click += async (_, _) => await HandleJump(10);
        }

        private void ToggleMetersWindow()
        {
            if (_metersWindow != null)
            {
                _metersWindow.Close();
                return;
            }

            DetachMetersPanel();

            _metersWindow = new Form
            {
                Text = "8 CH",
                Size = new Size(210, 420),
                MinimumSize = new Size(180, 280),
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                BackColor = Color.FromArgb(58, 62, 67),
                Owner = this
            };

            var screenPoint = PointToScreen(new Point(ClientSize.Width - 240, 80));
            _metersWindow.Location = new Point(
                Math.Max(0, screenPoint.X),
                Math.Max(0, screenPoint.Y)
            );

            _metersPanel.Dock = DockStyle.Fill;
            _metersWindow.Controls.Add(_metersPanel);
            _metersWindow.FormClosing += (_, _) => RestoreMetersPanel();
            _metersWindow.Show(this);
        }

        private void ToggleInlineMeters()
        {
            if (_metersWindow != null)
                _metersWindow.Close();

            SetInlineMetersVisible(!_areInlineMetersVisible);
        }

        private void SetInlineMetersVisible(bool visible)
        {
            _areInlineMetersVisible = visible;

            if (_videoAndMetersLayout.ColumnStyles.Count > 1)
                _videoAndMetersLayout.ColumnStyles[1].Width = visible ? MainMetersWidth : 0;

            if (visible)
            {
                if (_metersPanel.Parent != null)
                    _metersPanel.Parent.Controls.Remove(_metersPanel);

                if (!_videoAndMetersLayout.Controls.Contains(_metersPanel))
                    _videoAndMetersLayout.Controls.Add(_metersPanel, 1, 0);

                _metersPanel.Dock = DockStyle.Fill;
            }
            else if (_metersPanel.Parent == _videoAndMetersLayout)
            {
                _videoAndMetersLayout.Controls.Remove(_metersPanel);
            }

            if (_btnInlineMeters != null)
            {
                _btnInlineMeters.Image?.Dispose();
                _btnInlineMeters.Image = CreateInlineMetersIcon(visible ? Color.LimeGreen : Color.FromArgb(120, 130, 120));
                _btnInlineMeters.BackColor = visible ? Color.FromArgb(72, 76, 82) : Color.FromArgb(52, 56, 60);
            }
        }

        private static Bitmap CreateInlineMetersIcon(Color color)
        {
            var bitmap = new Bitmap(20, 20);

            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(brush, 4, 12, 3, 5);
                g.FillRectangle(brush, 9, 7, 3, 10);
                g.FillRectangle(brush, 14, 4, 3, 13);
            }

            return bitmap;
        }

        private void DetachMetersPanel()
        {
            if (_metersPanel.Parent != null)
                _metersPanel.Parent.Controls.Remove(_metersPanel);

            if (_videoAndMetersLayout.ColumnStyles.Count > 1)
                _videoAndMetersLayout.ColumnStyles[1].Width = 0;

            _areInlineMetersVisible = false;
        }

        private void RestoreMetersPanel()
        {
            if (_metersWindow != null)
            {
                _metersWindow.Controls.Remove(_metersPanel);
                _metersWindow = null;
            }

            SetInlineMetersVisible(true);
        }

        private async void HandlePlay()
        {
            if (!TryGetSelectedMediaFile(out var file) || file == null)
                return;

            try
            {
                await PlaySelectedFileAsync(file, resetRateToNormal: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"播放失敗：{ex.Message}");
            }
        }
        private void HandlePause()
        {
            _playbackController.Pause();
            ResetUiUpdateThrottle();
        }

        private async void HandleMoveFirst() 
        {
            await SeekToBoundaryAsync(moveLast: false); 
        }

        private async void HandleMoveLast()
        {
            await SeekToBoundaryAsync(moveLast: true);
        }

        private async Task SeekToBoundaryAsync(bool moveLast)
        {
            if (_isBoundarySeeking)
                return;

            double fps = GetSelectedFps();
            if (fps <= 0)
                return;

            _isBoundarySeeking = true;
            try
            {
                if (moveLast)
                    await _playbackController.MoveLast(fps);
                else
                    await _playbackController.MoveFirst(fps);

                ResetUiUpdateThrottle();
                _displayedVideoFrameIndex = -1;
                UpdateTimelineUI(-1);

                await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 1000);
                UpdateVideoFrame();
                UpdateTimelineUI(-1);
                UpdateMetersFromAudioLevel();
            }
            finally
            {
                _isBoundarySeeking = false;
            }
        }
        private void ApplyPlaybackRate(float rate)
        {
            _lblRate.Text = $"{rate:0}x";
            _player.SetVideoRate(rate);
            _player.PrepareAudioForRate(rate);

            if (Math.Abs(rate - 1.0f) > 0.001f)
                _player.PrepareVideoBuffer();
        }
        private void HandleMoveBackForward()
        {
            float rate = _playbackController.MoveBackForward();
            ApplyPlaybackRate(rate);
        }
        
        private void HandleMoveFastForward()
        {
            float rate = _playbackController.MoveFastForward();
            ApplyPlaybackRate(rate);
        }
        private async void HandleNegativeLog()
        {
            await StepFrameAsync(-1);
        }

        private async void HandlePositiveLog()
        {
            await StepFrameAsync(1);
        }

        private async void StepFrameByKeyboard(int direction)
        {
            await StepFrameAsync(direction);
        }

        private async Task StepFrameAsync(int direction)
        {
            if (direction == 0)
                return;

            if (_isFrameStepping)
            {
                _pendingFrameStepDelta += Math.Sign(direction);
                return;
            }

            double fps = GetSelectedFps();
            if (fps <= 0)
                return;

            _isFrameStepping = true;
            int currentDirection = Math.Sign(direction);
            try
            {
                while (currentDirection != 0)
                {
                    if (currentDirection < 0)
                        _playbackController.NegativeLog(fps);
                    else
                        await _playbackController.PositiveLog(fps);

                    await RefreshAfterFrameStepAsync(fps);

                    currentDirection = 0;
                    if (_pendingFrameStepDelta != 0)
                    {
                        currentDirection = Math.Sign(_pendingFrameStepDelta);
                        _pendingFrameStepDelta -= currentDirection;
                    }
                }
            }
            finally
            {
                _isFrameStepping = false;
            }
        }

        private async Task RefreshAfterFrameStepAsync(double fps)
        {
            _displayedVideoFrameIndex = -1;
            await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 1000);
            UpdateVideoFrame();
            UpdateTimelineUI(-1);
            await _player.PlayFrameAudioAsync(_player.CurrentFrameIndex, fps);
            UpdateMetersFromAudioLevel();
        }
        private int GetPlaybackPrebufferFrames(float rate)
        {
            double multiplier = Math.Max(1.0, Math.Abs(rate));
            return (int)Math.Ceiling(PlaybackPrebufferFrames * multiplier);
        }
        private double GetFpsFromInfo(MediaInfoResult info)
        {
            return GetRealFpsFromInfo(info);
        }

        private int GetAudioSamplingRate(string fullPath)
        {
            if (_mediaCache.TryGetValue(fullPath, out var info) &&
                int.TryParse(info.AudioSamplingRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sampleRate) &&
                sampleRate > 0)
            {
                return sampleRate;
            }

            return 48000;
        }

        private double GetRealFpsFromInfo(MediaInfoResult info)
        {
            if (double.TryParse(info.FrameRateNum, NumberStyles.Float, CultureInfo.InvariantCulture, out double fpsNum) &&
                double.TryParse(info.FrameRateDen, NumberStyles.Float, CultureInfo.InvariantCulture, out double fpsDen) &&
                fpsDen > 0)
            {
                double realFps = fpsNum / fpsDen;
                if (realFps > 0)
                    return realFps;
            }

            string frameRateText = info.FrameRateValue;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRate;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRateDisplay;

            int spaceIndex = frameRateText.IndexOf(' ');
            if (spaceIndex > 0)
                frameRateText = frameRateText.Substring(0, spaceIndex);

            if (double.TryParse(frameRateText, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0)
                return fps;

            return 0;
        }
        private async Task SeekFromNowInputAsync()
        {
            if (_isSeeking) return;
            string input = _lblNow.Text.Trim();
            if (string.IsNullOrEmpty(input))
            {
                UpdateTimelineUI(-1);
                return;
            }

            double fps = GetSelectedFps();
            if (fps <= 0)
            {
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);
                return;
            }

            bool dropFrame = IsSelectedDropFrame();
            if (!TryGetFrameFromTimecode(input, fps, dropFrame, out long inputFrame))
            {
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);
                return;
            }

            long somFrame = 0;

            if (TryGetSelectedMediaFile(out var file) &&
                file != null &&
                _mediaCache.TryGetValue(file.FullPath, out var info))
            {
                somFrame = TimecodeToFrame(info.Som, fps, dropFrame);
            }

            long targetFrame = inputFrame >= somFrame ? inputFrame - somFrame : inputFrame;
            long lastFrame = PlayerService.FrameFromTimeMs(_player.LengthMs, fps);
            targetFrame = Math.Clamp(targetFrame, 0, Math.Max(0, lastFrame));
            _isSeeking = true;
            try
            {
                bool wasPlaying = _meterTimer.Enabled;
                _playbackController.Pause();
                _player.SeekVideoByFrame(targetFrame);
                _player.SeekAudioByFrame(targetFrame, fps);
                _displayedVideoFrameIndex = -1;
                await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 3000);
                UpdateVideoFrame();
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);
                ReleaseNowTimecodeInputFocus();

                if (wasPlaying)
                    await _playbackController.Play();
            }
            finally
            {
                _isEditingNowTimecode = false;
                _isSeeking = false;
            }
        }

        private void ReleaseNowTimecodeInputFocus()
        {
            _isEditingNowTimecode = false;

            if (_lblNow == null || !_lblNow.Focused)
                return;

            ActiveControl = null;
            Focus();
        }

        private async Task HandleJump(int seconds)
        {
            double fps = GetSelectedFps();
            if (fps <= 0) return;

            bool wasPlaying = _meterTimer.Enabled;

            _playbackController.Pause();

            await _playbackController.Jump(seconds, fps);

            _displayedVideoFrameIndex = -1;
            await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 1000);

            UpdateVideoFrame();
            UpdateTimelineUI(-1);
            if (_currentMediaInfo != null)
                RefreshTimelineTicks(_currentMediaInfo);
            UpdateMetersFromAudioLevel();

            if (wasPlaying)
                await _playbackController.Play();
        }
        private Button CreateControlButton(string text, int width)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(72, 76, 82),
                ForeColor = Color.White,
                Margin = new Padding(4, 3, 0, 3)
            };
        }

        private void BuildRightBrowserArea(Control parent)
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Color.FromArgb(45, 48, 52),
                Padding = new Padding(6)
            };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            parent.Controls.Add(outer);

            var topPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                BackColor = Color.FromArgb(58, 62, 67)
            };

            var pathBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34
            };

            _txtPath = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(70, 74, 79),
                ForeColor = Color.White
            };

            var btnFolder = CreateControlButton("...", 34);
            var btnRefresh = CreateControlButton("更新", 50);
            var btnUp = CreateControlButton("上", 34);
            var btnDown = CreateControlButton("下", 34);

            btnFolder.Dock = DockStyle.Left;
            btnRefresh.Dock = DockStyle.Right;
            btnUp.Dock = DockStyle.Right;
            btnDown.Dock = DockStyle.Right;

            btnFolder.Click += OnSelectFolder;
            btnRefresh.Click += OnRefreshFolder;
            btnUp.Click += (_, _) => SelectRelativeGridFile(-1);
            btnDown.Click += (_, _) => SelectRelativeGridFile(1);

            pathBar.Controls.Add(_txtPath);
            pathBar.Controls.Add(btnDown);
            pathBar.Controls.Add(btnUp);
            pathBar.Controls.Add(btnRefresh);
            pathBar.Controls.Add(btnFolder);

            _gridFiles = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(70, 74, 79),
                BorderStyle = BorderStyle.FixedSingle,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false,
                GridColor = Color.FromArgb(100, 104, 109),
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(58, 62, 67),
                    ForeColor = Color.White,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold)
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(70, 74, 79),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(110, 114, 119),
                    SelectionForeColor = Color.White
                }
            };

            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "檔案名", Width = 300 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "入點", Width = 120 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "出點", Width = 120 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "時長", Width = 120 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "副檔名", Width = 70 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "格式檢查", Width = 100 });

            _gridFiles.CellClick += OnGridFileClick;
            _gridFiles.CellDoubleClick += OnGridFileDoubleClick;

            topPanel.Controls.Add(_gridFiles);
            topPanel.Controls.Add(pathBar);

            var statusPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(205, 205, 205),
                Padding = new Padding(10, 6, 10, 6)
            };

            _lblFileCount = new Label
            {
                Text = "檔案數: 0",
                ForeColor = Color.Black,
                AutoSize = true,
                Left = 6,
                Top = 8
            };

            _lblTotalSize = new Label
            {
                Text = "總大小: 0 GB",
                ForeColor = Color.Black,
                AutoSize = true,
                Top = 8,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            statusPanel.Controls.Add(_lblFileCount);
            statusPanel.Controls.Add(_lblTotalSize);
            statusPanel.Resize += (_, _) =>
            {
                _lblTotalSize.Left = statusPanel.Width - _lblTotalSize.Width - 10;
            };

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                BackColor = Color.FromArgb(58, 62, 67)
            };

            var lblManage = new Label
            {
                Text = "媒體資訊",
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Color.White
            };

            var manageBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(72, 76, 82)
            };

            _txtInfo = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = Color.FromArgb(70, 74, 79),
                ForeColor = Color.White,
                Font = new Font("Consolas", 11),
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };

            bottomPanel.Controls.Add(_txtInfo);
            bottomPanel.Controls.Add(manageBar);
            bottomPanel.Controls.Add(lblManage);

            outer.Controls.Add(topPanel, 0, 0);
            outer.Controls.Add(statusPanel, 0, 1);
            outer.Controls.Add(bottomPanel, 0, 2);
        }
        private void OnGridFileClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (_gridFiles.Rows[e.RowIndex].Tag is not MediaFile file) return;

            try
            {
                LoadAndShowMedia(file.FullPath);
            }
            catch (Exception ex)
            {
                _txtInfo.Text = $"讀取 MediaInfo 失敗: {ex.Message}";
            }
        }
        private void OnSelectFolder(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() != DialogResult.OK) return;

            LoadFolderToGrid(dialog.SelectedPath);
        }

        private void OnRefreshFolder(object? sender, EventArgs e)
        {
            string folderPath = _txtPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            string? selectedPath = TryGetSelectedMediaFile(out var selectedFile) && selectedFile != null
                ? selectedFile.FullPath
                : null;

            try
            {
                var files = _folder.LoadFolder(folderPath);
                PopulateGrid(files);

                double totalGB = CalculateTotalSizeGB(files);
                UpdateRightSummary(files.Count, totalGB);
                ClearSelectedMediaInfo();

                if (!string.IsNullOrWhiteSpace(selectedPath))
                    SelectFileInGrid(selectedPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"更新資料夾失敗：{ex.Message}");
            }
        }

        private void SelectRelativeGridFile(int offset)
        {
            if (_gridFiles.Rows.Count == 0)
                return;

            int currentIndex = _gridFiles.CurrentRow?.Index ?? (offset > 0 ? -1 : _gridFiles.Rows.Count);
            int nextIndex = Math.Clamp(currentIndex + offset, 0, _gridFiles.Rows.Count - 1);
            SelectGridRow(nextIndex);
        }

        private bool SelectFileInGrid(string fullPath)
        {
            for (int i = 0; i < _gridFiles.Rows.Count; i++)
            {
                if (_gridFiles.Rows[i].Tag is MediaFile file &&
                    string.Equals(file.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectGridRow(i);
                    return true;
                }
            }

            return false;
        }

        private void SelectGridRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _gridFiles.Rows.Count)
                return;

            var row = _gridFiles.Rows[rowIndex];
            _gridFiles.ClearSelection();
            row.Selected = true;
            _gridFiles.CurrentCell = row.Cells[0];

            if (row.Tag is MediaFile file)
                LoadAndShowMedia(file.FullPath);
        }

        private void UpdateRightSummary(int count, double totalGB)
        {
            _lblFileCount.Text = $"檔案數: {count}";
            _lblTotalSize.Text = $"總大小: {totalGB:F2} GB";
        }



        private async void OnGridFileDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            var row = _gridFiles.Rows[e.RowIndex];
            if (row.Tag is not MediaFile file) return;

            try
            {
                _gridFiles.CurrentCell = row.Cells[0];
                await PlaySelectedFileAsync(file);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"播放失敗：{ex.Message}");
            }
        }

        private void UpdateMetersFromAudioLevel()
        {
            int meterHeight = _meterAreaHeight;

            if (meterHeight <= 0 && _meterBars.Count > 0 && _meterBars[0].Parent != null)
                meterHeight = Math.Max(12, _meterBars[0].Parent.ClientSize.Height - 4);

            if (meterHeight <= 0)
                return;

            long currentMs = _playbackController.GetCurrentTime();

            for (int i = 0; i < _meterBars.Count; i++)
            {
                var bar = _meterBars[i];
                if (bar.Parent == null) continue;

                float level = _player.GetChannelLevelAtTime(i, currentMs);
                bar.Height = _meterScale.LevelToBarHeight(level, meterHeight);
            }
        }

        private void ResetMeters()
        {
            foreach (var bar in _meterBars)
            {
                bar.Height = 8;
            }
        }

        private bool _isSeeking = false; // ?啣?銝?蝳行?璅?
        private long _rewindAnchorTime = -1;
        private void UpdateTimelineFromPlayer()
        {
            // ?湔?嚗??迤?冽?啜迤?冽???銝??歲頧?瘝???蝯?銝??脖?
            if (_isDraggingTimeline || _isUpdatingTimeline || _isSeeking) return;

            float rate = _playbackController.CurrentRate;

            if (rate < 0 && _timeline.Maximum < 0)
            {
                // 1. ???暺?
                if (_rewindAnchorTime == -1) _rewindAnchorTime = _player.CurrentTimeMs;

                _isSeeking = true;

                // 2. ?詨?靽格迤嚗郊?脫??100 (???啁? Timer Interval)
                long step = (long)(Math.Abs(rate) * 100);
                _rewindAnchorTime = Math.Max(0, _rewindAnchorTime - step);

                if (_rewindAnchorTime == 0)
                {
                    _playbackController.Pause();
                    _rewindAnchorTime = -1;
                    _isSeeking = false;
                    UpdateTimelineUI(-1);
                    return;
                }

                // 3. UI ?芸?嚗?撘瑁?霈?蝐文??脣漲璇歲?啁璅?嚗?????
                UpdateTimelineUI(_rewindAnchorTime);

                // 4. ??瑁?敶勗?頝唾?
                Task.Run(() => {
                    try
                    {
                        // ?摨惜撌脫 Pause ???Time 鞈血澆??恍???澆???撟
                        _player.Seek(_rewindAnchorTime);
                    }
                    catch { /* 敹賜摨惜???啣虜 */ }
                    finally
                    {
                    
                        _isSeeking = false;
                    }
                });
            }
            else
            {
                _rewindAnchorTime = -1;
                UpdateTimelineUI(-1);
            }
        }
       
        private void UpdateTimelineUI(long overrideTime = -1)
        {
            if (_isDraggingTimeline) return;
            _isUpdatingTimeline = true;

            try
            {
                long current = (overrideTime != -1) ? overrideTime : _playbackController.GetCurrentTime();
                long length = _playbackController.GetLength();

                if (length > 0)
                {
                   
                    int timelineValue = Math.Clamp((int)(current * _timeline.Maximum / length), _timeline.Minimum, _timeline.Maximum);
                    if (_timeline.Value != timelineValue)
                        _timeline.Value = timelineValue;

                    double fps = GetSelectedFps();
                    if (fps <= 0) return;

                    bool dropFrame = IsSelectedDropFrame();
                    long somFrame = 0;
                    long currentFrame = overrideTime != -1
                        ? PlayerService.FrameFromTimeMs(overrideTime, fps)
                        : _player.CurrentFrameIndex;

                    if (TryGetSelectedMediaFile(out var file) &&
                        file != null &&
                        _mediaCache.TryGetValue(file.FullPath, out var info))
                    {
                        somFrame = TimecodeToFrame(info.Som, fps, dropFrame);
                    }

                    if (!_isEditingNowTimecode)
                        SetNowTimecodeText(FrameToTimecode(somFrame + currentFrame, fps, dropFrame), dropFrame);

                    long lastFrame = PlayerService.FrameFromTimeMs(length, fps);
                    long remainFrames = Math.Max(0, lastFrame - currentFrame);
                    _lblRemain.Text = $"REM {FrameToTimecode(remainFrames, fps, dropFrame)}";
                }
            }
            finally
            {
                _isUpdatingTimeline = false;
            }
        }
        private void ZeroNowTimecodeDigit(bool backspace)
        {
            _isEditingNowTimecode = true;
            string text = _lblNow.Text;
            int cursor = _lblNow.SelectionStart;

            if (_lblNow.SelectionLength > 0)
            {
                int start = _lblNow.SelectionStart;
                int end = Math.Min(text.Length, start + _lblNow.SelectionLength);

                for (int i = start; i < end; i++)
                    ReplaceNowTimecodeDigit(i, '0');

                _lblNow.SelectionStart = start;
                _lblNow.SelectionLength = 0;
                return;
            }

            int position = backspace ? cursor - 1 : cursor;
            int direction = backspace ? -1 : 1;
            position = FindNowTimecodeDigitPosition(position, direction);

            if (position < 0)
                return;

            ReplaceNowTimecodeDigit(position, '0');
            _lblNow.SelectionStart = position;
            _lblNow.SelectionLength = 0;
        }

        private void ReplaceNowTimecodeDigitAtCursor(char digit)
        {
            _isEditingNowTimecode = true;
            int position = FindNowTimecodeDigitPosition(_lblNow.SelectionStart, 1);
            if (position < 0) return;

            ReplaceNowTimecodeDigit(position, digit);
            int nextPosition = FindNowTimecodeDigitPosition(position + 1, 1);
            _lblNow.SelectionStart = nextPosition >= 0 ? nextPosition : position;
            _lblNow.SelectionLength = 0;
        }

        private void ReplaceNowTimecodeDigit(int position, char digit)
        {
            if (!IsNowTimecodeDigitPosition(position))
                return;

            char[] chars = NormalizeNowTimecodeText(_lblNow.Text, IsSelectedDropFrame()).ToCharArray();
            chars[position] = digit;
            _lblNow.Text = new string(chars);
        }

        private int FindNowTimecodeDigitPosition(int position, int direction)
        {
            while (position >= 0 && position < 11)
            {
                if (IsNowTimecodeDigitPosition(position))
                    return position;
                position += direction;
            }

            return -1;
        }

        private bool IsNowTimecodeDigitPosition(int position)
        {
            return position >= 0 && position < 11 && position != 2 && position != 5 && position != 8;
        }

        private void SetNowTimecodeText(string timecode)
        {
            SetNowTimecodeText(timecode, IsSelectedDropFrame());
        }

        private void SetNowTimecodeText(string timecode, bool dropFrame)
        {
            _lblNow.Text = NormalizeNowTimecodeText(timecode, dropFrame);
        }

        private string NormalizeNowTimecodeText(string timecode, bool dropFrame)
        {
            string separator = dropFrame ? ";" : ":";

            if (string.IsNullOrWhiteSpace(timecode))
                return $"00:00:00{separator}00";

            string normalized = timecode.Trim();
            if (normalized.Length == 11 && (normalized[8] == ':' || normalized[8] == ';'))
                normalized = normalized[..8] + separator + normalized[9..];

            return normalized;
        }

        private bool IsSelectedDropFrame()
        {
            if (TryGetSelectedMediaFile(out var file) &&
                file != null &&
                _mediaCache.TryGetValue(file.FullPath, out var info))
            {
                return IsDropFrame(info);
            }

            return false;
        }

        private bool IsDropFrame(MediaInfoResult info)
        {
            return string.Equals(info.DropFrame, "True", StringComparison.OrdinalIgnoreCase)
                || info.Som.Contains(';')
                || info.Eom.Contains(';');
        }

        private long TimecodeToFrame(string tc, double fps, bool dropFrame)
        {
            return TryGetFrameFromTimecode(tc, fps, dropFrame, out long frameNumber)
                ? frameNumber
                : 0;
        }

        private bool TryGetFrameFromTimecode(string tc, double fps, bool dropFrame, out long frameNumber)
        {
            frameNumber = 0;
            string[] parts = tc.Split(':', ';');
            if (parts.Length < 4) return false;

            if (!int.TryParse(parts[0], out int h) ||
                !int.TryParse(parts[1], out int m) ||
                !int.TryParse(parts[2], out int s) ||
                !int.TryParse(parts[3], out int f))
            {
                return false;
            }

            int nominalFps = GetNominalFps(fps);
            if (h < 0 || m < 0 || m > 59 || s < 0 || s > 59 || f < 0 || f >= nominalFps)
                return false;

            frameNumber = TimecodePartsToFrame(h, m, s, f, fps, dropFrame);
            return true;
        }

        private long TimecodePartsToFrame(int hours, int minutes, int seconds, int frames, double fps, bool dropFrame)
        {
            int nominalFps = GetNominalFps(fps);
            if (dropFrame)
            {
                int dropFrames = GetDropFrameCount(fps);
                if (IsDroppedFrameLabel(minutes, seconds, frames, dropFrames))
                    frames = dropFrames;
            }

            long totalMinutes = (hours * 60L) + minutes;
            long frameNumber = (((hours * 3600L) + (minutes * 60L) + seconds) * nominalFps) + frames;

            if (dropFrame)
            {
                int dropFrames = GetDropFrameCount(fps);
                frameNumber -= dropFrames * (totalMinutes - (totalMinutes / 10));
            }

            return Math.Max(0, frameNumber);
        }

        private bool IsDroppedFrameLabel(int minutes, int seconds, int frames, int dropFrames)
        {
            return dropFrames > 0
                && seconds == 0
                && minutes % 10 != 0
                && frames < dropFrames;
        }

        private string FrameToTimecode(long frameNumber, double fps, bool dropFrame)
        {
            if (frameNumber < 0) frameNumber = 0;

            int nominalFps = GetNominalFps(fps);
            long timecodeFrameNumber = frameNumber;

            if (dropFrame)
            {
                int dropFrames = GetDropFrameCount(fps);
                long framesPerMinute = (nominalFps * 60L) - dropFrames;
                long framesPer10Minutes = (nominalFps * 600L) - (dropFrames * 9L);

                long tenMinuteBlocks = frameNumber / framesPer10Minutes;
                long remainingFrames = frameNumber % framesPer10Minutes;
                long droppedFrames = dropFrames * 9L * tenMinuteBlocks;

                if (remainingFrames >= dropFrames)
                    droppedFrames += dropFrames * ((remainingFrames - dropFrames) / framesPerMinute);

                timecodeFrameNumber += droppedFrames;
            }

            long hours = timecodeFrameNumber / (nominalFps * 3600L);
            timecodeFrameNumber %= nominalFps * 3600L;
            long minutes = timecodeFrameNumber / (nominalFps * 60L);
            timecodeFrameNumber %= nominalFps * 60L;
            long seconds = timecodeFrameNumber / nominalFps;
            long frame = timecodeFrameNumber % nominalFps;
            string separator = dropFrame ? ";" : ":";

            return $"{hours:00}:{minutes:00}:{seconds:00}{separator}{frame:00}";
        }

        private int GetNominalFps(double fps)
        {
            if (fps <= 0) fps = 29.97;
            return Math.Max(1, (int)Math.Round(fps));
        }

        private int GetDropFrameCount(double fps)
        {
            return Math.Max(0, (int)Math.Round(GetNominalFps(fps) * 0.0666666667));
        }

        private async Task SeekFromTimeline()
        {
            if (_isSeeking) return;
            _isSeeking = true;

            try
            {
                _meterTimer.Stop();

                // 先暫停，避免 Timer / 播放時鐘 / audio cache 同時跑
                _playbackController.Pause();

                _playbackController.SeekByTimelineValue(
                    _timeline.Value,
                    _timeline.Maximum,
                    GetSelectedFps()
                );

                _displayedVideoFrameIndex = -1;

                await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 250);
                UpdateVideoFrame();
                UpdateTimelineUI(-1);
                UpdateMetersFromAudioLevel();
                await _playbackController.Play(250);
            }
            finally
            {
                _isSeeking = false;
            }
        }

        private void SetTimelineValueFromMouse(int mouseX)
        {
            int width = Math.Max(1, _timeline.ClientSize.Width);
            int clampedX = Math.Clamp(mouseX, 0, width);
            int range = _timeline.Maximum - _timeline.Minimum;
            int value = _timeline.Minimum + (int)Math.Round(clampedX * range / (double)width);
            _timeline.Value = Math.Clamp(value, _timeline.Minimum, _timeline.Maximum);
        }
        
        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(72, 76, 82);
            public override Color MenuItemBorder => Color.FromArgb(72, 76, 82);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(72, 76, 82);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(72, 76, 82);
            public override Color MenuItemPressedGradientBegin => Color.FromArgb(58, 62, 67);
            public override Color MenuItemPressedGradientEnd => Color.FromArgb(58, 62, 67);
            public override Color ToolStripDropDownBackground => Color.FromArgb(58, 62, 67);
            public override Color ImageMarginGradientBegin => Color.FromArgb(58, 62, 67);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(58, 62, 67);
            public override Color ImageMarginGradientEnd => Color.FromArgb(58, 62, 67);
        }

        private enum MenuIconKind
        {
            FileOpen,
            FolderOpen,
            Power,
            Play,
            Pause,
            NegativeJog,
            PositiveJog,
            Rewind,
            FastForward,
            MoveFirst,
            MoveLast,
            Keyboard
        }

        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }
    }
}
