using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MxfPlayer.Models;
using MxfPlayer.Services;
using MxfPlayer.Controllers;
using System.IO;
using System.Threading.Tasks;
namespace MxfPlayer
{
    public class MainForm : Form
    {
        private bool _isDraggingTimeline = false;
        private bool _isUpdatingTimeline = false;
        private readonly PlayerService _player = new();
        private readonly FolderService _folder = new();
        private readonly MediaInfoService _mediaInfo = new();
        private readonly Dictionary<string, MediaInfoResult> _mediaCache = new();
        private readonly AudioMixerService _audioMixer = new();
        private readonly PlaybackController _playbackController;
        private Panel _timelineLabelsPanel = null!;
        private readonly Random _rnd = new();
        private PictureBox _videoView = null!;
        private TextBox _txtPath = null!;
        private DataGridView _gridFiles = null!;
        private TextBox _txtInfo = null!;
        private Label _lblCurrentFile = null!;
        private TextBox _lblNow = null!;
        private Label _lblStart = null!;
        private Label _lblDur = null!;
        private Label _lblRemain = null!;
        private Label _lblFileCount = null!;
        private Label _lblTotalSize = null!;
        private TrackBar _timeline = null!;
        private System.Windows.Forms.Timer _meterTimer = null!;
        private Image? _displayedVideoFrame;
        private long _displayedVideoFrameIndex = -1;
        private readonly List<Panel> _meterBars = new();
        private readonly List<CheckBox> _channelChecks = new();
        private bool _isStartingPlayback = false;
        private bool _isEditingNowTimecode = false;
        private bool _isBuffering = false;
        private MediaInfoResult? _currentMediaInfo;
        public MainForm()
        {
            Text = "Offline xPlayer";
            Width = 1680;
            Height = 930;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1400, 820);
            BackColor = Color.FromArgb(45, 48, 52);
            ForeColor = Color.White;

            InitUI();
            InitTimer();
            _playbackController = new PlaybackController(_player, _meterTimer, ResetMeters);
            this.FormClosing += (s, e) => _player.Dispose();
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

        private void ShowMediaInfo(MediaInfoResult info)
        {
            _currentMediaInfo = info;

            UpdateTimeLabels(info);

            BeginInvoke(new Action(() =>
            {
                RefreshTimelineTicks(info);
            }));

            _txtInfo.Text =
                $"寬度:               {info.Width} pixels{Environment.NewLine}" +
                $"高度:               {info.Height} pixels{Environment.NewLine}" +
                $"影格率:             {info.FrameRateDisplay} FPS{Environment.NewLine}" +
                $"Drop Frame:         {info.DropFrame}{Environment.NewLine}" +
                $"音訊聲道:           {info.AudioCount}{Environment.NewLine}" +
                $"格式名稱:           {info.CommercialName}{Environment.NewLine}" +
                $"掃描方式:           {info.ScanType}{Environment.NewLine}" +
                $"掃描順序:           {info.ScanOrder}{Environment.NewLine}" +
                $"SOM:                {info.Som}{Environment.NewLine}" +
                $"EOM:                {info.Eom}{Environment.NewLine}" +
                $"長度:               {info.DurationTc}{Environment.NewLine}" +
                $"規格檢查:           {info.SpecCheck}{Environment.NewLine}" +
                $"影片位元率:         {info.VideoBitRate}{Environment.NewLine}" +
                $"音訊單軌位元率:     {info.AudioBitRate}{Environment.NewLine}" +
                $"整體位元率:         {info.OverallBitRate}{Environment.NewLine}" +
                $"顯示比例:           {info.DisplayAspect}{Environment.NewLine}" +
                $"檔案名稱: {info.FileName}{Environment.NewLine}" +
                $"完整路徑: {info.FullPath}";
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
            if (!_mediaCache.TryGetValue(filePath, out var info))
            {
                info = _mediaInfo.GetInfo(filePath);
                _mediaCache[filePath] = info;
            }

            ShowMediaInfo(info);
        }

        private async Task<bool> StartPlaybackForFile(MediaFile file, long startTimeMs = 0)
        {
            if (_isStartingPlayback) return false;
            _isStartingPlayback = true;

            try
            {
                int audioCount = 8;
                double fps = 0;

                if (!_mediaCache.ContainsKey(file.FullPath))
                    LoadAndShowMedia(file.FullPath);

                if (_mediaCache.TryGetValue(file.FullPath, out var info))
                {
                    int.TryParse(info.AudioCount, out audioCount);
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

                using (var loading = new LoadingForm(this, file.FileName))
                {
                    loading.TopMost = true;
                    loading.Show();
                    loading.Refresh();

                    try
                    {
                        _displayedVideoFrameIndex = -1;
                        await _player.StartAudioBridge(file.FullPath, audioCount, startTimeMs, 1.0f, fps);
                        UpdateVideoFrame();
                    }
                    finally
                    {
                        loading.Close();
                    }
                }

                _meterTimer.Start();
                return true;
            }
            finally
            {
                _isStartingPlayback = false;
            }
        }

        private async Task PlaySelectedFileAsync(MediaFile file)
        {
            long startTimeMs = _player.CurrentPath == file.FullPath
                ? _playbackController.GetCurrentTime()
                : 0;

            bool needsStart = _player.CurrentPath != file.FullPath || !_player.IsAudioReady;
            if (needsStart)
            {
                bool started = await StartPlaybackForFile(file, startTimeMs);
                if (!started) return;
            }

            await _playbackController.Play();
            _lblNow.ForeColor = Color.Orange;
        }
        private double GetSelectedFps()
        {
            if (!TryGetSelectedMediaFile(out var file) ||
                file == null ||
                !_mediaCache.TryGetValue(file.FullPath, out var info))
            {
                return 0;
            }

            string frameRateText = info.FrameRateValue;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRate;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRateDisplay;

            
            int spaceIndex = frameRateText.IndexOf(' ');
            if (spaceIndex > 0)
                frameRateText = frameRateText.Substring(0, spaceIndex);

            if (double.TryParse(frameRateText, out double fps) && fps > 0)
                return fps;

            return 0;
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
               
                    _timeline.Value = Math.Clamp(_playbackController.GetTimelineValue(_timeline.Maximum), _timeline.Minimum, _timeline.Maximum);


                    double fps = GetSelectedFps();
                    if (fps <= 0) return;

                    long somMs = 0;
                    if (TryGetSelectedMediaFile(out var file) &&
                        file != null &&
                        _mediaCache.TryGetValue(file.FullPath, out var info))
                    {
                        somMs = GetMsFromTimecode(info.Som, fps);
                    }


                    if (!_isEditingNowTimecode)
                        SetNowTimecodeText(FormatTimecodeFromMilliseconds(somMs + current, fps));

                    _lblRemain.Text = $"REM {FormatTimecodeFromMilliseconds(Math.Max(0, length - current), fps)}";
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
            MessageBox.Show($"找到 {files.Count} 個 MXF 檔案");

            PopulateGrid(files);

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

        private void PopulateGrid(List<MediaFile> files)
        {
            _gridFiles.Rows.Clear();

            foreach (var file in files)
            {
                string som = "00:00:00;00";
                string eom = "00:00:00;00";
                string duration = "00:00:00;00";
                string specCheck = "Error";

                try
                {
                    if (!_mediaCache.TryGetValue(file.FullPath, out var info))
                    {
                        info = _mediaInfo.GetInfo(file.FullPath);
                        _mediaCache[file.FullPath] = info;
                    }

                    som = string.IsNullOrWhiteSpace(info.Som) ? "00:00:00;00" : info.Som;
                    eom = string.IsNullOrWhiteSpace(info.Eom) ? "00:00:00;00" : info.Eom;
                    duration = string.IsNullOrWhiteSpace(info.DurationTc) ? "00:00:00;00" : info.DurationTc;

                    specCheck = IsSpecPass(info) ? "HD" : "Error";
                }
                catch
                {
                    specCheck = "Error";
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

                if (specCheck == "Error")
                {
                    row.Cells[5].Style.ForeColor = Color.Red;
                    row.Cells[5].Style.Font = new Font(_gridFiles.Font, FontStyle.Bold);
                }
                else
                {
                    row.Cells[5].Style.ForeColor = Color.White;
                }
            }
        }
        private bool IsSpecPass(MediaInfoResult info)
        {
            // 目前先照你說的：不是 HD 就 Error
            if (!string.Equals(info.SpecCheck, "HD", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
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

        private void UpdateTimeLabels(MediaInfoResult info)
        {
            _lblCurrentFile.Text = info.FileName;
            _lblStart.Text = $"START {info.Som}";
            SetNowTimecodeText(info.Som);
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

                float rate = _playbackController.CurrentRate;

                //if (Math.Abs(rate - 1.0f) > 0.001f && !_player.HasCurrentVideoFrame)
                //{
                //    _isBuffering = true;

                //    try
                //    {
                //        _meterTimer.Stop();
                //        _player.Pause();

                //        await CheckBufferForRateAsync(rate);

                //        await _playbackController.Play();
                //    }
                //    finally
                //    {
                //        _isBuffering = false;
                //    }

                //    return;
                //}

                UpdateVideoFrame();
                UpdateMetersFromAudioLevel();
                UpdateTimelineFromPlayer();
            };
        }

        private void UpdateVideoFrame()
        {
            var nextFrame = _player.CreateDisplayVideoFrameSnapshot(out var frameIndex);
            if (nextFrame == null) return;
            if (frameIndex == _displayedVideoFrameIndex)
            {
                nextFrame.Dispose();
                return;
            }

            var previousFrame = _displayedVideoFrame;
            _displayedVideoFrame = nextFrame;
            _displayedVideoFrameIndex = frameIndex;
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

            menu.Items.Add("檔案");
            menu.Items.Add("播放");
            menu.Items.Add("Tools");

            MainMenuStrip = menu;
            Controls.Add(menu);
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

            var videoAndMeters = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(58, 62, 67)
            };
            videoAndMeters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            videoAndMeters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            videoWrap.Controls.Add(videoAndMeters);

            _videoView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Margin = new Padding(0),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            videoAndMeters.Controls.Add(_videoView, 0, 0);

            videoAndMeters.Controls.Add(BuildMetersPanel(), 1, 0);

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
                ColumnCount = 5,
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
                Text = "00:00:00;00",
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

            layout.Controls.Add(_lblCurrentFile, 0, 0);
            layout.Controls.Add(_lblStart, 1, 0);
            layout.Controls.Add(_lblNow, 2, 0);
            layout.Controls.Add(_lblDur, 3, 0);
            layout.Controls.Add(_lblRemain, 4, 0);

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
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));   // ?餃漲??祝
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.Controls.Add(root);

            // 撌阡??餃漲
            var scalePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67)
            };
            root.Controls.Add(scalePanel, 0, 0);

            string[] dbLabels = { "0", "-6", "-12", "-18", "-24", "-30", "-36", "-48", "-54", "dB" };

            scalePanel.Resize += (_, _) =>
            {
                scalePanel.Controls.Clear();

                int h = scalePanel.ClientSize.Height;
                if (h <= 0) return;

                for (int i = 0; i < dbLabels.Length; i++)
                {
                    int top;

                    if (dbLabels[i] == "dB")
                    {
                        top = h - 18;
                    }
                    else
                    {
                        top = 6 + (int)((h - 42) * i / 8.0);
                       
                    }

                    var lbl = new Label
                    {
                        Text = dbLabels[i],
                        ForeColor = Color.White,
                        AutoSize = false,
                        Width = 42,
                        Height = 16,
                        Left = 0,
                        Top = Math.Max(0, Math.Min(top, h - 16)),
                        TextAlign = ContentAlignment.MiddleRight,
                        Font = new Font("Segoe UI", 8f, FontStyle.Regular)
                    };

                    scalePanel.Controls.Add(lbl);
                }
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
                // 瘥?甈?銝?checkbox?? meter
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

            // 2. 潃??芸??怠?嚗?蔣?唾???
            // ?澆?批?冽?????迫 Timer 銝阡?閮?Meter
            HandlePause();

            // 3. 蝡?遣瞈暸
            // ?喳?桀???蝣箔??怠????瞈暸?銋甇?Ⅱ??
            if (TryGetSelectedMediaFile(out var file) && file != null && _player.CurrentPath == file.FullPath)
            {
                long currentTime = _playbackController.GetCurrentTime();
                await StartPlaybackForFile(file, currentTime);
            }

            Console.WriteLine($"[Audio] Channel {channelIndex + 1} changed.");
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
            _timelineLabelsPanel = new Panel
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
            _timelineLabelsPanel.Controls.Clear();

            if (_timelineLabelsPanel.ClientSize.Width <= 80)
                return;

            if (string.IsNullOrWhiteSpace(info.Som) || string.IsNullOrWhiteSpace(info.DurationTc))
                return;

            double fps = GetFpsFromInfo(info);
            if (fps <= 0) return;

            long totalMs = GetMsFromTimecode(info.DurationTc, fps);
            long somMs = GetMsFromTimecode(info.Som, fps);

            if (totalMs <= 0)
                return;

            int tickCount = 8;
            int panelWidth = _timelineLabelsPanel.ClientSize.Width;

            for (int i = 0; i < tickCount; i++)
            {
                long currentTickMs = somMs + (totalMs * i / (tickCount - 1));

                var lbl = new Label
                {
                    Text = FormatTimecodeFromMilliseconds(currentTickMs, fps),
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
            _timeline.MouseDown += (_, _) => _isDraggingTimeline = true;

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
            var btnMinus10 = CreatePlaybackButton("-10", 60);
            var btnPlus10 = CreatePlaybackButton("+10", 60);

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
        private async void HandlePlay()
        {
            if (!TryGetSelectedMediaFile(out var file) || file == null)
                return;

            try
            {
                await PlaySelectedFileAsync(file);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"æ’­æ”¾å¤±æ•—: {ex.Message}");
            }
        }
        private void HandlePause()
        {
            _playbackController.Pause();
        }

        private async void HandleMoveFirst() // 潃?async void ?舀迤蝣箇? UI 鈭辣撖急?
        {
            await _playbackController.MoveFirst(GetSelectedFps()); // 潃??曉?ㄐ?臭誑甇?虜 await 鈭?
        }

        private async void HandleMoveLast()
        {
            await _playbackController.MoveLast(GetSelectedFps());
        }
        private void ApplyPlaybackRate(float rate)
        {
            _lblRate.Text = $"{rate:0}x";

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
            double fps = GetSelectedFps();

            _playbackController.NegativeLog(fps);
            await RefreshAfterFrameStepAsync(fps);
        }

        private async void HandlePositiveLog()
        {
            double fps = GetSelectedFps();
            await _playbackController.PositiveLog(fps);
            await RefreshAfterFrameStepAsync(fps);
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
        private async Task CheckBufferForRateAsync(float rate)
        {
            if (Math.Abs(rate - 1.0f) < 0.001f)
                return;

            if (!TryGetSelectedMediaFile(out var file) || file == null)
                return;

            if (_player.HasVideoBufferForRate(rate))
                return;

            using var loading = new LoadingForm(this, file.FileName);
            loading.TopMost = true;
            loading.Show();
            loading.Refresh();

            var start = DateTime.Now;

            while ((DateTime.Now - start).TotalMilliseconds < 3000)
            {
                if (_player.HasVideoBufferForRate(rate))
                    break;

                await Task.Delay(50);
            }

            _displayedVideoFrameIndex = -1;
            UpdateVideoFrame();
        }
        private double GetFpsFromInfo(MediaInfoResult info)
        {
            string frameRateText = info.FrameRateValue;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRate;

            if (string.IsNullOrWhiteSpace(frameRateText))
                frameRateText = info.FrameRateDisplay;

            int spaceIndex = frameRateText.IndexOf(' ');
            if (spaceIndex > 0)
                frameRateText = frameRateText.Substring(0, spaceIndex);

            if (double.TryParse(frameRateText, out double fps) && fps > 0)
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
            long targetMs;

            if (!TryGetMsFromTimecode(input, fps, out long inputMs))
            {
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);
                return;
            }

            long somMs = 0;

            if (TryGetSelectedMediaFile(out var file) &&
                file != null &&
                _mediaCache.TryGetValue(file.FullPath, out var info))
            {
                somMs = GetMsFromTimecode(info.Som, fps);
            }

            targetMs = inputMs >= somMs ? inputMs - somMs : inputMs;

            targetMs = Math.Clamp(targetMs, 0, Math.Max(0, _player.LengthMs));

            _isSeeking = true;
            try
            {
                bool wasPlaying = _meterTimer.Enabled;
                _playbackController.Pause();
                _player.Seek(targetMs);
                _displayedVideoFrameIndex = -1;
                await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 3000);
                UpdateVideoFrame();
                _isEditingNowTimecode = false;
                UpdateTimelineUI(-1);

                if (wasPlaying)
                    await _playbackController.Play();
            }
            finally
            {
                _isEditingNowTimecode = false;
                _isSeeking = false;
            }
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
            UpdateMetersFromAudioLevel();

            if (wasPlaying)
                await _playbackController.Play();
        }
        private void HandleFullScreen()
        {
            if (WindowState == FormWindowState.Maximized)
                WindowState = FormWindowState.Normal;
            else
                WindowState = FormWindowState.Maximized;
        }

        private void HandleAutoPlayChanged(bool isChecked)
        {
            // TODO
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

            _txtInfo = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(70, 74, 79),
                ForeColor = Color.White,
                Font = new Font("Consolas", 11),
                BorderStyle = BorderStyle.None
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
                MessageBox.Show($"æ’­æ”¾å¤±æ•—: {ex.Message}");
            }
        }

        private void UpdateMetersFromAudioLevel()
        {
            for (int i = 0; i < _meterBars.Count; i++)
            {
                var bar = _meterBars[i];
                if (bar.Parent == null) continue;

                if (i < _channelChecks.Count && !_channelChecks[i].Checked)
                {
                    bar.Height = 8;
                    continue;
                }

                //float level = _player.GetChannelLevel(i);
                long currentMs = _playbackController.GetCurrentTime();
                float level = _player.GetChannelLevelAtTime(i, currentMs);
                int maxHeight = Math.Max(12, bar.Parent.ClientSize.Height - 4);

                if (level <= 0.0001f)
                {
                    bar.Height = 8;
                    continue;
                }

                double db = 20.0 * Math.Log10(level);

                // -60dB ~ 0dB 頧? 0~1
                double normalized = (db + 60.0) / 60.0;
                normalized = Math.Max(0, Math.Min(1, normalized));

                bar.Height = 8 + (int)((maxHeight - 8) * normalized);
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
                        // ?嚗??? MediaPlayer 蝣箏祕??摰?隞歹??銵?銝甈∟歲頧?
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
        // 憓?銝??亙???overrideTime
        private void UpdateTimelineUI(long overrideTime = -1)
        {
            if (_isDraggingTimeline) return;
            _isUpdatingTimeline = true;

            try
            {
                // ?詨?靽格迤嚗???????(?璅∪?)嚗停?冽?摰????血???曉
                long current = (overrideTime != -1) ? overrideTime : _playbackController.GetCurrentTime();
                long length = _playbackController.GetLength();

                if (length > 0)
                {
                    // ?ㄐ?雿輻 _playbackController.GetTimelineValue 
                    // 撱箄降?寧??閮?隞仿???overrideTime
                    _timeline.Value = Math.Clamp((int)(current * _timeline.Maximum / length), _timeline.Minimum, _timeline.Maximum);

                    double fps = GetSelectedFps();
                    if (fps <= 0) return;

                    long somMs = 0;
                    if (TryGetSelectedMediaFile(out var file) &&
                        file != null &&
                        _mediaCache.TryGetValue(file.FullPath, out var info))
                    {
                        somMs = GetMsFromTimecode(info.Som, fps);
                    }

                    if (!_isEditingNowTimecode)
                        SetNowTimecodeText(FormatTimecodeFromMilliseconds(somMs + current, fps));
                    _lblRemain.Text = $"REM {FormatTimecodeFromMilliseconds(Math.Max(0, length - current), fps)}";
                }
            }
            finally
            {
                _isUpdatingTimeline = false;
            }
        }
        private bool TryGetMsFromTimecode(string tc, double fps, out long ms)
        {
            ms = 0;
            string[] parts = tc.Split(':', ';');
            if (parts.Length < 4) return false;

            if (!int.TryParse(parts[0], out int h) ||
                !int.TryParse(parts[1], out int m) ||
                !int.TryParse(parts[2], out int s) ||
                !int.TryParse(parts[3], out int f))
            {
                return false;
            }

            if (h < 0 || m < 0 || m > 59 || s < 0 || s > 59 || f < 0 || f >= Math.Ceiling(fps))
                return false;

            double totalSeconds = (h * 3600) + (m * 60) + s + (f / fps);
            ms = (long)(totalSeconds * 1000);
            return true;
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

            char[] chars = NormalizeNowTimecodeText(_lblNow.Text).ToCharArray();
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
            _lblNow.Text = NormalizeNowTimecodeText(timecode);
        }

        private string NormalizeNowTimecodeText(string timecode)
        {
            if (string.IsNullOrWhiteSpace(timecode))
                return "00:00:00;00";

            string normalized = timecode.Trim();
            if (normalized.Length == 11 && (normalized[8] == ':' || normalized[8] == ';'))
                normalized = normalized[..8] + ";" + normalized[9..];

            return normalized;
        }

        private long GetMsFromTimecode(string tc, double fps)
        {
            try
            {
                // ?舀??(Drop frame)????
                string[] parts = tc.Split(':', ';');
                if (parts.Length < 4) return 0;

                int h = int.Parse(parts[0]);
                int m = int.Parse(parts[1]);
                int s = int.Parse(parts[2]);
                int f = int.Parse(parts[3]);

                double totalSeconds = (h * 3600) + (m * 60) + s + (f / fps);
                return (long)(totalSeconds * 1000);
            }
            catch { return 0; }
        }

        private string FormatTimecodeFromMilliseconds(long ms, double fps)
        {
            if (ms < 0) ms = 0;
            if (fps <= 0) fps = 29.97;

            TimeSpan ts = TimeSpan.FromMilliseconds(ms);
            int frame = (int)((ms % 1000) * fps / 1000.0);
            string separator = IsSelectedDropFrame() ? ";" : ":";

            return $"{((int)ts.TotalHours):00}:{ts.Minutes:00}:{ts.Seconds:00}{separator}{frame:00}";
        }
        private bool IsSelectedDropFrame()
        {
            if (TryGetSelectedMediaFile(out var file) &&
                file != null &&
                _mediaCache.TryGetValue(file.FullPath, out var info))
            {
                return string.Equals(info.DropFrame, "True", StringComparison.OrdinalIgnoreCase)
                    || info.Som.Contains(';')
                    || info.Eom.Contains(';');
            }

            return false;
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

                if (TryGetSelectedMediaFile(out var loadingFile) && loadingFile != null)
                {
                    using var loading = new LoadingForm(this, loadingFile.FileName);
                    loading.TopMost = true;
                    loading.Show();
                    loading.Refresh();

                    await _player.WaitForFrameBufferAsync(_player.CurrentFrameIndex, 3000);
                }

                UpdateVideoFrame();

                long elapsedMs = _playbackController.GetCurrentTime();
                long lengthMs = _playbackController.GetLength();

                double fps = GetSelectedFps();
                if (fps <= 0) return;

                long somMs = 0;

                if (TryGetSelectedMediaFile(out var file) &&
                    file != null &&
                    _mediaCache.TryGetValue(file.FullPath, out var info))
                {
                    somMs = GetMsFromTimecode(info.Som, fps);
                }

                if (!_isEditingNowTimecode)
                    SetNowTimecodeText(FormatTimecodeFromMilliseconds(somMs + elapsedMs, fps));
                _lblRemain.Text = $"REM {FormatTimecodeFromMilliseconds(Math.Max(0, lengthMs - elapsedMs), fps)}";
            }
            finally
            {
                _isSeeking = false;
                _meterTimer.Start();
            }
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
    }
}
