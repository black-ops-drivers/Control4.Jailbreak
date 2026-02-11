using System;
using System.Windows.Forms;

namespace Garry.Control4.Jailbreak.UI
{
    public class LoginDialog : Form
    {
        public string Username { get; private set; }
        public string Password { get; private set; }

        private TextBox _usernameTextBox;
        private TextBox _passwordTextBox;
        private Button _okButton;
        private Button _cancelButton;

        public LoginDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = @"Control4 Customer Login";
            Size = new System.Drawing.Size(350, 270);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var explanationLabel = new Label
            {
                Text = @"SSH password authentication has been disabled by a system update. " +
                       @"Your Control4 customer account credentials (from customer.control4.com) " +
                       @"are needed to authenticate with the controller's local API in order to " +
                       @"restore SSH access. Credentials are not stored.",
                Location = new System.Drawing.Point(12, 12),
                Size = new System.Drawing.Size(310, 70),
                AutoSize = false
            };

            var usernameLabel = new Label
            {
                Text = @"Email:",
                Location = new System.Drawing.Point(12, 85),
                Size = new System.Drawing.Size(150, 23)
            };

            _usernameTextBox = new TextBox
            {
                Location = new System.Drawing.Point(12, 105),
                Size = new System.Drawing.Size(310, 23),
                TabIndex = 0
            };

            var passwordLabel = new Label
            {
                Text = @"Password:",
                Location = new System.Drawing.Point(12, 135),
                Size = new System.Drawing.Size(150, 23)
            };

            _passwordTextBox = new TextBox
            {
                Location = new System.Drawing.Point(12, 155),
                Size = new System.Drawing.Size(310, 23),
                UseSystemPasswordChar = true,
                TabIndex = 1
            };

            _okButton = new Button
            {
                Text = @"OK",
                Location = new System.Drawing.Point(167, 195),
                Size = new System.Drawing.Size(75, 23),
                DialogResult = DialogResult.OK,
                TabIndex = 2
            };

            _cancelButton = new Button
            {
                Text = @"Cancel",
                Location = new System.Drawing.Point(247, 195),
                Size = new System.Drawing.Size(75, 23),
                DialogResult = DialogResult.Cancel,
                TabIndex = 3
            };

            _okButton.Click += OkButton_Click;

            Controls.Add(explanationLabel);
            Controls.Add(usernameLabel);
            Controls.Add(_usernameTextBox);
            Controls.Add(passwordLabel);
            Controls.Add(_passwordTextBox);
            Controls.Add(_okButton);
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_usernameTextBox.Text) || string.IsNullOrWhiteSpace(_passwordTextBox.Text))
            {
                MessageBox.Show(@"Please enter both email and password.", @"Validation Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Username = _usernameTextBox.Text.Trim();
            Password = _passwordTextBox.Text;
        }
    }
}