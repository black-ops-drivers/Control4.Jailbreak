namespace Garry.Control4.Jailbreak.UI
{
    partial class MainWindow
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainWindow));
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.quitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.foldersToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.composerFolderToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.composerSettingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.helpToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.rc4diyToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.splitContainer3 = new System.Windows.Forms.SplitContainer();
            this.StatusTextLeft = new System.Windows.Forms.Label();
            this.StatusTextRight = new System.Windows.Forms.Label();
            this.menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).BeginInit();
            this.splitContainer3.Panel2.SuspendLayout();
            this.splitContainer3.SuspendLayout();
            this.SuspendLayout();
            //
            // menuStrip1
            //
            this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileToolStripMenuItem,
            this.foldersToolStripMenuItem,
            this.helpToolStripMenuItem});
            this.menuStrip1.Location = new System.Drawing.Point(0, 0);
            this.menuStrip1.Name = "menuStrip1";
            this.menuStrip1.Size = new System.Drawing.Size(504, 24);
            this.menuStrip1.TabIndex = 1;
            this.menuStrip1.Text = "menuStrip1";
            //
            // fileToolStripMenuItem
            //
            this.fileToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.quitToolStripMenuItem});
            this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            this.fileToolStripMenuItem.Size = new System.Drawing.Size(37, 20);
            this.fileToolStripMenuItem.Text = "File";
            //
            // quitToolStripMenuItem
            //
            this.quitToolStripMenuItem.Name = "quitToolStripMenuItem";
            this.quitToolStripMenuItem.Size = new System.Drawing.Size(97, 22);
            this.quitToolStripMenuItem.Text = "Quit";
            this.quitToolStripMenuItem.Click += new System.EventHandler(this.FileAndQuit);
            //
            // foldersToolStripMenuItem
            //
            this.foldersToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.composerFolderToolStripMenuItem,
            this.composerSettingsToolStripMenuItem});
            this.foldersToolStripMenuItem.Name = "foldersToolStripMenuItem";
            this.foldersToolStripMenuItem.Size = new System.Drawing.Size(57, 20);
            this.foldersToolStripMenuItem.Text = "Folders";
            //
            // composerFolderToolStripMenuItem
            //
            this.composerFolderToolStripMenuItem.Name = "composerFolderToolStripMenuItem";
            this.composerFolderToolStripMenuItem.Size = new System.Drawing.Size(174, 22);
            this.composerFolderToolStripMenuItem.Text = "Composer Folder";
            this.composerFolderToolStripMenuItem.Click += new System.EventHandler(this.OpenComposerFolder);
            //
            // composerSettingsToolStripMenuItem
            //
            this.composerSettingsToolStripMenuItem.Name = "composerSettingsToolStripMenuItem";
            this.composerSettingsToolStripMenuItem.Size = new System.Drawing.Size(174, 22);
            this.composerSettingsToolStripMenuItem.Text = "Composer Settings";
            this.composerSettingsToolStripMenuItem.Click += new System.EventHandler(this.OpenComposerSettingsFolder);
            //
            // helpToolStripMenuItem
            //
            this.helpToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.aboutToolStripMenuItem,
            this.rc4diyToolStripMenuItem});
            this.helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            this.helpToolStripMenuItem.Size = new System.Drawing.Size(44, 20);
            this.helpToolStripMenuItem.Text = "Help";
            //
            // aboutToolStripMenuItem
            //
            this.aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            this.aboutToolStripMenuItem.Size = new System.Drawing.Size(183, 22);
            this.aboutToolStripMenuItem.Text = "View On GitHub";
            this.aboutToolStripMenuItem.Click += new System.EventHandler(this.ViewOnGithub);
            //
            // rc4diyToolStripMenuItem
            //
            this.rc4diyToolStripMenuItem.Name = "rc4diyToolStripMenuItem";
            this.rc4diyToolStripMenuItem.Size = new System.Drawing.Size(183, 22);
            this.rc4diyToolStripMenuItem.Text = "Visit C4diy on Reddit";
            this.rc4diyToolStripMenuItem.Click += new System.EventHandler(this.VisitC4Diy);
            //
            // splitContainer3
            //
            this.splitContainer3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer3.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
            this.splitContainer3.IsSplitterFixed = true;
            this.splitContainer3.Location = new System.Drawing.Point(0, 24);
            this.splitContainer3.Name = "splitContainer3";
            this.splitContainer3.Orientation = System.Windows.Forms.Orientation.Horizontal;
            //
            // splitContainer3.Panel1
            //
            this.splitContainer3.Panel1.Margin = new System.Windows.Forms.Padding(5);
            this.splitContainer3.Panel1.Padding = new System.Windows.Forms.Padding(5, 5, 5, 0);
            //
            // splitContainer3.Panel2
            //
            this.splitContainer3.Panel2.Controls.Add(this.StatusTextLeft);
            this.splitContainer3.Panel2.Controls.Add(this.StatusTextRight);
            this.splitContainer3.Panel2.Padding = new System.Windows.Forms.Padding(4);
            this.splitContainer3.Panel2MinSize = 20;
            this.splitContainer3.Size = new System.Drawing.Size(504, 537);
            this.splitContainer3.SplitterDistance = 511;
            this.splitContainer3.SplitterWidth = 1;
            this.splitContainer3.TabIndex = 2;
            //
            // StatusTextLeft
            //
            this.StatusTextLeft.AutoSize = true;
            this.StatusTextLeft.BackColor = System.Drawing.Color.Transparent;
            this.StatusTextLeft.Dock = System.Windows.Forms.DockStyle.Left;
            this.StatusTextLeft.Location = new System.Drawing.Point(4, 4);
            this.StatusTextLeft.Name = "StatusTextLeft";
            this.StatusTextLeft.Size = new System.Drawing.Size(0, 13);
            this.StatusTextLeft.TabIndex = 0;
            this.StatusTextLeft.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // StatusTextRight
            //
            this.StatusTextRight.AutoSize = true;
            this.StatusTextRight.BackColor = System.Drawing.Color.Transparent;
            this.StatusTextRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.StatusTextRight.Location = new System.Drawing.Point(500, 4);
            this.StatusTextRight.Name = "StatusTextRight";
            this.StatusTextRight.Size = new System.Drawing.Size(0, 13);
            this.StatusTextRight.TabIndex = 1;
            this.StatusTextRight.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // MainWindow
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(504, 561);
            this.Controls.Add(this.splitContainer3);
            this.Controls.Add(this.menuStrip1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.menuStrip1;
            this.Name = "MainWindow";
            this.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
            this.Text = "Garry\'s Control4 Jailbreak";
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.OnFormClosed);
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            this.splitContainer3.Panel2.ResumeLayout(false);
            this.splitContainer3.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).EndInit();
            this.splitContainer3.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion
        private System.Windows.Forms.MenuStrip menuStrip1;
        private System.Windows.Forms.SplitContainer splitContainer3;
        private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem quitToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem helpToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem aboutToolStripMenuItem;
        private System.Windows.Forms.Label StatusTextLeft;
        private System.Windows.Forms.Label StatusTextRight;
        private System.Windows.Forms.ToolStripMenuItem foldersToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem composerFolderToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem composerSettingsToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem rc4diyToolStripMenuItem;
    }
}
