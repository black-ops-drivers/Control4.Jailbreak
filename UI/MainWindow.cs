using System;
using System.Windows.Forms;
using Garry.Control4.Jailbreak.Properties;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class MainWindow : Form
    {
        public Jailbreak Jailbreak { get; }
        private Director Director { get; }


        public MainWindow()
        {
            InitializeComponent();

            if (!System.IO.Directory.Exists(Constants.CertsFolder))
            {
                System.IO.Directory.CreateDirectory(Constants.CertsFolder);
            }

            if (!System.IO.Directory.Exists(Constants.KeysFolder))
            {
                System.IO.Directory.CreateDirectory(Constants.KeysFolder);
            }

            System.IO.File.WriteAllBytes($"{Constants.CertsFolder}/openssl.cfg", Resources.openssl);

            Text += $@" v{Constants.Version} ({Constants.TargetComposerVersion} / {Constants.TargetOsVersion})";

            Director = new Director(this);

            Jailbreak = new Jailbreak(this);
            // Dock to the top (not Fill) and let Panel1 scroll, so the content can be taller than
            // the window without being clipped when it exceeds the screen height.
            Jailbreak.Dock = DockStyle.Top;
            splitContainer3.Panel1.AutoScroll = true;
            splitContainer3.Panel1.Controls.Add(Jailbreak);

            // Size window to fit the Jailbreak content, but never taller than the screen; Panel1
            // scrolls for the overflow.
            var contentSize = Jailbreak.PreferredSize;
            var chromeWidth = Width - ClientSize.Width;
            var chromeHeight = Height - ClientSize.Height;
            var menuHeight = menuStrip1.Height;
            var statusHeight = splitContainer3.Height - splitContainer3.SplitterDistance;
            var desiredHeight = contentSize.Height + chromeHeight + menuHeight + statusHeight + 10;
            var maxHeight = Screen.PrimaryScreen.WorkingArea.Height;
            var needsScroll = desiredHeight > maxHeight;
            var scrollbarWidth = needsScroll ? SystemInformation.VerticalScrollBarWidth : 0;
            Width = contentSize.Width + chromeWidth + Jailbreak.Margin.Horizontal + 10 + scrollbarWidth;
            Height = Math.Min(desiredHeight, maxHeight);
            MaximizeBox = false;

            CenterToScreen();

            Load += OnLoaded;
        }

        public sealed override string Text
        {
            get => base.Text;
            set => base.Text = value;
        }

        private void OnLoaded(object sender, EventArgs e)
        {
            Director.RefreshList();
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            Application.Exit();
        }

        public void SetStatusRight(string txt)
        {
            StatusTextRight.Text = txt;
        }

        private void OpenComposerFolder(object sender, EventArgs e)
        {
            var dir = Jailbreak.ComposerInstallDir ?? @"C:\Program Files (x86)\Control4\Composer\Pro";
            System.Diagnostics.Process.Start(dir);
        }

        private void OpenComposerSettingsFolder(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start(
                $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\Control4");
        }

        private void ViewOnGithub(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/black-ops-drivers/Control4.Jailbreak");
        }

        private void FileAndQuit(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void VisitC4Diy(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://www.reddit.com/r/C4diy/");
        }
    }
}
