using System;
using System.Drawing;
using System.Windows.Forms;

namespace MxfPlayer
{
    public class LoadingForm : Form
    {
        private ProgressBar _progressBar;
        private Label _lblInfo;

        public LoadingForm(Form owner, string fileName)
        {
            // 視窗基本設定
            this.Owner = owner;
            this.Text = "Loading Media";
            this.Size = new Size(450, 150);
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.FromArgb(36, 39, 43); // 匹配播放器深背景

            // 精確計算置中座標
            int x = owner.Location.X + (owner.Width - this.Width) / 2;
            int y = owner.Location.Y + (owner.Height - this.Height) / 2;
            this.Location = new Point(x, y);

            // 建立一個有邊框的容器 Panel，解決「只有框框」的視覺問題
            Panel mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(20)
            };
            // 邊框顏色 (橘色)
            mainPanel.Paint += (s, e) => {
                ControlPaint.DrawBorder(e.Graphics, mainPanel.ClientRectangle, Color.Orange, ButtonBorderStyle.Solid);
            };

            // 顯示文字
            _lblInfo = new Label
            {
                Text = $"正在解析音訊，請稍候...\n檔案：{fileName}",
                ForeColor = Color.White,
                Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Top,
                Height = 60
            };

            // 跑馬燈進度條
            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Dock = DockStyle.Bottom,
                Height = 20,
                BackColor = Color.FromArgb(45, 48, 52)
            };

            mainPanel.Controls.Add(_lblInfo);
            mainPanel.Controls.Add(_progressBar);
            this.Controls.Add(mainPanel);

            // 強制在視窗顯示時最上層
            this.TopMost = true;
        }

        // 解決「只有框框」的關鍵：在 Show 之後調用 Refresh
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.Refresh(); // 強制重繪所有子控制項
        }
    }
}