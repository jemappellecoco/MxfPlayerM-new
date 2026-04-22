using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.WinForms;
using MxfPlayer.Models;
using MxfPlayer.Services;
using MxfPlayer.Controllers;
using System.IO;
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
        private VideoView _videoView = null!;
        private TextBox _txtPath = null!;
        private DataGridView _gridFiles = null!;
        private TextBox _txtInfo = null!;
        private Label _lblCurrentFile = null!;
        private Label _lblNow = null!;
        private Label _lblStart = null!;
        private Label _lblDur = null!;
        private Label _lblRemain = null!;
        private Label _lblFileCount = null!;
        private Label _lblTotalSize = null!;
        private TrackBar _timeline = null!;
        private System.Windows.Forms.Timer _meterTimer = null!;
        private readonly List<Panel> _meterBars = new();
        private readonly List<CheckBox> _channelChecks = new();
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
            UpdateTimeLabels(info);

            _txtInfo.Text =
                $"Width:              {info.Width} pixels{Environment.NewLine}" +
                $"Height:             {info.Height} pixels{Environment.NewLine}" +
                $"FrameRate:          {info.FrameRate} FPS{Environment.NewLine}" +
                $"DropFrame:          True{Environment.NewLine}" +
                $"Audio Channel:      {info.AudioCount}{Environment.NewLine}" +
                $"CommercialName:     {info.CommercialName}{Environment.NewLine}" +
                $"ScanType:           {info.ScanType}{Environment.NewLine}" +
                $"ScanOrder:          {info.ScanOrder}{Environment.NewLine}" +
                $"SOM:                {info.Som}{Environment.NewLine}" +
                $"EOM:                {info.Eom}{Environment.NewLine}" +
                $"Duration:           {info.DurationTc}{Environment.NewLine}" +
                $"SpecCheck:          {info.SpecCheck}{Environment.NewLine}" +
                $"Bit Rate:           {info.BitRate}{Environment.NewLine}" +
                $"Display Aspect:     {info.DisplayAspect}{Environment.NewLine}" +
                Environment.NewLine +
                $"檔名: {info.FileName}{Environment.NewLine}" +
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

        private async Task StartPlaybackForFile(MediaFile file)
        {
            // 1. 取得音軌數量 (從快取獲取)
            int audioCount = 8;
            if (_mediaCache.TryGetValue(file.FullPath, out var info))
            {
                int.TryParse(info.AudioCount, out audioCount);
            }

            // 2. 同步 CheckBox 狀態到 Player 遮罩
            for (int i = 0; i < 8; i++)
            {
                _player.ChannelMask[i] = _channelChecks[i].Checked;
            }

            // 3. 建立並顯示 Loading 視窗
            // 使用 using 確保 loadingForm 物件資源最後會被釋放
            using (var loading = new LoadingForm(this, file.FileName))
            {
                // 改進 2：TopMost 確保不會被 VLC 視窗蓋住
                loading.TopMost = true;
                loading.Show();

                // 改進 3：強制 Refresh 比 DoEvents 穩定，確保 UI 繪製
                loading.Refresh();

                try
                {
                    await _player.StartAudioBridge(file.FullPath, audioCount);
                }
                finally
                {
                    loading.Close();
                }
            }
            _meterTimer.Start();
        }
        
        //private void SyncChannelSelectionToMixer()
        //{
        //    for (int i = 0; i < _channelChecks.Count; i++)
        //    {
        //        _audioMixer.SetChannelEnabled(i, _channelChecks[i].Checked);
        //    }

        //    var selected = _audioMixer.GetSelectedIndices();
        //    Console.WriteLine("[Audio] Play with selected = " + string.Join(",", selected));
        //}

        private void LoadFolderToGrid(string folderPath)
        {
            _txtPath.Text = folderPath;

            var files = _folder.LoadFolder(folderPath);
            MessageBox.Show($"抓到 {files.Count} 個 MXF 檔案");

            PopulateGrid(files);

            double totalGB = CalculateTotalSizeGB(files);
            UpdateRightSummary(files.Count, totalGB);
        }

        private void PopulateGrid(List<MediaFile> files)
        {
            _gridFiles.Rows.Clear();

            foreach (var file in files)
            {
                int rowIndex = _gridFiles.Rows.Add(
                    file.FileName,
                    "00:00:00;00",
                    "00:00:59;29",
                    "00:01:00;02",
                    Path.GetExtension(file.FileName),
                    "HD");

                _gridFiles.Rows[rowIndex].Tag = file;
            }
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
            _lblNow.Text = info.Som;
            _lblDur.Text = $"DUR {info.DurationTc}";
            _lblRemain.Text = $"REM {info.Eom}";
        }
        private void InitTimer()
        {
            _meterTimer = new System.Windows.Forms.Timer();
            _meterTimer.Interval = 180;
            _meterTimer.Tick += (_, _) =>
            {
                FakeUpdateMeters();
                UpdateTimelineFromPlayer();
            };
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

            menu.Items.Add("File");
            menu.Items.Add("Playback");
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

            _videoView = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Margin = new Padding(0)
            };
            _videoView.MediaPlayer = _player.MediaPlayer;
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

            _lblCurrentFile = new Label
            {
                Text = "未選擇檔案",
                AutoSize = false,
                Location = new Point(10, 8),
                Size = new Size(360, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Regular)
            };

            _lblStart = new Label
            {
                Text = "START 00:00:00:00",
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Location = new Point(410, 11)
            };

            _lblNow = new Label
            {
                Text = "00:00:00:00",
                AutoSize = true,
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Location = new Point(540, 7)
            };

            _lblDur = new Label
            {
                Text = "DUR 00:00:00:00",
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Location = new Point(685, 11)
            };

            _lblRemain = new Label
            {
                Text = "REM 00:00:00:00",
                AutoSize = true,
                ForeColor = Color.Orange,
                Location = new Point(820, 11)
            };

            panel.Controls.Add(_lblCurrentFile);
            panel.Controls.Add(_lblStart);
            panel.Controls.Add(_lblNow);
            panel.Controls.Add(_lblDur);
            panel.Controls.Add(_lblRemain);

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
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));   // 刻度區加寬
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.Controls.Add(root);

            // 左邊刻度
            var scalePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(58, 62, 67)
            };
            root.Controls.Add(scalePanel, 0, 0);

            AddScaleLabel(scalePanel, "0", 6);
            AddScaleLabel(scalePanel, "-6", 85);
            AddScaleLabel(scalePanel, "-12", 165);
            AddScaleLabel(scalePanel, "-18", 245);
            AddScaleLabel(scalePanel, "-24", 325);
            AddScaleLabel(scalePanel, "-30", 405);
            AddScaleLabel(scalePanel, "-36", 485);
            AddScaleLabel(scalePanel, "-48", 615);
            AddScaleLabel(scalePanel, "-54", 695);
            AddScaleLabel(scalePanel, "dB", 770);

            // 右邊 8 聲道
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
                // 每一欄：上 checkbox、下 meter
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
        private void OnChannelCheckChanged(int channelIndex, bool isChecked)
        {
            // 1. 更新遮罩
            _player.ChannelMask[channelIndex] = isChecked;
            _audioMixer.SetChannelEnabled(channelIndex, isChecked);

            // 2. ⭐ 自動暫停：避免影音跑掉
            // 呼叫控制器暫停，這會停止 Timer 並重設 Meter
            HandlePause();

            // 3. 立即重建濾鏡
            // 傳入目前的倍速，確保暫停狀態下濾鏡參數也是正確的
            _player.UpdateFilterGraph(_playbackController.CurrentRate);

            Console.WriteLine($"[Audio] 聲道 {channelIndex + 1} 已變更，系統已自動暫停以確保影音同步。");
        }
        private void AddScaleLabel(Panel parent, string text, int top)
        {
            var lbl = new Label
            {
                Text = text,
                ForeColor = Color.White,
                AutoSize = false,
                Width = 42,
                Height = 16,
                Left = 0,
                Top = top,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular)
            };
            parent.Controls.Add(lbl);
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
            var timeRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 22
            };

            var tickTexts = new[]
            {
        "00:00:00", "00:00:08", "00:00:17", "00:00:25",
        "00:00:34", "00:00:42", "00:00:51", "00:01:00"
    };

            int left = 0;
            foreach (var text in tickTexts)
            {
                var lbl = new Label
                {
                    Text = text,
                    ForeColor = Color.White,
                    AutoSize = true,
                    Left = left,
                    Top = 2,
                    Font = new Font("Segoe UI", 8.5f)
                };
                timeRow.Controls.Add(lbl);
                left += 95;
            }

            return timeRow;
        }
        private Control BuildPlaybackBar()
        {
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

            var btnMoveFirst = CreatePlaybackButton("|◀", 36);
            var btnMoveBackForward = CreatePlaybackButton("⏪", 36);
            var btnNegativeLog = CreatePlaybackButton("|◂", 36);
            var btnPlay = CreatePlaybackButton("▶", 40, true);
            var btnPause = CreatePlaybackButton("▌▌", 36);
            var btnPositiveLog = CreatePlaybackButton("▸|", 36);
            var btnMoveFastForward = CreatePlaybackButton("⏩", 36);
            var btnMoveLast = CreatePlaybackButton("▶|", 36);
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

            panel.Controls.Add(buttonHost);
            panel.Controls.Add(_timeline);
            panel.Controls.Add(timeRow);

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
            btnMinus10.Click += (_, _) => HandleJump(-10);
            btnPlus10.Click += (_, _) => HandleJump(10);
        }
        private async void HandlePlay()
        {
            if (!TryGetSelectedMediaFile(out var file) || file == null)
                return;

            // 1. 同步目前的 CheckBox 狀態到 Player
            for (int i = 0; i < 8; i++)
            {
                _player.ChannelMask[i] = _channelChecks[i].Checked;
            }

            // 2. 顯示 Loading 並啟動
            using (var loading = new LoadingForm(this, file.FileName))
            {
                loading.TopMost = true;
                loading.Show();
                loading.Refresh();

                try
                {
                    // 改用 Controller 啟動，它內部會處理 PlaybackRate 與 StartAudioBridge
                    await _playbackController.Play();
                    _lblNow.ForeColor = Color.Orange;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"播放失敗: {ex.Message}");
                }
                finally
                {
                    loading.Close();
                }
            }
        }

        private void HandlePause()
        {
            _playbackController.Pause();
        }

        private async void HandleMoveFirst() // ⭐ async void 是正確的 UI 事件寫法
        {
            await _playbackController.MoveFirst(); // ⭐ 現在這裡可以正常 await 了！
        }

        private async void HandleMoveLast()
        {
            await _playbackController.MoveLast();
        }

        private void HandleMoveBackForward()
        {
            float rate = _playbackController.MoveBackForward();
            _lblNow.Text = $"{rate:0}x";
        }

        private void HandleMoveFastForward()
        {
            float rate = _playbackController.MoveFastForward();
            _lblNow.Text = $"{rate:0}x";
        }

        private void HandleNegativeLog()
        {
            double fps = 29.97;

            if (TryGetSelectedMediaFile(out var file) &&
                file != null &&
                _mediaCache.TryGetValue(file.FullPath, out var info) &&
                double.TryParse(info.FrameRate, out var parsedFps))
            {
                fps = parsedFps;
            }

            _playbackController.NegativeLog(fps);
        }

        private async Task HandlePositiveLog()
        {
            await _playbackController.PositiveLog();
        }

        private async Task HandleJump(int seconds)
        {
            await _playbackController.Jump(seconds);
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

            var btnFolder = CreateControlButton("📁", 34);
            var btnRefresh = CreateControlButton("⟳", 34);
            var btnUp = CreateControlButton("⌃", 34);
            var btnDown = CreateControlButton("⌄", 34);

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

            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "檔案名", Width = 250 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "入點", Width = 90 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "出點", Width = 90 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "時長", Width = 90 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "副檔名", Width = 70 });
            _gridFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "格式檢查", Width = 80 });

            _gridFiles.SelectionChanged += OnGridSelectionChanged;
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
                Text = "文件總數：0",
                ForeColor = Color.Black,
                AutoSize = true,
                Left = 6,
                Top = 8
            };

            _lblTotalSize = new Label
            {
                Text = "總大小：0 GB",
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
                Text = "管理",
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
        private void OnGridSelectionChanged(object? sender, EventArgs e)
        {
            if (!TryGetSelectedMediaFile(out var file) || file == null) return;

            try
            {
                LoadAndShowMedia(file.FullPath);
            }
            catch (Exception ex)
            {
                _txtInfo.Text = $"讀取 MediaInfo 失敗：{ex.Message}";
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
            _lblFileCount.Text = $"文件總數：{count}";
            _lblTotalSize.Text = $"總大小：{totalGB:F2} GB";
        }

       

        private async void OnGridFileDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            var row = _gridFiles.Rows[e.RowIndex];
            if (row.Tag is not MediaFile file) return;

           await StartPlaybackForFile(file);
        }
      

        private void FakeUpdateMeters()
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

                int maxHeight = Math.Max(12, bar.Parent.ClientSize.Height - 4);
                bar.Height = _rnd.Next(12, maxHeight);
            }
        }

        private void ResetMeters()
        {
            foreach (var bar in _meterBars)
            {
                bar.Height = 8;
            }
        }
       
       private void UpdateTimelineFromPlayer()
        {
            // ⭐ 修改：同時檢查正在拖動或正在更新中
            if (_isDraggingTimeline || _isUpdatingTimeline) return;

            long current = _playbackController.GetCurrentTime();
            long length = _playbackController.GetLength();

            if (length <= 0) return;

            _isUpdatingTimeline = true; // 開始更新

            _timeline.Value = _playbackController.GetTimelineValue(_timeline.Maximum);
            _lblNow.Text = FormatTimecodeFromMilliseconds(current);
            _lblRemain.Text = $"REM {FormatTimecodeFromMilliseconds(Math.Max(0, length - current))}";

            _isUpdatingTimeline = false; // 更新結束
        }
        private async Task SeekFromTimeline()
        {
            await _playbackController.SeekByTimelineValue(_timeline.Value, _timeline.Maximum);
            long current = _playbackController.GetCurrentTime();
            long length = _playbackController.GetLength();

            _lblNow.Text = FormatTimecodeFromMilliseconds(current);
            _lblRemain.Text = $"REM {FormatTimecodeFromMilliseconds(Math.Max(0, length - current))}";
        }
        private string FormatTimecodeFromMilliseconds(long ms)
        {
            if (ms < 0) ms = 0;
            TimeSpan ts = TimeSpan.FromMilliseconds(ms);

            // 假設 29.97 fps (每幀約 33.3ms)，如果 info 有值建議從 info 抓
            int frame = (int)((ms % 1000) / 33.3);

            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00};{frame:00}";
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