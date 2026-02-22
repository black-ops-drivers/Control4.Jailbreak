using System;
using System.Drawing;
using System.Windows.Forms;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class LogWindow : Form
    {
        public LogWindow(Form mainWindow, string title = "LogWindow")
        {
            Owner = mainWindow;
            InitializeComponent(title);

            CenterToParent();
            Show();
        }

        private void Write(string v)
        {
            _progressLineStart = -1;
            textBox.AppendText(v);

            textBox.ScrollToCaret();
            textBox.Refresh();
        }

        internal void WriteNormal(string v)
        {
            textBox.SelectionColor = Color.Black;
            Write(v);
        }

        internal void WriteSuccess(string v)
        {
            textBox.SelectionColor = Color.Green;
            Write(v);
        }

        // ReSharper disable once UnusedMember.Global
        internal void WriteWarning(string v)
        {
            textBox.SelectionColor = Color.Orange;
            Write(v);
        }

        internal void WriteError(Exception v)
        {
            WriteError($"\n{v.Message}\n");
            WriteNormal($"{v.StackTrace}\n");
        }

        internal void WriteError(string v)
        {
            textBox.SelectionColor = Color.Red;
            Write(v);
        }

        internal void WriteTrace(string v)
        {
            textBox.SelectionColor = Color.Gray;
            Write(v);
        }

        internal void WriteHighlight(string v)
        {
            textBox.SelectionColor = Color.Blue;
            Write(v);
        }

        private int _progressLineStart = -1;

        internal void WriteProgress(string v)
        {
            if (_progressLineStart >= 0)
            {
                // Replace the previous progress text in-place
                textBox.Select(_progressLineStart, textBox.TextLength - _progressLineStart);
                textBox.SelectionColor = Color.Gray;
                textBox.SelectedText = v;
                _progressLineStart = textBox.TextLength - v.Length;
            }
            else
            {
                _progressLineStart = textBox.TextLength;
                textBox.SelectionColor = Color.Gray;
                textBox.AppendText(v);
            }

            textBox.ScrollToCaret();
            textBox.Refresh();
        }

        internal void WriteHeader(string title)
        {
            var line = new string('\u2500', 50 - title.Length);
            textBox.SelectionColor = Color.Blue;
            Write($"\n\u2500\u2500 {title} {line}\n");
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            Owner.Enabled = true;
        }
    }
}