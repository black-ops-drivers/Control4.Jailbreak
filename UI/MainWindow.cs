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
            Jailbreak.Dock = DockStyle.Fill;
            splitContainer3.Panel1.Controls.Add(Jailbreak);

            // Size window to fit the Jailbreak content
            var contentSize = Jailbreak.PreferredSize;
            var chromeWidth = Width - ClientSize.Width;
            var chromeHeight = Height - ClientSize.Height;
            var menuHeight = menuStrip1.Height;
            var statusHeight = splitContainer3.Height - splitContainer3.SplitterDistance;
            Width = contentSize.Width + chromeWidth + Jailbreak.Margin.Horizontal + 10;
            Height = contentSize.Height + chromeHeight + menuHeight + statusHeight + 10;
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
            System.Diagnostics.Process.Start("https://github.com/garrynewman/Control4.Jailbreak");
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
