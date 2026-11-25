using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class Jailbreak : UserControl
    {
        private readonly MainWindow _mainWindow;
        private string _cachedJwtToken;
        private bool _loading;
        private string _directorVersion;
        private string _controllerCommonName;
        private string _lastCheckedIp;
        private readonly Timer _connectionTimer = new Timer { Interval = 10000 };
        private readonly Timer _debounceTimer = new Timer { Interval = 800 };

        // Split-button action mode. The arrow opens the menu to pick a mode; the button runs it.
        private enum ActionMode { Jailbreak = 0, Ssh = 1, Revert = 2 }
        private ActionMode _mode = ActionMode.Jailbreak;
        private System.Windows.Forms.ContextMenuStrip _modeMenu;
        private System.Windows.Forms.ToolStripMenuItem _miJailbreak;
        private System.Windows.Forms.ToolStripMenuItem _miSsh;
        private System.Windows.Forms.ToolStripMenuItem _miRevert;

        // Composer install path detection
        public string ComposerInstallDir { get; private set; }

        private string OpenSslExe => ComposerInstallDir != null
            ? Path.Combine(ComposerInstallDir, @"RemoteAccess\bin\openssl.exe")
            : null;

        private static readonly string[] WellKnownInstallPaths =
        {
            @"C:\Program Files (x86)\Control4\Composer\Pro",
            @"C:\Program Files\Control4\Composer\Pro"
        };

        private static string DetectComposerInstallDir()
        {
            foreach (var path in WellKnownInstallPaths)
            {
                if (ValidateInstallDir(path))
                    return path;
            }

            return DetectFromRegistry();
        }

        private static bool ValidateInstallDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            return File.Exists(Path.Combine(dir, "ComposerPro.exe")) &&
                   File.Exists(Path.Combine(dir, @"RemoteAccess\bin\openssl.exe"));
        }

        private static string DetectFromRegistry()
        {
            const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var views = new[] { RegistryView.Registry32, RegistryView.Registry64 };

            foreach (var view in views)
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(uninstallPath))
                    {
                        if (key == null) continue;
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var subKey = key.OpenSubKey(subKeyName))
                                {
                                    var displayName = subKey?.GetValue("DisplayName")?.ToString() ?? "";
                                    if (displayName.IndexOf("Composer Pro", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var installLocation = subKey?.GetValue("InstallLocation")?.ToString();
                                        if (!string.IsNullOrEmpty(installLocation) &&
                                            ValidateInstallDir(installLocation))
                                            return installLocation.TrimEnd('\\');
                                    }
                                }
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }

        public Jailbreak(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            InitializeComponent();
            checkBoxBlockSplitIo.CheckedChanged += checkBoxBlockSplitIo_CheckedChanged;
            Username.TextChanged += OnUsernameChanged;
            Password.TextChanged += OnPasswordChanged;
            _connectionTimer.Tick += (s, ev) => _ = CheckConnection();
            _debounceTimer.Tick += (s, ev) =>
            {
                _debounceTimer.Stop();
                _ = CheckConnection();
            };
            SetupModeUi();
            UpdateUpdatesButton();
            Load += Jailbreak_Load;
        }

        private void Jailbreak_Load(object sender, EventArgs e)
        {
            _loading = true;

            checkBoxBlockSplitIo.Checked = Properties.Settings.Default.BlockSplitIoChecked;

            var savedMode = Properties.Settings.Default.LastActionMode;
            _mode = System.Enum.IsDefined(typeof(ActionMode), savedMode)
                ? (ActionMode)savedMode
                : ActionMode.Jailbreak;
            UpdateActionUi();

            // Restore cached input values before auto-derivation kicks in
            var settings = Properties.Settings.Default;
            if (!string.IsNullOrEmpty(settings.LastIpAddress))
                IpAddress.Text = settings.LastIpAddress;
            if (!string.IsNullOrEmpty(settings.LastUsername))
                Username.Text = settings.LastUsername;
            if (!string.IsNullOrEmpty(settings.LastMacAddress))
                MacAddress.Text = settings.LastMacAddress;
            if (!string.IsNullOrEmpty(settings.LastPassword))
                Password.Text = settings.LastPassword;

            _loading = false;

            // Check connection to restored IP and start periodic refresh
            _ = CheckConnection();
            _connectionTimer.Start();

            // Auto-detect Composer install path
            var savedDir = settings.ComposerInstallDir;
            if (!string.IsNullOrEmpty(savedDir) && ValidateInstallDir(savedDir))
            {
                ComposerInstallDir = savedDir;
            }
            else
            {
                ComposerInstallDir = DetectComposerInstallDir();
            }
        }

        private void checkBoxBlockSplitIo_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.BlockSplitIoChecked = checkBoxBlockSplitIo.Checked;
            Properties.Settings.Default.Save();
        }

        // -------------------------------------------------------------------
        // Jailbreak button — the one-click orchestrator
        // -------------------------------------------------------------------

        private async void DoJailbreak(object sender, EventArgs e)
        {
            var actionTitle = ActionTitle(_mode);

            // Revert is destructive, so confirm it first.
            if (_mode == ActionMode.Revert)
            {
                var confirm = MessageBox.Show(FindForm(),
                    "Revert the jailbreak?\n\n" +
                    "This deletes the jailbreak's local files and hosts entries and, if the " +
                    "controller is reachable over SSH, strips the injected certificates and SSH " +
                    "key from the director, then offers to reboot it.\n\nContinue?",
                    actionTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                    return;
            }

            var log = new LogWindow(_mainWindow, actionTitle);
            var warnings = new List<string>();

            // Read controls here on the UI thread; the worker must not touch them.
            var inputs = new JailbreakInputs(
                IpAddress.Text,
                Username.Text,
                Password.Text,
                MacAddress.Text,
                checkBoxBlockSplitIo.Checked);

            // Stop the status poll so it doesn't hit the controller mid-patch or during reboot.
            buttonJailbreak.Enabled = false;
            _connectionTimer.Stop();
            try
            {
                await Task.Run(() =>
                {
                    switch (_mode)
                    {
                        case ActionMode.Ssh:
                            SshSetupWorker(inputs, log, warnings);
                            break;
                        case ActionMode.Revert:
                            UnjailbreakWorker(inputs, log, warnings);
                            break;
                        default:
                            JailbreakWorker(inputs, log, warnings);
                            break;
                    }
                });
            }
            finally
            {
                buttonJailbreak.Enabled = true;
                _connectionTimer.Start();
            }
        }

        // -------------------------------------------------------------------
        // Mode selector (Jailbreak / SSH Access / Revert)
        // -------------------------------------------------------------------

        private static string ActionTitle(ActionMode mode)
        {
            switch (mode)
            {
                case ActionMode.Ssh: return "Set Up SSH Access";
                case ActionMode.Revert: return "Revert";
                default: return "Jailbreak";
            }
        }

        private void SetupModeUi()
        {
            _miJailbreak = new System.Windows.Forms.ToolStripMenuItem(
                "Jailbreak", null, (s, e) => SetMode(ActionMode.Jailbreak));
            _miSsh = new System.Windows.Forms.ToolStripMenuItem(
                "SSH Access", null, (s, e) => SetMode(ActionMode.Ssh));
            _miRevert = new System.Windows.Forms.ToolStripMenuItem(
                "Revert", null, (s, e) => SetMode(ActionMode.Revert));
            _modeMenu = new System.Windows.Forms.ContextMenuStrip();
            _modeMenu.Items.Add(_miJailbreak);
            _modeMenu.Items.Add(_miSsh);
            _modeMenu.Items.Add(_miRevert);

            // buttonJailbreak is a SplitButton: the right-hand arrow zone opens the mode menu,
            // the rest runs the current mode. Reserve right padding so the label clears the arrow.
            buttonJailbreak.DropDownMenu = _modeMenu;
            buttonJailbreak.Padding = new System.Windows.Forms.Padding(
                buttonJailbreak.Padding.Left, buttonJailbreak.Padding.Top,
                SplitButton.ArrowZoneWidth, buttonJailbreak.Padding.Bottom);

            toolTip.SetToolTip(buttonJailbreak,
                "Click to run the selected action.\r\n" +
                "Click the arrow to switch between Jailbreak, SSH Access and Revert.");

            UpdateActionUi();
        }

        private void SetMode(ActionMode mode)
        {
            _mode = mode;
            Properties.Settings.Default.LastActionMode = (int)mode;
            Properties.Settings.Default.Save();
            UpdateActionUi();
        }

        private void UpdateActionUi()
        {
            string label;
            switch (_mode)
            {
                case ActionMode.Ssh: label = " Set Up SSH"; break;
                case ActionMode.Revert: label = " Revert"; break;
                default: label = " Jailbreak"; break;
            }

            buttonJailbreak.Text = label;

            if (_miJailbreak != null) _miJailbreak.Checked = _mode == ActionMode.Jailbreak;
            if (_miSsh != null) _miSsh.Checked = _mode == ActionMode.Ssh;
            if (_miRevert != null) _miRevert.Checked = _mode == ActionMode.Revert;

            // The split.io block only applies to a full jailbreak.
            checkBoxBlockSplitIo.Visible = _mode == ActionMode.Jailbreak;
        }

        // -------------------------------------------------------------------
        // SSH-only mode: set up / remove just root SSH access to the controller
        // -------------------------------------------------------------------

        private void ConnectToDirector(JailbreakInputs inputs, LogWindow log, out ScpClient scp, out SshClient ssh)
        {
            var connectionInfo = SshConnection(inputs);
            scp = new ScpClient(connectionInfo);
            ssh = new SshClient(connectionInfo);

            log.WriteNormal("Connecting to director via SSH/SCP... ");
            try
            {
                ConnectWithRetries(scp, log, "SCP");
                ConnectWithRetries(ssh, log, "SSH");
            }
            catch (Exception)
            {
                log.WriteWarning("failed — attempting to restore SSH access\n");

                log.WriteNormal("Restoring SSH password authentication... ");
                ApplySshRestoreExploit(inputs);
                log.WriteSuccess("done\n");

                log.WriteNormal("Reloading SSH service... ");
                ReloadSshService(inputs);
                log.WriteSuccess("done\n");

                log.WriteNormal("Waiting for SSH to reload... ");
                System.Threading.Thread.Sleep(1000);
                log.WriteSuccess("done\n");

                log.WriteNormal("Reconnecting via SSH/SCP... ");
                scp.Dispose();
                ssh.Dispose();
                connectionInfo = SshConnection(inputs);
                scp = new ScpClient(connectionInfo);
                ssh = new SshClient(connectionInfo);
                ConnectWithRetries(scp, log, "SCP");
                ConnectWithRetries(ssh, log, "SSH");
            }

            log.WriteSuccess("connected\n");
        }

        private void SshSetupWorker(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            try
            {
                log.WriteHeader("SSH ACCESS");

                if (string.IsNullOrWhiteSpace(inputs.MacAddress))
                {
                    log.WriteError("A controller MAC address is required to set up SSH access.\n");
                    return;
                }

                var localKeysFolder = $"{Constants.KeysFolder}/{inputs.MacAddress}";
                if (!Directory.Exists(localKeysFolder))
                    Directory.CreateDirectory(localKeysFolder);

                ScpClient scp = null;
                SshClient ssh = null;
                try
                {
                    ConnectToDirector(inputs, log, out scp, out ssh);
                    SyncControllerClock(log, ssh);

                    if (DownloadRootDeviceSshKeys(log, scp, localKeysFolder, warnings))
                        PatchDirectorySshAuthorizedKeysFile(log, scp, ssh, localKeysFolder);
                    else
                        log.WriteWarning("Could not download the controller's SSH keys — access not set up.\n");
                }
                finally
                {
                    scp?.Dispose();
                    ssh?.Dispose();
                }

                log.WriteHeader("DONE");
                log.WriteSuccess(
                    $"SSH access set up. Connect as root using the keys in {localKeysFolder}.\n");
                foreach (var warning in warnings)
                    log.WriteWarning(warning + "\n");
            }
            catch (Exception ex)
            {
                log.WriteError("SSH setup failed:\n");
                log.WriteError(ex);
            }
        }

        private void JailbreakWorker(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            try
            {
                // 1. Find Composer install dir
                if (!FindComposerInstallDir(log))
                    return;

                // 2. Validate prerequisites
                if (!File.Exists(OpenSslExe))
                {
                    log.WriteError($"Couldn't find {OpenSslExe} - is Composer installed?\n");
                    return;
                }

                if (!File.Exists(Constants.OpenSslConfig))
                {
                    log.WriteError($"Couldn't find {Constants.OpenSslConfig}\n");
                    return;
                }

                if (Process.GetProcessesByName(Constants.ComposerProcessName).Length > 0)
                {
                    log.WriteError("ComposerPro.exe is currently running. Please close Composer and try again.\n");
                    return;
                }

                // 3. Ensure root CA certs exist (idempotent via schema version)
                log.WriteHeader("ROOT CA");
                if (!EnsureRootCaCerts(log))
                    return;

                // 4. Generate Composer cert (always regenerate — short-lived)
                log.WriteHeader("COMPOSER CERTIFICATE");
                if (!GenerateComposerCert(log))
                    return;

                // 5. Ensure MQTT JWT signing keypair exists (OS 4.2+)
                log.WriteHeader("JWT SIGNING KEYPAIR");
                if (!EnsureJwtSigningKeyPair(log))
                    return;

                // 6. Patch ComposerPro.exe.config (idempotent)
                log.WriteHeader("PATCH CONFIG");
                PatchConfigFile(log);

                // 7-11. Deploy files + settings
                log.WriteHeader("DEPLOY FILES");

                var configFolder = GetComposerConfigFolder();
                DeployComposerFiles(log, configFolder);
                WriteFeatureFlags(log, configFolder);
                UpdateUpdateManagerSettings(log, configFolder);
                ConfigureSplitIoBlock(log, inputs.BlockSplitIo);
                BlockCloudServices(log);
                EnsureDealerAccount(log, configFolder);
                WriteLicenseFile(log, configFolder);

                // 11. Patch Director (SSH)
                log.WriteHeader("PATCH DIRECTOR");

                bool directorModified;
                try
                {
                    directorModified = PatchDirector(inputs, log, warnings);
                }
                catch (Exception ex)
                {
                    log.WriteError("Director patching failed:\n");
                    log.WriteError(ex);
                    log.WriteNormal(
                        "\nLocal Composer patching succeeded. Run the jailbreak again to retry director patching.\n");
                    return;
                }

                // Write MQTT JWT cache (OS 4.2+). Harmless on earlier OS — Composer only
                // consults this cache when the controller advertises SupportsMqtt.
                log.WriteHeader("JWT CACHE");
                WriteJwtCache(log, configFolder, _controllerCommonName);

                // 12. Reboot Director if cert chains were modified or a previous reboot is pending
                if (directorModified)
                {
                    log.WriteNormal("Director needs a reboot to apply certificate changes.\n");

                    DialogResult rebootChoice = DialogResult.None;

                    void AskReboot()
                    {
                        rebootChoice = MessageBox.Show(
                            FindForm(),
                            "The director needs to reboot for certificate changes to take effect.\n\n" +
                            "This will temporarily take your Control4 system offline.\n\n" +
                            "Reboot now?",
                            "Director Reboot Required",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                    }

                    if (InvokeRequired)
                        Invoke((Action)AskReboot);
                    else
                        AskReboot();

                    if (rebootChoice == DialogResult.Yes)
                    {
                        RebootDirector(inputs, log, warnings);
                        _directorVersion = null;
                    }
                    else
                    {
                        log.WriteNormal("Skipping reboot for now.\n");
                        warnings.Add(
                            "Director reboot is still needed for cert changes to take effect. " +
                            "Power cycle the controller or run the jailbreak again to be prompted.\n");
                    }
                }
                else
                {
                    log.WriteTrace("Director certs already up to date — no reboot needed.\n");
                }

                log.WriteHeader("DONE");
                log.WriteSuccess("Jailbreak complete!\n");
                log.WriteNormal(
                    "If Composer shows a registration renewal warning, close Composer and run this again.\n");
            }
            catch (Exception ex)
            {
                log.WriteError(ex);
            }
            finally
            {
                if (warnings.Count > 0)
                {
                    log.WriteHeader("WARNINGS");
                    for (var i = 0; i < warnings.Count; i++)
                    {
                        if (i > 0) log.WriteNormal("\n");
                        log.WriteWarning(warnings[i]);
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Composer install dir detection
        // -------------------------------------------------------------------

        private bool FindComposerInstallDir(LogWindow log)
        {
            if (ComposerInstallDir != null && ValidateInstallDir(ComposerInstallDir))
            {
                log.WriteNormal($"Composer install: {ComposerInstallDir}\n");
                return true;
            }

            // Try auto-detection
            var detected = DetectComposerInstallDir();
            if (detected != null)
            {
                ComposerInstallDir = detected;
                Properties.Settings.Default.ComposerInstallDir = detected;
                Properties.Settings.Default.Save();

                log.WriteNormal($"Auto-detected Composer at: {detected}\n");
                return true;
            }

            // Ask user to browse
            log.WriteNormal("Composer not found in standard locations. Please locate ComposerPro.exe.\n");

            string selectedDir = null;

            void AskUser()
            {
                using (var open = new OpenFileDialog())
                {
                    open.Filter = @"ComposerPro|ComposerPro.exe";
                    open.Title = @"Locate ComposerPro.exe";
                    open.InitialDirectory = @"C:\Program Files (x86)";
                    if (open.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(open.FileName))
                    {
                        selectedDir = Path.GetDirectoryName(open.FileName);
                    }
                }
            }

            if (InvokeRequired)
                Invoke((Action)AskUser);
            else
                AskUser();

            if (selectedDir == null)
            {
                log.WriteError("Cancelled.\n");
                return false;
            }

            if (!ValidateInstallDir(selectedDir))
            {
                log.WriteError($"{selectedDir} doesn't contain ComposerPro.exe and openssl.exe\n");
                return false;
            }

            ComposerInstallDir = selectedDir;
            Properties.Settings.Default.ComposerInstallDir = selectedDir;
            Properties.Settings.Default.Save();
            log.WriteNormal($"Using Composer at: {selectedDir}\n");
            return true;
        }

        // -------------------------------------------------------------------
        // Root CA certificate generation (idempotent via schema version)
        // -------------------------------------------------------------------

        private bool EnsureRootCaCerts(LogWindow log)
        {
            if (!Directory.Exists(Constants.CertsFolder))
            {
                log.WriteTrace($"Creating {Constants.CertsFolder} folder\n");
                Directory.CreateDirectory(Constants.CertsFolder);
            }

            var schemaFile = $"{Constants.CertsFolder}/.schema-version";
            var needsRegeneration = true;

            if (File.Exists($"{Constants.CertsFolder}/{Constants.CaPublicPemFileName}") &&
                File.Exists($"{Constants.CertsFolder}/private.key"))
            {
                if (File.Exists(schemaFile))
                {
                    var storedVersion = File.ReadAllText(schemaFile).Trim();
                    if (storedVersion == Constants.CertSchemaVersion.ToString())
                    {
                        log.WriteTrace("Root CA certs exist and schema version matches — skipping.\n");
                        needsRegeneration = false;
                    }
                    else
                    {
                        log.WriteNormal(
                            $"Schema version changed ({storedVersion} -> {Constants.CertSchemaVersion}) — regenerating.\n");
                    }
                }
                else
                {
                    log.WriteNormal("No schema version file — regenerating.\n");
                }
            }
            else
            {
                log.WriteNormal("Root CA certs not found — generating.\n");
            }

            if (needsRegeneration)
            {
                log.WriteNormal("Generating private + public keys...\n");
                var exitCode = RunProcessPrintOutput(
                    log,
                    OpenSslExe,
                    "req -new -x509 -sha256 -nodes " +
                    $"-days {Constants.CertificateExpireDays} " +
                    "-newkey rsa:2048 " +
                    $"-keyout \"{Constants.CertsFolder}/private.key\" " +
                    "-extensions v3_ca " +
                    "-subj \"/C=US/ST=Utah/L=Draper/O=Control4 Corporation/CN=Control4 Corporation CA/emailAddress=pki@control4.com/\" " +
                    $"-out \"{Constants.CertsFolder}/public.pem\""
                );

                if (exitCode != 0)
                {
                    log.WriteError("Root CA generation failed.\n");
                    return false;
                }

                File.WriteAllText(schemaFile, Constants.CertSchemaVersion.ToString());
                log.WriteSuccess("Root CA generated.\n");
            }

            log.WriteNormal($"Creating {Constants.ComposerCertName}... ");
            var output = RunProcessGetOutput(
                OpenSslExe,
                $"x509 -in \"{Constants.CertsFolder}/public.pem\" -text"
            );
            File.WriteAllText($"{Constants.CertsFolder}/{Constants.ComposerCertName}", output);
            log.WriteSuccess("done\n");

            return true;
        }

        // -------------------------------------------------------------------
        // Composer cert generation (always regenerated)
        // -------------------------------------------------------------------

        private bool GenerateComposerCert(LogWindow log)
        {
            log.WriteNormal("Creating signing request + key...\n");
            var exitCode = RunProcessPrintOutput(
                log,
                OpenSslExe,
                "req -new -nodes " +
                $"-newkey rsa:2048 -keyout {Constants.CertsFolder}/composer.key " +
                $"-subj \"/C=US/ST=Utah/L=Draper/CN={Constants.CertificateCn}\" " +
                $"-out {Constants.CertsFolder}/composer.csr"
            );

            if (exitCode != 0)
            {
                log.WriteError("Failed.\n");
                return false;
            }

            File.WriteAllText($"{Constants.CertsFolder}/ext.conf",
                @"[v3_client]\n" +
                $@"subjectAltName=DNS:{Constants.CertificateCn}\n" +
                @"extendedKeyUsage=clientAuth,serverAuth\n" +
                @"basicConstraints=CA:FALSE\n" +
                @"keyUsage=digitalSignature,keyEncipherment");

            log.WriteNormal("Signing certificate...\n");
            exitCode = RunProcessPrintOutput(
                log,
                OpenSslExe,
                "x509 -req " +
                $"-in {Constants.CertsFolder}/composer.csr " +
                $"-CA {Constants.CertsFolder}/public.pem " +
                $"-CAkey {Constants.CertsFolder}/private.key " +
                "-CAcreateserial " +
                $"-out {Constants.CertsFolder}/composer.pem " +
                "-days 1095 " +
                "-sha256 " +
                $"-extfile {Constants.CertsFolder}/ext.conf -extensions v3_client"
            );
            if (exitCode != 0)
            {
                log.WriteError("Failed.\n");
                return false;
            }

            log.WriteNormal("Creating composer.p12...\n");
            exitCode = RunProcessPrintOutput(
                log,
                OpenSslExe,
                "pkcs12 " +
                "-export " +
                $"-out \"{Constants.CertsFolder}/composer.p12\" " +
                $"-inkey \"{Constants.CertsFolder}/composer.key\" " +
                $"-in \"{Constants.CertsFolder}/composer.pem\" " +
                $"-certfile \"{Constants.CertsFolder}/public.pem\" " +
                $"-passout pass:{Constants.CertPassword}"
            );

            if (exitCode != 0)
            {
                log.WriteError("Failed.\n");
                return false;
            }

            log.WriteSuccess("Composer certificate generated.\n");
            return true;
        }

        // -------------------------------------------------------------------
        // JWT signing keypair — used to forge MQTT auth tokens on OS 4.2+
        // -------------------------------------------------------------------

        private bool EnsureJwtSigningKeyPair(LogWindow log)
        {
            var keyPath = $"{Constants.CertsFolder}/jailbreak_api.key";
            var pemPath = $"{Constants.CertsFolder}/{Constants.JailbreakApiPemFileName}";

            if (File.Exists(keyPath) && File.Exists(pemPath))
            {
                log.WriteTrace("JWT signing keypair already exists — skipping.\n");
                return true;
            }

            log.WriteNormal("Generating JWT signing private key...\n");
            var exitCode = RunProcessPrintOutput(log, OpenSslExe,
                $"genrsa -out \"{keyPath}\" 2048");
            if (exitCode != 0)
            {
                log.WriteError("Key generation failed.\n");
                return false;
            }

            log.WriteNormal("Generating JWT signing certificate...\n");
            exitCode = RunProcessPrintOutput(log, OpenSslExe,
                "req -new -x509 " +
                $"-key \"{keyPath}\" " +
                $"-out \"{pemPath}\" " +
                "-days 36500 " +
                "-subj \"/C=US/ST=Utah/L=Draper/O=Control4 Corporation/CN=composerexpress_auth/emailAddress=pki@control4.com\"");
            if (exitCode != 0)
            {
                log.WriteError("Certificate generation failed.\n");
                return false;
            }

            log.WriteSuccess("JWT signing keypair generated.\n");
            return true;
        }

        // -------------------------------------------------------------------
        // Config file patching (dead proxy + bypasslist)
        // -------------------------------------------------------------------

        // Injects a dead <defaultProxy> (127.0.0.1:31337) that blackholes managed HTTP,
        // with a bypasslist for the update endpoints. On 2026.x the online cert/auth check is
        // made by the new HttpClient-based REST client, which ignores the config proxy; that
        // path is handled by the hosts block (BlockCloudServices) instead. But older Composer
        // makes its license/dealer calls over managed HttpClient, which DOES honor this proxy,
        // so we keep injecting it for backwards-compatibility with older installs.
        private void PatchConfigFile(LogWindow log)
        {
            var configPath = Path.Combine(ComposerInstallDir, Constants.ComposerConfigFileName);
            if (!File.Exists(configPath))
            {
                log.WriteError($"Config file not found: {configPath}\n");
                return;
            }

            log.WriteNormal($"Patching {configPath}...\n");

            try
            {
                var xmlDoc = XDocument.Load(configPath);

                var systemNet = xmlDoc.Root?.Element("system.net");
                if (systemNet == null)
                {
                    log.WriteError("Could not find the <system.net> node in the configuration file.\n");
                    return;
                }

                var desiredProxy = new XElement("defaultProxy",
                    new XElement("proxy",
                        new XAttribute("usesystemdefault", "false"),
                        new XAttribute("proxyaddress", "http://127.0.0.1:31337/"),
                        new XAttribute("bypassonlocal", "true")
                    ),
                    new XElement("bypasslist",
                        new XElement("add",
                            new XAttribute("address", @"services\.control4\.com")),
                        new XElement("add",
                            new XAttribute("address", @"update2\.control4\.com")),
                        new XElement("add",
                            new XAttribute("address", @"c4updates\.control4\.com"))
                    )
                );

                var existingProxy = systemNet.Element("defaultProxy");
                if (existingProxy != null &&
                    XNode.DeepEquals(existingProxy, desiredProxy))
                {
                    log.WriteTrace("Config file already patched.\n");
                    return;
                }

                existingProxy?.Remove();
                systemNet.Add(desiredProxy);

                // Only back up once — skip if a backup already exists
                var backupPath = configPath + ".backup";
                if (!File.Exists(backupPath))
                {
                    log.WriteNormal("Writing backup...\n");
                    File.Copy(configPath, backupPath);
                }

                log.WriteNormal("Writing patched config...\n");
                xmlDoc.Save(configPath);

                log.WriteSuccess("Config file patched.\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"Config patch error: {ex.Message}\n");
            }
        }

        // Removes any dead <defaultProxy> from the config. Used by the unjailbreak path to
        // undo PatchConfigFile.
        private void RemoveDeadProxyFromConfig(LogWindow log)
        {
            var configPath = Path.Combine(ComposerInstallDir, Constants.ComposerConfigFileName);
            if (!File.Exists(configPath))
            {
                log.WriteError($"Config file not found: {configPath}\n");
                return;
            }

            try
            {
                var xmlDoc = XDocument.Load(configPath);
                var existingProxy = xmlDoc.Root?.Element("system.net")?.Element("defaultProxy");
                if (existingProxy == null)
                {
                    log.WriteTrace("No dead proxy in config; nothing to do.\n");
                    return;
                }

                log.WriteNormal($"Removing dead proxy from {configPath}...\n");
                existingProxy.Remove();
                xmlDoc.Save(configPath);
                log.WriteSuccess("Dead proxy removed.\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"Config patch error: {ex.Message}\n");
            }
        }

        // -------------------------------------------------------------------
        // Composer file deployment + settings
        // -------------------------------------------------------------------

        private static string GetComposerConfigFolder()
        {
            var configFolder = $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}/Control4";
            Directory.CreateDirectory($"{configFolder}/Composer");
            return configFolder;
        }

        private static void DeployComposerFiles(LogWindow log, string configFolder)
        {
            CopyFile(log, $"{Constants.CertsFolder}/{Constants.ComposerCertName}",
                $"{configFolder}/Composer/{Constants.ComposerCertName}");
            CopyFile(log, $"{Constants.CertsFolder}/{Constants.ComposerP12FileName}", $"{configFolder}/Composer/{Constants.ComposerP12FileName}");
        }

        private static void WriteFeatureFlags(LogWindow log, string configFolder)
        {
            WriteFile(log, $"{configFolder}/Composer/{Constants.FeaturesConfigFileName}",
                @"{" +
                @"""composer-x4-updatemanger-restrict-override"":{""Result"":true,""Config"":null}," +
                @"""connection-whitelist"":{""Result"":false,""Config"":""[]""}," +
                @"""os-pack-on-connect"":{""Result"":true,""Config"":null}" +
                @"}");
        }

        private static void UpdateUpdateManagerSettings(LogWindow log, string configFolder)
        {
            log.WriteNormal("Setting Update Manager URL... ");
            var settingsPath = $"{configFolder}/Composer/{Constants.UpdateManagerSettingsFileName}";
            try
            {
                var settingsDoc = File.Exists(settingsPath)
                    ? XDocument.Load(settingsPath)
                    : XDocument.Parse("<settings/>");

                var root = settingsDoc.Root ?? new XElement("settings");
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                XNamespace xsd = "http://www.w3.org/2001/XMLSchema";

                root.Element("UpdateURLList30")?.Remove();
                root.Add(new XElement("UpdateURLList30",
                    new XAttribute("type", "System.Collections.ArrayList"),
                    new XElement("ArrayOfAnyType",
                        new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                        new XAttribute(XNamespace.Xmlns + "xsd", xsd),
                        new XElement("anyType",
                            new XAttribute(xsi + "type", "xsd:string"),
                            Constants.UpdatesExperienceUrl))));

                settingsDoc.Save(settingsPath);
                log.WriteSuccess("done\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"Could not update settings: {ex.Message}\n");
                log.WriteWarning($"Manually enter this URL in the Update Manager:\n");
                log.WriteNormal($"  {Constants.UpdatesExperienceUrl}\n");
            }
        }

        // On 2026.x, once the locator (apis.control4.com) reports online, Composer reauthorizes the
        // Composer license via ReauthorizeComposerPro on services.control4.com; the reauth is rejected
        // for the jailbreak's dealer account and the modal "Register Composer" window closes the app.
        // That call ignores the config defaultProxy, so services.control4.com is blocked at the hosts
        // level here, which leaves Composer to fall back to the local license.xml. The locator
        // (apis.control4.com) and the REST cloud API (apiz.control4.com) are left reachable so
        // OnlineServicesAvailable stays true and the online driver add/update actions stay enabled.
        // Also strips the legacy locator block left by earlier versions. Not tied to a checkbox
        // because the jailbreak can't function without it.
        private static void BlockCloudServices(LogWindow log)
        {
            RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockCloudLocatorHostsEntry);
            AddLineToFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
        }

        private static void ConfigureSplitIoBlock(LogWindow log, bool blockSplitIo)
        {
            if (blockSplitIo)
            {
                AddLineToFile(log, Constants.WindowsHostsFile, Constants.BlockSplitIoHostsEntry);
            }
            else
            {
                RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockSplitIoHostsEntry);
            }
        }

        private static void WriteLicenseFile(LogWindow log, string configFolder)
        {
            // Composer 2026.x shows the "Register Composer" dialog at startup if this file is
            // missing. FileManager/DealerAccountService look for it at %AppData%\Control4\license.xml
            // (alongside dealeraccount.xml), NOT under the Composer subfolder.
            // Contents aren't validated when OnlineServicesAvailable=false — existence is enough.
            var path = $"{configFolder}/{Constants.LicenseFileName}";
            if (File.Exists(path))
            {
                log.WriteTrace("license.xml already exists\n");
                return;
            }

            WriteFile(log, path, @"<?xml version=""1.0"" encoding=""utf-8""?>
<License>
  <Name>Composer Pro</Name>
  <Code>ProLicense</Code>
  <Expiration>2099-01-01</Expiration>
  <Status>Active</Status>
  <Purchased>2024-01-01</Purchased>
</License>");
        }

        private static void EnsureDealerAccount(LogWindow log, string configFolder)
        {
            log.WriteNormal("Checking dealer account... ");
            if (!File.Exists($"{configFolder}/{Constants.DealerAccountFileName}"))
            {
                log.WriteNormal("creating\n");
                WriteFile(log, $"{configFolder}/{Constants.DealerAccountFileName}", @"<?xml version=""1.0"" encoding=""utf-8""?>
<DealerAccount>
  <Username>no</Username>
  <Employee>False</Employee>
  <Password>+bJjU5zcsEI=</Password>
  <UserHash>9390298f3fb0c5b160498935d79cb139aef28e1c47358b4bbba61862b9c26e59</UserHash>
</DealerAccount>");
            }
            else
            {
                log.WriteTrace("already exists\n");
            }
        }

        // -------------------------------------------------------------------
        // SSH / Director patching — returns true if cert chains were modified
        // -------------------------------------------------------------------

        /// <summary>
        /// Immutable snapshot of the UI inputs a jailbreak run needs, captured on the UI
        /// thread before the work moves to a background thread.
        /// </summary>
        private sealed class JailbreakInputs
        {
            public JailbreakInputs(string ipAddress, string username, string password, string macAddress,
                bool blockSplitIo)
            {
                IpAddress = ipAddress;
                Username = username;
                Password = password;
                MacAddress = macAddress;
                BlockSplitIo = blockSplitIo;
            }

            public string IpAddress { get; }
            public string Username { get; }
            public string Password { get; }
            public string MacAddress { get; }
            public bool BlockSplitIo { get; }
        }

        private ConnectionInfo SshConnection(JailbreakInputs inputs)
        {
            var authMethods = new List<AuthenticationMethod>
            {
                new PasswordAuthenticationMethod(inputs.Username, inputs.Password),
                new PasswordAuthenticationMethod(inputs.Username, "t0talc0ntr0l4!")
            };

            var privateKeyFiles = new List<IPrivateKeySource>();
            if (Directory.Exists(Constants.KeysFolder))
            {
                foreach (var folder in Directory.EnumerateDirectories(Constants.KeysFolder))
                {
                    var sshKeyFiles = Directory.EnumerateFiles(folder, "ssh_host_*_key");
                    foreach (var sshKeyFile in sshKeyFiles)
                    {
                        privateKeyFiles.Add(new PrivateKeyFile(sshKeyFile));
                    }
                }
            }

            if (privateKeyFiles.Count > 0)
            {
                authMethods.Add(new PrivateKeyAuthenticationMethod("root", privateKeyFiles.ToArray()));
            }

            var sshConnectionInfo = new ConnectionInfo(
                inputs.IpAddress,
                inputs.Username,
                authMethods.ToArray()
            )
            {
                RetryAttempts = 3,
                Timeout = TimeSpan.FromSeconds(15)
            };

            return sshConnectionInfo;
        }

        /// <summary>
        /// Retries the initial connect (ConnectionInfo.RetryAttempts only covers channel-open,
        /// not the connect). Only network transients are retried; auth/protocol failures are
        /// deterministic, so they fail fast to let the caller's SSH-restore path run instead.
        /// </summary>
        private static void ConnectWithRetries(BaseClient client, LogWindow log, string label, int maxAttempts = 3)
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    client.Connect();
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= maxAttempts || !IsTransientConnectFailure(ex))
                        throw;

                    log.WriteWarning(
                        $"\n  [{label}] connect attempt {attempt}/{maxAttempts} failed ({ex.Message}) — retrying...\n");
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }
        }

        private static bool IsTransientConnectFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Net.Sockets.SocketException) return true;
                if (e is SshOperationTimeoutException) return true;
            }

            return false;
        }

        /// <summary>
        /// Patches the director's cert chains via SSH.
        /// Returns true if a reboot is needed (either from new cert changes or a previous pending reboot).
        /// </summary>
        private bool PatchDirector(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            if (!File.Exists($"{Constants.CertsFolder}/{Constants.CaPublicPemFileName}"))
            {
                log.WriteError(
                    $"Couldn't find {Constants.CertsFolder}/public.pem - have you generated certificates?\n");
                return false;
            }

            var macAddress = inputs.MacAddress;
            var localKeysFolder = $"{Constants.KeysFolder}/{macAddress}";
            if (!string.IsNullOrEmpty(macAddress))
            {
                if (!Directory.Exists(localKeysFolder))
                {
                    log.WriteTrace($"Creating {localKeysFolder} Folder\n");
                    Directory.CreateDirectory(localKeysFolder);
                }
            }

            var anyModified = false;
            ScpClient scp = null;
            SshClient ssh = null;

            try
            {
                // One SCP client (files) + one SSH client (commands), reused by every helper
                // below instead of reconnecting per operation.
                var connectionInfo = SshConnection(inputs);
                scp = new ScpClient(connectionInfo);
                ssh = new SshClient(connectionInfo);

                log.WriteNormal("Connecting to director via SSH/SCP... ");
                try
                {
                    ConnectWithRetries(scp, log, "SCP");
                    ConnectWithRetries(ssh, log, "SSH");
                }
                catch (Exception)
                {
                    log.WriteWarning("failed — attempting to restore SSH access\n");

                    log.WriteNormal("Restoring SSH password authentication... ");
                    ApplySshRestoreExploit(inputs);
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Reloading SSH service... ");
                    ReloadSshService(inputs);
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Waiting for SSH to reload... ");
                    System.Threading.Thread.Sleep(1000);
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Reconnecting via SSH/SCP... ");
                    scp.Dispose();
                    ssh.Dispose();
                    connectionInfo = SshConnection(inputs);
                    scp = new ScpClient(connectionInfo);
                    ssh = new SshClient(connectionInfo);
                    ConnectWithRetries(scp, log, "SCP");
                    ConnectWithRetries(ssh, log, "SSH");
                }

                log.WriteSuccess("connected\n");

                _controllerCommonName = FetchControllerCommonName(scp);

                SyncControllerClock(log, ssh);

                if (DownloadRootDeviceSshKeys(log, scp, localKeysFolder, warnings))
                {
                    PatchDirectorySshAuthorizedKeysFile(log, scp, ssh, localKeysFolder);
                }

                log.WriteNormal($"Reading {Constants.CertsFolder}/public.pem... ");
                var localCert = File.ReadAllText($"{Constants.CertsFolder}/{Constants.CaPublicPemFileName}").Trim();
                log.WriteSuccess("done\n");

                anyModified |= PatchRemoteCertChain(log, scp, ssh, Constants.OpenVpnClientCaProdPem, localCert);
                anyModified |=
                    PatchRemoteCertChain(log, scp, ssh, Constants.ControllerClientCaProdPem, localCert);
                anyModified |= PatchRemoteCertChain(log, scp, ssh, Constants.MosquittoCaChainPem, localCert);

                PatchControllerApiPem(log, scp, ssh);

                if (anyModified)
                {
                    // Write reboot marker — /tmp is cleared on reboot
                    log.WriteTrace("Writing reboot marker...\n");
                    UploadFile(scp, ssh, Constants.RebootMarkerPath, DateTime.UtcNow.ToString("o"));
                }
                else
                {
                    // Check if a previous reboot is still pending
                    try
                    {
                        DownloadFile(scp, Constants.RebootMarkerPath);
                        log.WriteNormal("Previous reboot is still pending.\n");
                        anyModified = true;
                    }
                    catch (ScpException)
                    {
                        // Marker gone — controller was rebooted
                    }
                }
            }
            finally
            {
                scp?.Dispose();
                ssh?.Dispose();
            }

            return anyModified;
        }

        /// <summary>
        /// Appends jailbreak_api.pem to /opt/control4/etc/ssl/certs/api.pem so the mosquitto-jwt-auth
        /// plugin (OS 4.2+) accepts JWTs signed with our keypair. Unlike the CA chain patches,
        /// this is a per-cert append (the plugin's pem_parser iterates every BEGIN CERTIFICATE block)
        /// and we must kill the daemon to pick up the new key — it only reads the PEM at startup.
        /// No reboot required — sysmand respawns the daemon within ~10 seconds.
        ///
        /// Dedupe strategy: strip any self-signed cert with CN=composerexpress_auth before appending,
        /// which cleans up orphaned jailbreak certs from previous runs where the local keypair was
        /// regenerated. The production cert shares the CN but is issued by "Control4 Primary Root CA"
        /// (Subject != Issuer), so it survives the filter.
        /// </summary>
        private static void PatchControllerApiPem(LogWindow log, ScpClient scp, SshClient ssh)
        {
            const string remoteFile = Constants.ControllerApiPem;
            var localPemPath = $"{Constants.CertsFolder}/{Constants.JailbreakApiPemFileName}";

            log.WriteNormal($"Patching {remoteFile}:\n");

            if (!File.Exists(localPemPath))
            {
                log.WriteWarning($"  {localPemPath} missing — skipping\n");
                return;
            }

            string remotePem;
            try
            {
                log.WriteNormal($"  Downloading {remoteFile}... ");
                remotePem = DownloadFile(scp, remoteFile);
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist (pre-OS 4.2) — skipping\n");
                return;
            }

            var ourPem = File.ReadAllText(localPemPath).Trim();

            var kept = ExtractPemBlocks(remotePem)
                .Where(pem => !IsOrphanJailbreakCert(pem))
                .ToList();

            var rebuilt = string.Join("\n", kept.Concat(new[] { ourPem })).Trim() + "\n";
            var original = string.Join("\n", ExtractPemBlocks(remotePem)).Trim() + "\n";

            if (rebuilt == original)
            {
                log.WriteTrace("  (already patched)\n");
                return;
            }

            var backupFilename = $"api.pem.{DateTime.Now:yyyy-dd-M--HH-mm-ss}.backup";
            log.WriteNormal($"  Saving remote backup to /opt/control4/etc/ssl/certs/{backupFilename}... ");
            UploadFile(scp, ssh, $"{Constants.ControllerSslCertsDir}/{backupFilename}", remotePem);
            log.WriteSuccess("done\n");

            log.WriteNormal($"  Saving local backup to {Constants.CertsFolder}/{backupFilename}... ");
            File.WriteAllText($"{Constants.CertsFolder}/{backupFilename}", remotePem);
            log.WriteSuccess("done\n");

            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, ssh, remoteFile, rebuilt);
            log.WriteSuccess("done\n");

            log.WriteNormal("  Restarting mosquitto-jwt-auth... ");
            // Plugin loads the PEM only at startup; sysmand respawns the process within ~10s.
            var restartResult = ssh.RunCommand($"pidof {Constants.MosquittoJwtAuthProcess} | xargs -r kill -9");
            if (restartResult.ExitStatus != 0)
            {
                log.WriteError($"failed (exit {restartResult.ExitStatus})\n");
                return;
            }

            log.WriteSuccess("done\n");
        }

        private static IEnumerable<string> ExtractPemBlocks(string pemText)
        {
            const string beginMarker = "-----BEGIN CERTIFICATE-----";
            const string endMarker = "-----END CERTIFICATE-----";
            var startIdx = 0;
            while ((startIdx = pemText.IndexOf(beginMarker, startIdx, StringComparison.Ordinal)) >= 0)
            {
                var endIdx = pemText.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
                if (endIdx < 0) yield break;
                yield return pemText.Substring(startIdx, endIdx - startIdx + endMarker.Length).Trim();
                startIdx = endIdx + endMarker.Length;
            }
        }

        private static bool IsOrphanJailbreakCert(string pem)
        {
            try
            {
                var cert = new X509Certificate2(Encoding.UTF8.GetBytes(pem));
                var isSelfSigned = cert.Subject == cert.Issuer;
                var isOurCn = cert.Subject.IndexOf("CN=composerexpress_auth", StringComparison.Ordinal) >= 0;
                return isSelfSigned && isOurCn;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the cert chain was actually modified, false if already patched or the file doesn't exist.
        /// </summary>
        private static bool PatchRemoteCertChain(LogWindow log, ScpClient scp, SshClient ssh, string remoteFile,
            string localCert)
        {
            log.WriteNormal($"Patching {remoteFile}:\n");

            string remoteCertChain;
            try
            {
                log.WriteNormal($"  Downloading {remoteFile}... ");
                remoteCertChain = DownloadFile(scp, remoteFile).Trim();
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist - skipping\n");
                return false;
            }

            var dedupedRemoteCertChain = DedupeX509CertChain(remoteCertChain);
            if (remoteCertChain == dedupedRemoteCertChain && remoteCertChain.Contains(localCert))
            {
                log.WriteTrace("  (already patched)\n");
                return false;
            }

            var directory = (Path.GetDirectoryName(remoteFile) ?? "").Replace("\\", "/");
            var fileName = Path.GetFileName(remoteFile);
            var backupFilename = $"{fileName}.{DateTime.Now:yyyy-dd-M--HH-mm-ss}.backup";

            log.WriteNormal($"  Saving remote backup to {directory}/{backupFilename}... ");
            UploadFile(scp, ssh, $"{directory}/{backupFilename}", remoteCertChain);
            log.WriteSuccess("done\n");

            log.WriteNormal($"  Saving local backup to {Constants.CertsFolder}/{backupFilename}... ");
            File.WriteAllText($"{Constants.CertsFolder}/{backupFilename}", remoteCertChain);
            log.WriteSuccess("done\n");

            remoteCertChain = DedupeX509CertChain(dedupedRemoteCertChain + "\n" + localCert);

            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, ssh, remoteFile, remoteCertChain);
            log.WriteSuccess("done\n");

            return true;
        }

        private static string DedupeX509CertChain(string certChain)
        {
            // Extract complete PEM certificate blocks, skipping any truncated/malformed entries
            var certs = new List<string>();
            const string beginMarker = "-----BEGIN CERTIFICATE-----";
            const string endMarker = "-----END CERTIFICATE-----";
            var startIdx = 0;
            while ((startIdx = certChain.IndexOf(beginMarker, startIdx, StringComparison.Ordinal)) >= 0)
            {
                var endIdx = certChain.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
                if (endIdx < 0) break; // Truncated cert — skip
                var pem = certChain.Substring(startIdx, endIdx - startIdx + endMarker.Length).Trim();
                certs.Add(pem);
                startIdx = endIdx + endMarker.Length;
            }

            return string.Join("\n", certs
                    .GroupBy(cert =>
                    {
                        try
                        {
                            return new X509Certificate2(Encoding.UTF8.GetBytes(cert)).Subject;
                        }
                        catch
                        {
                            return cert;
                        } // Fallback: keep unparseable certs, dedupe by content
                    })
                    .Select(group => group.Last()))
                .Trim();
        }

        private static bool DownloadRootDeviceSshKeys(LogWindow log, ScpClient scp, string localKeysFolder,
            List<string> warnings)
        {
            log.WriteNormal("Downloading ssh keys from device:\n");
            var files = new List<string>
            {
                "ssh_host_rsa_key",
                "ssh_host_rsa_key.pub",
                "ssh_host_ed25519_key",
                "ssh_host_ed25519_key.pub"
            };
            var keysDownloaded = false;
            var keysExist = false;
            foreach (var file in files)
            {
                var localFile = $"{localKeysFolder}/{file}";
                var remoteFile = $"{Constants.ControllerSshDir}/{file}";
                log.WriteNormal($"  Downloading {remoteFile}... ");

                string key;
                try
                {
                    key = DownloadFile(scp, remoteFile);
                }
                catch (ScpException)
                {
                    log.WriteTrace("ignoring - file doesnt exist\n");
                    continue;
                }

                keysExist = true;

                if (File.Exists(localFile) && File.ReadAllText(localFile) == key)
                {
                    log.WriteTrace("already exists\n");
                    continue;
                }

                File.WriteAllText(localFile, key);
                log.WriteSuccess("done\n");
                keysDownloaded = true;
            }

            if (keysDownloaded)
            {
                warnings.Add(
                    $"If you lose the '{Constants.KeysFolder}' folder, connecting to X4 systems " +
                    "becomes more difficult! While SSH password authentication can currently be " +
                    "restored using customer credentials, this capability may not always be " +
                    "available in future firmware versions. Back up this folder somewhere safe.\n");
            }

            return keysExist;
        }

        private static void PatchDirectorySshAuthorizedKeysFile(LogWindow log, ScpClient scp, SshClient ssh,
            string localKeysFolder)
        {
            var localPubKeyFiles = new List<string>
            {
                $"{localKeysFolder}/{Constants.SshHostRsaPubKey}",
                $"{localKeysFolder}/{Constants.SshHostEd25519PubKey}"
            }.Where(File.Exists).ToArray();
            if (localPubKeyFiles.Length == 0)
            {
                return;
            }

            var localAuthorizedKeysFile = $"{localKeysFolder}/authorized_keys";
            const string remoteAuthorizedKeysFile = Constants.ControllerAuthorizedKeys;

            log.WriteNormal("Patching authorized_keys file on director:\n");
            var localPubKeys = new List<string>();
            foreach (var localPubKeyFile in localPubKeyFiles)
            {
                if (!File.Exists(localPubKeyFile))
                {
                    continue;
                }

                log.WriteNormal($"  Reading {localPubKeyFile}... ");
                localPubKeys.Add(File.ReadAllText(localPubKeyFile).Trim());
                log.WriteSuccess("done\n");
            }

            log.WriteNormal($"  Downloading {remoteAuthorizedKeysFile}... ");
            var authorizedKeys = "";
            try
            {
                authorizedKeys = DownloadFile(scp, remoteAuthorizedKeysFile);
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("ignoring - file doesnt exist\n");
            }

            if (localPubKeys.All(localPubKey => authorizedKeys.Contains(localPubKey)))
            {
                log.WriteTrace("  (already patched)\n");
                return;
            }

            var backupSuffix = $".{DateTime.Now:yyyy-dd-M--HH-mm-ss}.backup";

            log.WriteNormal($"  Saving remote backup to {remoteAuthorizedKeysFile}{backupSuffix}... ");
            UploadFile(scp, ssh, $"{remoteAuthorizedKeysFile}{backupSuffix}", authorizedKeys);
            log.WriteSuccess("done\n");

            log.WriteNormal($"  Saving local backup to {localAuthorizedKeysFile}{backupSuffix}... ");
            File.WriteAllText($"{localAuthorizedKeysFile}{backupSuffix}", authorizedKeys);
            log.WriteSuccess("done\n");

            foreach (var localPubKey in localPubKeys)
            {
                if (!authorizedKeys.Contains(localPubKey))
                {
                    authorizedKeys = authorizedKeys.Trim() + "\n" + localPubKey.Trim();
                }
            }

            log.WriteNormal("  Updating remote authorized_keys file... ");
            UploadFile(scp, ssh, remoteAuthorizedKeysFile, authorizedKeys);
            log.WriteSuccess("done\n");
        }

        private void RebootDirector(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            try
            {
                log.WriteNormal("Connecting to director... ");

                using (var ssh = new SshClient(SshConnection(inputs)))
                {
                    ConnectWithRetries(ssh, log, "SSH");
                    log.WriteSuccess("connected\n");

                    log.WriteNormal("Running reboot command... ");
                    ssh.RunCommand("nohup sh -c '( sleep 2 ; reboot )' >/dev/null 2>&1 &");
                    log.WriteSuccess("done\n");

                    warnings.Add(
                        "Your system is rebooting — it can take a while. Don't panic, give it 10 minutes!\n");
                }
            }
            catch (Exception ex)
            {
                log.WriteError("Reboot failed:\n");
                log.WriteError(ex);
            }
        }

        /// <summary>
        /// Checks the controller's clock and corrects it if it's more than two minutes off.
        /// A wrong clock (e.g. dead CMOS battery) causes TLS certificate validation failures.
        /// </summary>
        private static void SyncControllerClock(LogWindow log, SshClient ssh)
        {
            try
            {
                var result = ssh.RunCommand("date +%s");
                if (result.ExitStatus != 0 || !long.TryParse(result.Result.Trim(), out var remoteEpoch))
                {
                    return;
                }

                var localEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var drift = Math.Abs(localEpoch - remoteEpoch);

                if (drift > 120) // More than 2 minutes off
                {
                    var utcNow = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                    log.WriteNormal(
                        $"Controller clock is off by {TimeSpan.FromSeconds(drift):d'd 'h'h 'm'm'} — correcting... ");
                    var dateResult = ssh.RunCommand($"date -u -s \"{utcNow}\"");
                    if (dateResult.ExitStatus != 0)
                    {
                        log.WriteWarning($"failed (exit {dateResult.ExitStatus})\n");
                    }
                    else
                    {
                        log.WriteSuccess("done\n");
                    }
                }

                log.WriteTrace("Syncing hardware clock... ");
                var hwclockResult = ssh.RunCommand("hwclock -w 2>/dev/null");
                log.WriteTrace(hwclockResult.ExitStatus != 0
                    ? $"(failed with exit {hwclockResult.ExitStatus})\n"
                    : "done\n");
            }
            catch
            {
                // Non-fatal — don't block the jailbreak if clock sync fails
            }
        }

        /// <summary>
        /// Fetches the controller's common name (control4_{model}_{mac}) by reading the subject CN
        /// from its own cert. This is what Composer uses to look up the JWT cache and what the
        /// mosquitto-jwt-auth plugin expects in the signed JWT's CommonName claim.
        /// NOTE: the Linux hostname uses a different format (hyphen-separated, no model prefix)
        /// and is NOT what we want.
        /// </summary>
        private static string FetchControllerCommonName(ScpClient scp)
        {
            // agent.pem and client.pem both carry the controller's CN; agent.pem is the canonical one.
            // On OS 4.2.1 those moved: agent.pem is a dangling symlink to a now-absent client.pem,
            // and the device cert lives at /opt/control4/etc/certs/cvm-device.pem instead. Try the
            // legacy paths first (unchanged for older OS), then fall back to the 4.2.1 location.
            foreach (var remote in new[]
                     {
                         Constants.ControllerAgentPem,
                         Constants.ControllerClientPem,
                         Constants.ControllerCvmDevicePem
                     })
            {
                try
                {
                    var pem = DownloadFile(scp, remote);
                    var block = ExtractPemBlocks(pem).FirstOrDefault();
                    if (block == null) continue;
                    var cert = new X509Certificate2(Encoding.UTF8.GetBytes(block));
                    var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    if (!string.IsNullOrEmpty(cn)) return cn;
                }
                catch
                {
                    // try next
                }
            }

            return null;
        }

        // -------------------------------------------------------------------
        // JWT cache (OS 4.2+ — bypasses the ControllersForm cloud-status gate)
        // -------------------------------------------------------------------

        private bool WriteJwtCache(LogWindow log, string configFolder, string commonName)
        {
            if (string.IsNullOrEmpty(commonName))
            {
                log.WriteWarning("Controller common name unknown — skipping JWT cache write.\n");
                return false;
            }

            var cacheDir = $"{configFolder}/JwtCache";
            Directory.CreateDirectory(cacheDir);
            var cachePath = $"{cacheDir}/{commonName}.jwtcache";

            // Idempotency: skip if a non-stale entry is already present.
            if (File.Exists(cachePath) && IsJwtCacheFresh(cachePath))
            {
                log.WriteTrace($"JWT cache for {commonName} already fresh — skipping.\n");
                return true;
            }

            var dealerUsername = ReadDealerUsername(configFolder) ?? "no";
            log.WriteNormal($"Signing MQTT JWT for {commonName}... ");

            var issuedAt = DateTimeOffset.UtcNow;
            var expiry = issuedAt.AddDays(Constants.JwtExpireDays);

            var jwt = SignMqttJwt(log, commonName, issuedAt, expiry);
            if (jwt == null)
            {
                log.WriteError("failed\n");
                return false;
            }

            log.WriteSuccess("done\n");

            // expiresUtc must match the JWT's real "exp" claim — Composer caches the token
            // until this date, so lying with a far-future value leaves an expired token in
            // place that the controller silently rejects. Re-running the jailbreak re-signs.
            var json =
                "{\"entries\":{\"" + JsonEscape(dealerUsername) + "\":{" +
                "\"token\":\"" + jwt + "\"," +
                "\"expiresUtc\":\"" + expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}}";

            WriteFile(log, cachePath, json);
            return true;
        }

        /// <summary>
        /// A cache entry is "fresh" only if the JWT it contains is still comfortably valid.
        /// We decode the token's own "exp" claim rather than trusting the file's expiresUtc
        /// field, because Composer treats a ~30-day-old token as stale regardless of what we
        /// claim there — so a token within the refresh margin must be re-signed.
        /// </summary>
        private static bool IsJwtCacheFresh(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                var parsed = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(text) as Dictionary<string, object>;
                if (parsed == null || !(parsed["entries"] is Dictionary<string, object> entries))
                    return false;

                var threshold = DateTimeOffset.UtcNow.AddDays(Constants.JwtRefreshMarginDays);

                // Every entry must be fresh; if any is missing/stale we re-sign the whole cache.
                var sawEntry = false;
                foreach (var entry in entries.Values.OfType<Dictionary<string, object>>())
                {
                    sawEntry = true;
                    if (!entry.TryGetValue("token", out var token))
                        return false;
                    var exp = GetJwtExpiry(token?.ToString());
                    if (exp == null || exp.Value <= threshold)
                        return false;
                }

                return sawEntry;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Decodes a JWT's payload and returns its "exp" claim as a UTC timestamp, or null
        /// if the token is malformed or carries no expiration.
        /// </summary>
        private static DateTimeOffset? GetJwtExpiry(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                if (!(new JavaScriptSerializer()
                        .DeserializeObject(payloadJson) is Dictionary<string, object> payload) ||
                    !payload.TryGetValue("exp", out var exp)) return null;
                return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(exp));
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            return Convert.FromBase64String(s);
        }

        private static string ReadDealerUsername(string configFolder)
        {
            var path = $"{configFolder}/{Constants.DealerAccountFileName}";
            if (!File.Exists(path)) return null;
            try
            {
                return XDocument.Load(path).Root?.Element("Username")?.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Builds and signs an RS256 JWT matching the shape the controller's mosquitto-jwt-auth
        /// plugin (OS 4.2+) expects. Uses jailbreak_api.key via OpenSSL for the signature.
        /// </summary>
        private string SignMqttJwt(LogWindow log, string commonName,
            DateTimeOffset issuedAt, DateTimeOffset expiresAt)
        {
            var keyPath = $"{Constants.CertsFolder}/jailbreak_api.key";
            if (!File.Exists(keyPath))
            {
                log.WriteError($"{keyPath} missing.\n");
                return null;
            }

            var now = issuedAt.ToUnixTimeSeconds();
            var exp = expiresAt.ToUnixTimeSeconds();

            var header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
            var payload =
                "{\"CommonName\":\"" + JsonEscape(commonName) + "\"," +
                "\"Services\":\"director,sysman\"," +
                "\"UserName\":\"CN=" + Constants.CertificateCn + ",L=Draper,ST=Utah,C=US\"," +
                "\"Permissions\":\"/sysman,/director\"," +
                "\"iat\":" + now + "," +
                "\"exp\":" + exp + "}";

            var headerB64 = Base64Url(Encoding.UTF8.GetBytes(header));
            var payloadB64 = Base64Url(Encoding.UTF8.GetBytes(payload));
            var signingInput = headerB64 + "." + payloadB64;

            var tempInput = Path.GetTempFileName();
            var tempSig = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempInput, Encoding.UTF8.GetBytes(signingInput));

                var exitCode = RunProcessPrintOutput(log, OpenSslExe,
                    $"dgst -sha256 -sign \"{keyPath}\" -binary -out \"{tempSig}\" \"{tempInput}\"");
                if (exitCode != 0) return null;

                var sigBytes = File.ReadAllBytes(tempSig);
                return signingInput + "." + Base64Url(sigBytes);
            }
            finally
            {
                try
                {
                    File.Delete(tempInput);
                }
                catch
                {
                    /* ignore */
                }

                try
                {
                    File.Delete(tempSig);
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string JsonEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // -------------------------------------------------------------------
        // SSH helpers and network operations
        // -------------------------------------------------------------------

        private static string DownloadFile(ScpClient scp, string remoteFilename)
        {
            using (var stream = new MemoryStream())
            {
                scp.Download(remoteFilename, stream);
                stream.Position = 0;
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static void UploadFile(ScpClient scp, SshClient ssh, string remoteFilename, string fileContents)
        {
            var remoteDirectory = Path.GetDirectoryName(remoteFilename);

            var mkdirResult = ssh.RunCommand($"mkdir -p {remoteDirectory}");
            if (mkdirResult.ExitStatus != 0)
            {
                throw new Exception($"Failed to create directory {remoteDirectory}: exit {mkdirResult.ExitStatus}");
            }

            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(fileContents);
                    writer.Flush();
                    stream.Position = 0;
                    scp.Upload(stream, remoteFilename);
                }
            }
        }

        private void OnIpAddressChanged(object sender, EventArgs e)
        {
            SaveInputSettings();
            if (_loading) return;
            MacAddress.Text = "";
            _mainWindow.SetStatusRight("Not connected");
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async Task CheckConnection()
        {
            var ipAddress = IpAddress.Text;
            if (string.IsNullOrEmpty(ipAddress) || !IPAddress.TryParse(ipAddress, out _))
            {
                _mainWindow.SetStatusRight("Not connected");
                _directorVersion = null;
                _lastCheckedIp = null;
                return;
            }

            // If IP changed, clear the cached version
            if (ipAddress != _lastCheckedIp)
            {
                _directorVersion = null;
                _lastCheckedIp = ipAddress;
            }

            var result = await Task.Run(() =>
            {
                try
                {
                    using (var client = new WebClient())
                    {
                        // Ignore TLS cert errors (self-signed controller cert)
                        ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
                        var json = client.DownloadString($"https://{ipAddress}/api/v1/platform_status");
                        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    }
                }
                catch
                {
                    // OS 3.3 and earlier: no HTTPS on 443, try the node API directly on port 3000
                    try
                    {
                        using (var client = new WebClient())
                        {
                            var json = client.DownloadString($"http://{ipAddress}:3000/api/v1/platform_status");
                            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                        }
                    }
                    catch
                    {
                        return null;
                    }
                }
            });

            if (IpAddress.Text != ipAddress) return;

            if (result != null)
            {
                // Extract Director version from versions array
                // JavaScriptSerializer returns arrays as ArrayList
                if (result.ContainsKey("versions") && result["versions"] is System.Collections.ArrayList versions)
                {
                    foreach (var item in versions)
                    {
                        if (item is Dictionary<string, object> v &&
                            v.ContainsKey("name") && v["name"]?.ToString() == "Director" &&
                            v.TryGetValue("version", out var directorVersion))
                        {
                            _directorVersion = directorVersion?.ToString();
                            break;
                        }
                    }
                }

                // Autopopulate MAC address from API
                if (result.TryGetValue("directorMAC", out var directorMac))
                {
                    var mac = directorMac?.ToString();
                    if (!string.IsNullOrEmpty(mac) && MacAddress.Text != mac)
                    {
                        MacAddress.Text = mac;
                    }
                }

                var name = result.TryGetValue("directorName", out var directorName)
                    ? directorName?.ToString()
                    : ipAddress;
                var status = _directorVersion != null
                    ? $"{name} ({_directorVersion})"
                    : name;

                if (_directorVersion != null)
                {
                    var installed = GetInstalledManagementPackVersions();
                    status += IsPackInstalled(_directorVersion, installed)
                        ? " | Management pack installed"
                        : " | Management pack not installed";
                }

                _mainWindow.SetStatusRight(status);
            }
            else
            {
                _mainWindow.SetStatusRight($"Cannot reach {ipAddress}");
            }
        }

        private void OnMacAddressChanged(object sender, EventArgs e)
        {
            SaveInputSettings();
            if (_loading) return;
            WorkoutPassword();
        }

        private void OnUsernameChanged(object sender, EventArgs e)
        {
            SaveInputSettings();
        }

        private void OnPasswordChanged(object sender, EventArgs e)
        {
            SaveInputSettings();
        }

        private void SaveInputSettings()
        {
            if (_loading) return;
            var settings = Properties.Settings.Default;
            settings.LastIpAddress = IpAddress.Text;
            settings.LastUsername = Username.Text;
            settings.LastPassword = Password.Text;
            settings.LastMacAddress = MacAddress.Text;
            settings.Save();
        }

        private void WorkoutPassword()
        {
            var macAddress = MacAddress.Text;
            var password = GetDirectorRootPassword(macAddress);

            void UpdatePassword()
            {
                if (MacAddress.Text == macAddress)
                {
                    Password.Text = password;
                }
            }

            if (InvokeRequired)
            {
                Invoke((Action)UpdatePassword);
            }
            else
            {
                UpdatePassword();
            }
        }

        private static string GetDirectorRootPassword(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress) || macAddress.Length != 12)
            {
                return null;
            }

            var salt = Convert.FromBase64String("STlqJGd1fTkjI25CWz1hK1YuMURseXA/UnU5QGp6cF4=");
            return Convert.ToBase64String(
                new Rfc2898DeriveBytes(macAddress, salt, macAddress.Length * 397, HashAlgorithmName.SHA384)
                    .GetBytes(33));
        }

        // -------------------------------------------------------------------
        // SSH restore exploit + JWT auth
        // -------------------------------------------------------------------

        private static System.Net.Http.HttpClient CreateHttpClient()
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (s, cert, chain, sslPolicyErrors) => true
            };
            return new System.Net.Http.HttpClient(handler);
        }

        private List<string> GetWritableDriverIds(JailbreakInputs inputs)
        {
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken(inputs)}");

                var response = client.GetAsync($"https://{inputs.IpAddress}:443/api/v1/items").Result;
                response.EnsureSuccessStatusCode();

                var content = response.Content.ReadAsStringAsync().Result;

                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue
                };

                var luaDriverIds = new List<string>();
                var allDriverIds = new List<string>();
                if (serializer.DeserializeObject(content) is object[] items)
                {
                    foreach (var item in items)
                    {
                        if (item is Dictionary<string, object> itemDict &&
                            itemDict.TryGetValue("id", out var idObj))
                        {
                            var id = idObj.ToString();
                            allDriverIds.Add(id);
                            if (itemDict.TryGetValue("control", out var controlObj) &&
                                controlObj?.ToString() == "lua_gen")
                            {
                                luaDriverIds.Add(id);
                            }
                        }
                    }
                }

                var candidates = luaDriverIds.Count > 0 ? luaDriverIds : allDriverIds;
                if (candidates.Count == 0)
                {
                    throw new Exception("No drivers found in project to restore SSH through!");
                }

                return candidates;
            }
        }

        private void ApplySshRestoreExploit(JailbreakInputs inputs)
        {
            const string luaExploit = @"-- Only modify sshd_config to enable password authentication
local ssh_path = '/etc/ssh/sshd_config'

-- Read & patch sshd_config to enable password authentication
local ssh_lines = {}
for line in io.lines(ssh_path) do
  if line:match('^%s*PasswordAuthentication%s+no') then
    ssh_lines[#ssh_lines+1] = 'PasswordAuthentication yes'
  else
    ssh_lines[#ssh_lines+1] = line
  end
end

-- Write back sshd_config
local f = assert(io.open(ssh_path, 'w'))
for _, l in ipairs(ssh_lines) do
  f:write(l, '\n')
end
f:close()
";

            var driverIds = GetWritableDriverIds(inputs);
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken(inputs)}");

                var serializer = new JavaScriptSerializer();
                var commandData = new
                {
                    command = "LUA_COMMANDS",
                    async = false,
                    tParams = new
                    {
                        COMMANDS = luaExploit
                    }
                };
                var json = serializer.Serialize(commandData);

                var accepted = 0;
                Exception lastError = null;
                foreach (var driverId in driverIds)
                {
                    try
                    {
                        var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                        var response = client
                            .PostAsync($"https://{inputs.IpAddress}:443/api/v1/items/{driverId}/commands", content).Result;
                        response.EnsureSuccessStatusCode();
                        accepted++;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                if (accepted == 0)
                {
                    throw new Exception(
                        "SSH restore failed: no driver accepted the LUA command." +
                        (lastError != null ? " " + lastError.Message : ""));
                }
            }
        }

        private void ReloadSshService(JailbreakInputs inputs)
        {
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken(inputs)}");

                var response = client
                    .GetAsync($"https://{inputs.IpAddress}:443/api/v1/sysman/ssh?command=pkill%20-HUP%20sshd").Result;
                response.EnsureSuccessStatusCode();
            }
        }

        private string GetJwtToken(JailbreakInputs inputs)
        {
            if (!string.IsNullOrEmpty(_cachedJwtToken))
            {
                return _cachedJwtToken;
            }

            string customerEmail = null;
            string customerPassword = null;

            void ShowLoginDialog()
            {
                using (var loginDialog = new LoginDialog())
                {
                    if (loginDialog.ShowDialog(FindForm()) == DialogResult.OK)
                    {
                        customerEmail = loginDialog.Username;
                        customerPassword = loginDialog.Password;
                    }
                }
            }

            if (InvokeRequired)
            {
                Invoke((Action)ShowLoginDialog);
            }
            else
            {
                ShowLoginDialog();
            }

            if (string.IsNullOrEmpty(customerEmail) || string.IsNullOrEmpty(customerPassword))
            {
                throw new Exception("No customer credentials provided!");
            }

            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");

                var serializer = new JavaScriptSerializer();
                var requestData = new
                {
                    applicationkey = "78f6791373d61bea49fdb9fb8897f1f3af193f11",
                    env = "Prod",
                    email = customerEmail,
                    pwd = customerPassword,
                    dev = false
                };
                var json = serializer.Serialize(requestData);
                var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

                var response = client.PostAsync($"https://{inputs.IpAddress}:443/api/v1/jwt", content).Result;
                var responseContent = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"JWT auth failed (HTTP {(int)response.StatusCode}): {responseContent}");
                }

                if (serializer.DeserializeObject(responseContent) is Dictionary<string, object> responseData &&
                    responseData.TryGetValue("token", out var value))
                {
                    _cachedJwtToken = value.ToString();
                    return _cachedJwtToken;
                }

                throw new Exception($"JWT response did not contain a token: {responseContent}");
            }
        }

        // -------------------------------------------------------------------
        // Management pack installation
        // -------------------------------------------------------------------

        private async void InstallManagementPack(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Install Management Pack");

            // services.control4.com is normally blocked by the jailbreak because it hosts the
            // licensing reauthorization that pops the app-closing "Register Composer" modal. It also
            // hosts the Updates2x SOAP service this installer needs, so lift the block for the
            // duration of the install and restore it in the finally below. Only restore it if it was
            // actually blocked at the start, so a non-jailbroken machine is not left with a new block.
            var servicesWasBlocked = File.Exists(Constants.WindowsHostsFile) &&
                File.ReadAllLines(Constants.WindowsHostsFile)
                    .Any(l => l.Trim() == Constants.BlockCloudServicesHostsEntry.Trim());

            try
            {
                if (servicesWasBlocked)
                {
                    log.WriteNormal("Temporarily unblocking services.control4.com for the download...\n");
                    RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
                    FlushDns(log);
                }
                if (_directorVersion == null)
                {
                    log.WriteError("Controller OS version not detected.\n");
                    log.WriteError("Enter the controller IP address and wait for it to connect, then try again.\n");
                    return;
                }

                var versionParts = _directorVersion.Split('.');
                var shortVersion = string.Join(".", versionParts.Take(3));

                log.WriteNormal($"Director OS version: {_directorVersion}\n");

                // Check if already installed
                log.WriteNormal("Checking installed management packs... ");
                var installedVersions = GetInstalledManagementPackVersions();
                if (IsPackInstalled(_directorVersion, installedVersions))
                {
                    log.WriteSuccess($"Management pack for {shortVersion} is already installed.\n");
                    return;
                }

                log.WriteSuccess("not yet installed\n");

                // Find a matching SOAP version. Prefer the main update service; fall
                // back to the external (beta) service only if the OS isn't found there,
                // since newer/beta OS builds are published only to the external endpoint.
                var serviceUrl = Constants.UpdatesServiceUrl;
                log.WriteNormal("Querying Control4 update service... ");
                var versions = await Task.Run(() => GetComposerVersions(serviceUrl));
                var matchedVersion = versions?.FirstOrDefault(v => v.StartsWith(shortVersion));

                if (matchedVersion == null)
                {
                    log.WriteSuccess("not found\n");
                    log.WriteNormal("Checking the beta (external) update service... ");
                    serviceUrl = Constants.UpdatesExternalUrl;
                    versions = await Task.Run(() => GetComposerVersions(serviceUrl));
                    matchedVersion = versions?.FirstOrDefault(v => v.StartsWith(shortVersion));
                }

                if (matchedVersion == null)
                {
                    log.WriteError($"No management pack found for OS {shortVersion}.\n");
                    return;
                }

                log.WriteSuccess("done\n");
                log.WriteNormal($"Matched version: {matchedVersion}\n");

                log.WriteHeader("PACKAGE INFO");
                log.WriteNormal("Querying packages... ");
                string pkgName = null, pkgUrl = null, pkgChecksum = null;
                long pkgSize = 0;
                var found = await Task.Run(() =>
                    GetDriversPackageInfo(serviceUrl, matchedVersion, out pkgName, out pkgUrl, out pkgSize, out pkgChecksum));
                if (!found)
                {
                    log.WriteError("No management pack package found for this version.\n");
                    return;
                }

                log.WriteSuccess("done\n");
                log.WriteNormal($"Package: {pkgName}\n");
                log.WriteNormal($"URL: {pkgUrl}\n");
                log.WriteNormal($"Size: {pkgSize / 1024 / 1024} MB\n");

                log.WriteHeader("DOWNLOAD");
                var tempFile = Path.Combine(Path.GetTempPath(), pkgName);
                log.WriteNormal($"Downloading to {tempFile}...\n");
                log.WriteNormal("This may take a few minutes for large files.\n");

                using (var client = new WebClient())
                {
                    client.Proxy = null; // Bypass system/dead proxy
                    var lastPercent = -1;
                    client.DownloadProgressChanged += (s, args) =>
                    {
                        if (args.ProgressPercentage == lastPercent) return;
                        lastPercent = args.ProgressPercentage;
                        BeginInvoke((Action)(() =>
                            log.WriteProgress(
                                $"Downloading... {args.ProgressPercentage}% ({args.BytesReceived / 1024 / 1024} MB / {args.TotalBytesToReceive / 1024 / 1024} MB)")));
                    };
                    log.FormClosing += (s, args) => { client.CancelAsync(); };
                    await client.DownloadFileTaskAsync(pkgUrl, tempFile);
                }

                log.WriteSuccess("\nDownload complete!\n");

                if (!string.IsNullOrEmpty(pkgChecksum))
                {
                    log.WriteNormal("Verifying MD5 checksum... ");
                    var md5 = ComputeMd5(tempFile);
                    if (string.Equals(md5, pkgChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        log.WriteSuccess("OK\n");
                    }
                    else
                    {
                        log.WriteError($"Checksum mismatch! Expected {pkgChecksum}, got {md5}\n");
                        log.WriteError("The download may be corrupted. Aborting.\n");
                        return;
                    }
                }

                log.WriteHeader("INSTALL");
                log.WriteNormal("Launching installer... ");
                Process.Start(tempFile);
                log.WriteSuccess("done\n");
                log.WriteNormal("Follow the prompts to complete installation.\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"Error: {ex.Message}\n");
            }
            finally
            {
                if (servicesWasBlocked)
                {
                    log.WriteNormal("Restoring services.control4.com block...\n");
                    AddLineToFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
                    FlushDns(log);
                }
            }
        }

        private void ToggleUpdatesBlock(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Control4 Updates");
            try
            {
                if (IsServicesBlocked())
                {
                    log.WriteNormal("Allowing Control4 updates (unblocking services.control4.com)...\n");
                    RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
                    FlushDns(log);
                    log.WriteSuccess("Updates allowed. Use the Update Manager in Composer, then click \"Block Updates\" before you restart Composer; the jailbreak needs the block to run.\n");
                }
                else
                {
                    log.WriteNormal("Blocking Control4 updates (restoring the block)...\n");
                    AddLineToFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
                    FlushDns(log);
                    log.WriteSuccess("Updates blocked. The jailbreak will run normally again.\n");
                }
                UpdateUpdatesButton();
            }
            catch (Exception ex)
            {
                log.WriteError($"Error: {ex.Message}\n");
            }
        }

        private static bool IsServicesBlocked()
        {
            return File.Exists(Constants.WindowsHostsFile) &&
                File.ReadAllLines(Constants.WindowsHostsFile)
                    .Any(l => l.Trim() == Constants.BlockCloudServicesHostsEntry.Trim());
        }

        private void UpdateUpdatesButton()
        {
            try
            {
                if (IsServicesBlocked())
                {
                    buttonAllowUpdates.Text = " Allow Updates";
                    buttonAllowUpdates.Image = Properties.Resources.updates_block;
                }
                else
                {
                    buttonAllowUpdates.Text = " Block Updates";
                    buttonAllowUpdates.Image = Properties.Resources.updates_allow;
                }
            }
            catch
            {
            }
        }

        private static XDocument CallSoapService(string action, string innerXml, string serviceUrl)
        {
            var soapBody = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                           "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                           "xmlns:upd=\"" + Constants.UpdatesSoapNamespace + "\">" +
                           "<soap:Body>" +
                           innerXml +
                           "</soap:Body></soap:Envelope>";

            using (var client = new WebClient())
            {
                client.Headers["Content-Type"] = "text/xml; charset=utf-8";
                client.Headers["SOAPAction"] = "\"" + Constants.UpdatesSoapNamespace + action + "\"";

                var response = client.UploadString(serviceUrl, soapBody);
                return XDocument.Parse(response);
            }
        }

        private static string[] GetComposerVersions(string serviceUrl)
        {
            var doc = CallSoapService("GetVersions",
                "<upd:GetVersions>" +
                "<upd:currentVersion>3.0.0</upd:currentVersion>" +
                "</upd:GetVersions>", serviceUrl);

            var ns = XNamespace.Get(Constants.UpdatesSoapNamespace);

            return doc.Descendants(ns + "string")
                .Select(x => x.Value)
                .Where(v => v.EndsWith("+Composer"))
                .OrderByDescending(v => v)
                .ToArray();
        }

        private static bool GetDriversPackageInfo(string serviceUrl, string version,
            out string name, out string url, out long size, out string checksum)
        {
            name = url = checksum = null;
            size = 0;

            var escapedVersion = System.Security.SecurityElement.Escape(version);
            var doc = CallSoapService("GetPackagesByVersion",
                "<upd:GetPackagesByVersion>" +
                "<upd:version>" + escapedVersion + "</upd:version>" +
                "</upd:GetPackagesByVersion>", serviceUrl);

            var ns = XNamespace.Get(Constants.UpdatesSoapNamespace);

            foreach (var pkg in doc.Descendants(ns + "Package"))
            {
                var pkgName = pkg.Element(ns + "Name")?.Value ?? "";
                if (pkgName.StartsWith("Drivers-", StringComparison.OrdinalIgnoreCase))
                {
                    name = pkgName;
                    url = (pkg.Element(ns + "Url")?.Value ?? "").Replace("http://", "https://");
                    long.TryParse(pkg.Element(ns + "Size")?.Value ?? "0", out size);
                    checksum = pkg.Element(ns + "Checksum")?.Value ?? "";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scans the Windows registry for installed Control4 management packs.
        /// Returns a set of version strings that are installed (e.g. "3.4.3.741643").
        /// </summary>
        private static HashSet<string> GetInstalledManagementPackVersions()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            // Scan both 32-bit and 64-bit registry views to find installed packs
            // regardless of whether this process is 32-bit or 64-bit
            var views = new[] { RegistryView.Registry32, RegistryView.Registry64 };

            foreach (var view in views)
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(uninstallPath))
                    {
                        if (key == null) continue;
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var subKey = key.OpenSubKey(subKeyName))
                                {
                                    var displayName = subKey?.GetValue("DisplayName")?.ToString() ?? "";
                                    // Match "Drivers-3.4.3.741643-res" → "3.4.3.741643"
                                    if (displayName.StartsWith("Drivers-", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ver = displayName.Substring(8);
                                        var dashIdx = ver.IndexOf('-');
                                        if (dashIdx > 0)
                                            ver = ver.Substring(0, dashIdx);
                                        installed.Add(ver);
                                    }
                                    // Match "Composer OS Management Package 4.1.0" → "4.1.0"
                                    else if (displayName.StartsWith("Composer OS Management Package ",
                                                 StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ver = displayName.Substring("Composer OS Management Package ".Length)
                                            .Trim();
                                        if (!string.IsNullOrEmpty(ver))
                                            installed.Add(ver);
                                    }
                                }
                            }
                            catch
                            {
                                // Skip unreadable subkeys
                            }
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible registry views
                }
            }

            return installed;
        }

        /// <summary>
        /// Checks if a SOAP version has a matching installed management pack.
        /// Handles both full versions ("4.1.0.744089") and short versions ("4.1.0") from registry.
        /// </summary>
        private static bool IsPackInstalled(string soapVersion, HashSet<string> installedVersions)
        {
            if (installedVersions.Contains(soapVersion)) return true;
            foreach (var iv in installedVersions)
            {
                if (soapVersion.StartsWith(iv + ".") || soapVersion == iv)
                    return true;
            }

            return false;
        }

        private static string ComputeMd5(string filename)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filename))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // -------------------------------------------------------------------
        // UI button handlers
        // -------------------------------------------------------------------

        private void SearchGoogleForComposer(object sender, EventArgs e)
        {
            Process.Start("https://www.google.com/search?q=ComposerPro-3.1.3.574885-res.exe");
        }

        private void OpenControl4Reddit(object sender, EventArgs e)
        {
            Process.Start("https://www.reddit.com/r/C4diy/");
        }

        // -------------------------------------------------------------------
        // File utility helpers
        // -------------------------------------------------------------------

        private static void CopyFile(LogWindow log, string a, string b)
        {
            log.WriteNormal($"Copying {Path.GetFileName(a)}... ");
            File.Copy(a, b, true);
            log.WriteSuccess("done\n");
        }

        private static void WriteFile(LogWindow log, string file, string content)
        {
            log.WriteNormal($"Writing {Path.GetFileName(file)}... ");
            File.WriteAllText(file, content);
            log.WriteSuccess("done\n");
        }

        // -------------------------------------------------------------------
        // Unjailbreak — revert what the jailbreak changed (hidden: Shift+Click)
        // -------------------------------------------------------------------

        private void UnjailbreakWorker(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            try
            {
                if (Process.GetProcessesByName(Constants.ComposerProcessName).Length > 0)
                {
                    log.WriteError("ComposerPro.exe is currently running. Please close Composer and try again.\n");
                    return;
                }

                if (!FindComposerInstallDir(log))
                    return;

                // 1. Local (Windows) reversal
                log.WriteHeader("REMOVE LOCAL CHANGES");
                var configFolder = GetComposerConfigFolder();
                RemoveComposerFiles(log, configFolder);

                RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockCloudServicesHostsEntry);
                RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockCloudLocatorHostsEntry);
                RemoveLineFromFile(log, Constants.WindowsHostsFile, Constants.BlockSplitIoHostsEntry);

                // Strip the dead proxy that PatchConfigFile injected (and any left by older versions).
                RemoveDeadProxyFromConfig(log);

                // 2. Controller (director) reversal — best-effort, requires SSH. The reboot
                // (if cert chains changed) runs inside UnpatchDirector over the still-open
                // session, because the reversal removes our authorized_keys entry and a fresh
                // SSH connection would then fail with "publickey".
                log.WriteHeader("REVERT DIRECTOR");
                try
                {
                    UnpatchDirector(inputs, log, warnings);
                }
                catch (Exception ex)
                {
                    log.WriteError("Director reversal failed:\n");
                    log.WriteError(ex);
                    log.WriteNormal(
                        "\nLocal changes were reverted. Re-run (Shift+Click) when the controller is reachable to finish.\n");
                    return;
                }

                log.WriteHeader("DONE");
                log.WriteSuccess("Unjailbreak complete.\n");
                foreach (var warning in warnings)
                    log.WriteWarning(warning + "\n");
            }
            catch (Exception ex)
            {
                log.WriteError("Unjailbreak failed:\n");
                log.WriteError(ex);
            }
        }

        private static void RemoveComposerFiles(LogWindow log, string configFolder)
        {
            DeleteIfExists(log, $"{configFolder}/Composer/{Constants.ComposerCertName}");
            DeleteIfExists(log, $"{configFolder}/Composer/{Constants.ComposerP12FileName}");
            DeleteIfExists(log, $"{configFolder}/Composer/{Constants.FeaturesConfigFileName}");
            DeleteIfExists(log, $"{configFolder}/{Constants.DealerAccountFileName}");
            DeleteIfExists(log, $"{configFolder}/{Constants.LicenseFileName}");
            // ComposerUpdateManagerSettings.Config is Composer's own settings file; only our
            // UpdateURLList30 entry was added and it's harmless without the jailbreak, so leave it.
        }

        private static void DeleteIfExists(LogWindow log, string path)
        {
            log.WriteNormal($"Removing {Path.GetFileName(path)}... ");
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    log.WriteSuccess("done\n");
                }
                else
                {
                    log.WriteTrace("not present\n");
                }
            }
            catch (Exception ex)
            {
                log.WriteError($"failed: {ex.Message}\n");
            }
        }

        private bool UnpatchDirector(JailbreakInputs inputs, LogWindow log, List<string> warnings)
        {
            var anyModified = false;
            ScpClient scp = null;
            SshClient ssh = null;

            try
            {
                var connectionInfo = SshConnection(inputs);
                scp = new ScpClient(connectionInfo);
                ssh = new SshClient(connectionInfo);

                log.WriteNormal("Connecting to director via SSH/SCP... ");
                ConnectWithRetries(scp, log, "SCP");
                ConnectWithRetries(ssh, log, "SSH");
                log.WriteSuccess("connected\n");

                var localCertPath = $"{Constants.CertsFolder}/{Constants.CaPublicPemFileName}";
                var localCert = File.Exists(localCertPath)
                    ? File.ReadAllText(localCertPath).Trim()
                    : null;
                if (localCert == null)
                    log.WriteWarning(
                        $"{localCertPath} missing — removing jailbreak certs by CN detection only.\n");

                anyModified |= UnpatchRemoteCertChain(log, scp, ssh, Constants.OpenVpnClientCaProdPem, localCert);
                anyModified |=
                    UnpatchRemoteCertChain(log, scp, ssh, Constants.ControllerClientCaProdPem, localCert);
                anyModified |= UnpatchRemoteCertChain(log, scp, ssh, Constants.MosquittoCaChainPem, localCert);

                UnpatchControllerApiPem(log, scp, ssh);

                // Remove our authorized_keys entry LAST — we rely on it to stay connected.
                UnpatchAuthorizedKeys(log, scp, ssh, $"{Constants.KeysFolder}/{inputs.MacAddress}");

                if (anyModified)
                {
                    // Reboot over THIS already-open session. We just removed our authorized_keys
                    // entry, so a new SSH connection would fail with "publickey"; this session is
                    // already authenticated and stays valid until the box actually reboots.
                    DialogResult rebootChoice = DialogResult.None;

                    void AskReboot()
                    {
                        rebootChoice = MessageBox.Show(
                            FindForm(),
                            "The director needs to reboot for the certificate changes to take effect.\n\n" +
                            "This will temporarily take your Control4 system offline.\n\nReboot now?",
                            "Director Reboot Required",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                    }

                    if (InvokeRequired)
                        Invoke((Action)AskReboot);
                    else
                        AskReboot();

                    if (rebootChoice == DialogResult.Yes)
                    {
                        log.WriteNormal("Rebooting director... ");
                        ssh.RunCommand("nohup sh -c '( sleep 2 ; reboot )' >/dev/null 2>&1 &");
                        log.WriteSuccess("done\n");
                        _directorVersion = null;
                        warnings.Add(
                            "Your system is rebooting — it can take a while. Don't panic, give it 10 minutes!\n");
                    }
                    else
                    {
                        log.WriteNormal("Skipping reboot for now.\n");
                        warnings.Add(
                            "Reboot skipped — certificate changes take effect after the director reboots.\n");
                    }
                }

                return anyModified;
            }
            finally
            {
                scp?.Dispose();
                ssh?.Dispose();
            }
        }

        private static string NormalizePem(string pem) =>
            new string((pem ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

        // Download a cert chain, drop the jailbreak CA (and any orphan jailbreak certs), re-upload.
        private static bool UnpatchRemoteCertChain(LogWindow log, ScpClient scp, SshClient ssh, string remoteFile,
            string localCert)
        {
            log.WriteNormal($"Cleaning {remoteFile}:\n");

            string remoteCertChain;
            try
            {
                log.WriteNormal($"  Downloading {remoteFile}... ");
                remoteCertChain = DownloadFile(scp, remoteFile).Trim();
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist - skipping\n");
                return false;
            }

            var localNorm = localCert == null ? null : NormalizePem(localCert);
            var blocks = ExtractPemBlocks(remoteCertChain).ToList();
            var kept = blocks
                .Where(pem => !IsOrphanJailbreakCert(pem)
                              && (localNorm == null || NormalizePem(pem) != localNorm))
                .ToList();

            if (kept.Count == blocks.Count)
            {
                log.WriteTrace("  (nothing to remove)\n");
                return false;
            }

            var rebuilt = string.Join("\n", kept).Trim() + "\n";
            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, ssh, remoteFile, rebuilt);
            log.WriteSuccess("done\n");
            return true;
        }

        private static void UnpatchControllerApiPem(LogWindow log, ScpClient scp, SshClient ssh)
        {
            const string remoteFile = Constants.ControllerApiPem;
            log.WriteNormal($"Cleaning {remoteFile}:\n");

            string remotePem;
            try
            {
                log.WriteNormal($"  Downloading {remoteFile}... ");
                remotePem = DownloadFile(scp, remoteFile);
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist - skipping\n");
                return;
            }

            var blocks = ExtractPemBlocks(remotePem).ToList();
            var kept = blocks.Where(pem => !IsOrphanJailbreakCert(pem)).ToList();
            if (kept.Count == blocks.Count)
            {
                log.WriteTrace("  (nothing to remove)\n");
                return;
            }

            var rebuilt = string.Join("\n", kept).Trim() + "\n";
            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, ssh, remoteFile, rebuilt);
            log.WriteSuccess("done\n");

            log.WriteNormal("  Restarting mosquitto-jwt-auth... ");
            var restartResult = ssh.RunCommand($"pidof {Constants.MosquittoJwtAuthProcess} | xargs -r kill -9");
            if (restartResult.ExitStatus != 0)
                log.WriteError($"failed (exit {restartResult.ExitStatus})\n");
            else
                log.WriteSuccess("done\n");
        }

        private static void UnpatchAuthorizedKeys(LogWindow log, ScpClient scp, SshClient ssh, string localKeysFolder)
        {
            var pubKeys = new List<string>
                {
                    $"{localKeysFolder}/{Constants.SshHostRsaPubKey}",
                    $"{localKeysFolder}/{Constants.SshHostEd25519PubKey}"
                }
                .Where(File.Exists)
                .Select(f => File.ReadAllText(f).Trim())
                .ToList();
            if (pubKeys.Count == 0)
            {
                log.WriteTrace("No local SSH pub keys on record — skipping authorized_keys cleanup.\n");
                return;
            }

            const string remoteAuthorizedKeysFile = Constants.ControllerAuthorizedKeys;
            log.WriteNormal($"Cleaning {remoteAuthorizedKeysFile}:\n");

            string authorizedKeys;
            try
            {
                log.WriteNormal($"  Downloading {remoteAuthorizedKeysFile}... ");
                authorizedKeys = DownloadFile(scp, remoteAuthorizedKeysFile);
                log.WriteSuccess("done\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist - skipping\n");
                return;
            }

            var originalLines = authorizedKeys.Replace("\r\n", "\n").Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            var keptLines = originalLines
                .Where(line => !pubKeys.Any(k => line.Trim() == k || line.Contains(k)))
                .ToList();

            if (keptLines.Count == originalLines.Count)
            {
                log.WriteTrace("  (nothing to remove)\n");
                return;
            }

            var rebuilt = string.Join("\n", keptLines).Trim();
            if (rebuilt.Length > 0)
                rebuilt += "\n";

            log.WriteNormal("  Updating authorized_keys... ");
            UploadFile(scp, ssh, remoteAuthorizedKeysFile, rebuilt);
            log.WriteSuccess("done\n");
        }

        private static void RemoveLineFromFile(LogWindow log, string file, string line, bool ignoreWhitespace = true)
        {
            if (!File.Exists(file))
            {
                log.WriteTrace($"Removing line from {Path.GetFileName(file)}... file not found, skipping\n");
                return;
            }

            log.WriteNormal($"Removing line from {Path.GetFileName(file)}... ");
            var lines = File.ReadAllLines(file);
            var newLines = lines.Where(s => ignoreWhitespace
                ? s.Trim() != line.Trim()
                : s != line).ToArray();

            if (newLines.Length != lines.Length)
            {
                File.WriteAllLines(file, newLines);
                log.WriteSuccess("done\n");
            }
            else
            {
                log.WriteTrace("already removed\n");
            }
        }

        private static void AddLineToFile(LogWindow log, string file, string line, bool ignoreWhitespace = true)
        {
            log.WriteNormal($"Adding line to {Path.GetFileName(file)}... ");

            if (File.Exists(file))
            {
                var lines = File.ReadAllLines(file);
                if (!lines.Select(s => ignoreWhitespace ? s.Trim() : s).Contains(ignoreWhitespace ? line.Trim() : line))
                {
                    File.AppendAllText(file, line.TrimEnd() + Environment.NewLine);
                    log.WriteSuccess("done\n");
                }
                else
                {
                    log.WriteTrace("already present\n");
                }
            }
            else
            {
                File.WriteAllText(file, line.TrimEnd() + Environment.NewLine);
                log.WriteSuccess("done\n");
            }
        }

        private static void FlushDns(LogWindow log)
        {
            try
            {
                var psi = new ProcessStartInfo("ipconfig", "/flushdns")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                log.WriteTrace($"DNS flush skipped: {ex.Message}\n");
            }
        }

        private static int RunProcessPrintOutput(LogWindow log, string exe, string arguments)
        {
            log.WriteTrace($"{Path.GetFileName(exe)} {arguments}\n");

            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = Environment.CurrentDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                EnvironmentVariables = { ["OPENSSL_CONF"] = Path.GetFullPath(Constants.OpenSslConfig) }
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                log.WriteError($"Failed to start {exe} {arguments}\n");
                return -1;
            }

            log.WriteTrace(process.StandardOutput.ReadToEnd());
            log.WriteTrace(process.StandardError.ReadToEnd());

            process.WaitForExit();

            log.WriteTrace(process.StandardError.ReadToEnd());
            log.WriteTrace(process.StandardOutput.ReadToEnd());

            return process.ExitCode;
        }

        private static string RunProcessGetOutput(string exe, string arguments)
        {
            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                EnvironmentVariables = { ["OPENSSL_CONF"] = Path.GetFullPath(Constants.OpenSslConfig) }
            };

            var process = Process.Start(startInfo);

            return process?.StandardOutput.ReadToEnd();
        }
    }

    /// <summary>
    /// A Button with a right-hand dropdown-arrow zone, drawn as a single element. Clicking the
    /// arrow zone opens DropDownMenu; clicking anywhere else raises Click as usual.
    /// </summary>
    public class SplitButton : Button
    {
        public const int ArrowZoneWidth = 34;

        public ContextMenuStrip DropDownMenu { get; set; }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);

            var g = pevent.Graphics;
            var splitX = Width - ArrowZoneWidth;

            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(60, ForeColor)))
            {
                g.DrawLine(pen, splitX, 10, splitX, Height - 10);
            }

            var cx = splitX + (ArrowZoneWidth / 2);
            var cy = Height / 2;
            var arrow = new[]
            {
                new System.Drawing.Point(cx - 5, cy - 2),
                new System.Drawing.Point(cx + 5, cy - 2),
                new System.Drawing.Point(cx, cy + 4),
            };
            using (var brush = new System.Drawing.SolidBrush(
                Enabled ? ForeColor : System.Drawing.SystemColors.GrayText))
            {
                g.FillPolygon(brush, arrow);
            }
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left && DropDownMenu != null &&
                mevent.X >= Width - ArrowZoneWidth)
            {
                DropDownMenu.Show(this, new System.Drawing.Point(0, Height));
                return;
            }

            base.OnMouseDown(mevent);
        }
    }
}