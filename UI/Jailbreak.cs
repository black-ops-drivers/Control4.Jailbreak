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
        private string _lastCheckedIp;
        private readonly Timer _connectionTimer = new Timer { Interval = 10000 };
        private readonly Timer _debounceTimer = new Timer { Interval = 800 };

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
                                        if (!string.IsNullOrEmpty(installLocation) && ValidateInstallDir(installLocation))
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
            _debounceTimer.Tick += (s, ev) => { _debounceTimer.Stop(); _ = CheckConnection(); };
            Load += Jailbreak_Load;
        }

        private void Jailbreak_Load(object sender, EventArgs e)
        {
            _loading = true;

            checkBoxBlockSplitIo.Checked = Properties.Settings.Default.BlockSplitIoChecked;

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

        private void DoJailbreak(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Jailbreak");
            var warnings = new List<string>();

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

                if (Process.GetProcessesByName("ComposerPro").Length > 0)
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

                // 5. Patch ComposerPro.exe.config (idempotent)
                log.WriteHeader("PATCH CONFIG");
                PatchConfigFile(log);

                // 6-10. Deploy files + settings
                log.WriteHeader("DEPLOY FILES");

                var configFolder = GetComposerConfigFolder();
                DeployComposerFiles(log, configFolder);
                WriteFeatureFlags(log, configFolder);
                UpdateUpdateManagerSettings(log, configFolder);
                ConfigureSplitIoBlock(log, checkBoxBlockSplitIo.Checked);
                EnsureDealerAccount(log, configFolder);

                // 11. Patch Director (SSH)
                log.WriteHeader("PATCH DIRECTOR");

                bool directorModified;
                try
                {
                    directorModified = PatchDirector(log, warnings);
                }
                catch (Exception ex)
                {
                    log.WriteError("Director patching failed:\n");
                    log.WriteError(ex);
                    log.WriteNormal("\nLocal Composer patching succeeded. Run the jailbreak again to retry director patching.\n");
                    return;
                }

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
                        RebootDirector(log, warnings);
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

            if (File.Exists($"{Constants.CertsFolder}/public.pem") &&
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
                        log.WriteNormal($"Schema version changed ({storedVersion} -> {Constants.CertSchemaVersion}) — regenerating.\n");
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
        // Config file patching (dead proxy + bypasslist)
        // -------------------------------------------------------------------

        private void PatchConfigFile(LogWindow log)
        {
            var configPath = Path.Combine(ComposerInstallDir, "ComposerPro.exe.config");
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
            CopyFile(log, $"{Constants.CertsFolder}/composer.p12", $"{configFolder}/Composer/composer.p12");
        }

        private static void WriteFeatureFlags(LogWindow log, string configFolder)
        {
            WriteFile(log, $"{configFolder}/Composer/FeaturesConfiguration.json",
                @"{" +
                @"""composer-x4-updatemanger-restrict-override"":{""Result"":true,""Config"":null}," +
                @"""connection-whitelist"":{""Result"":false,""Config"":""[]""}," +
                @"""os-pack-on-connect"":{""Result"":true,""Config"":null}" +
                @"}");
        }

        private static void UpdateUpdateManagerSettings(LogWindow log, string configFolder)
        {
            log.WriteNormal("Setting Update Manager URL... ");
            var settingsPath = $"{configFolder}/Composer/ComposerUpdateManagerSettings.Config";
            try
            {
                var settingsDoc = File.Exists(settingsPath) ? XDocument.Load(settingsPath) : XDocument.Parse("<settings/>");

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

        private static void EnsureDealerAccount(LogWindow log, string configFolder)
        {
            log.WriteNormal("Checking dealer account... ");
            if (!File.Exists($"{configFolder}/dealeraccount.xml"))
            {
                log.WriteNormal("creating\n");
                WriteFile(log, $"{configFolder}/dealeraccount.xml", @"<?xml version=""1.0"" encoding=""utf-8""?>
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

        private ConnectionInfo SshConnection()
        {
            var authMethods = new List<AuthenticationMethod>
            {
                new PasswordAuthenticationMethod(Username.Text, Password.Text),
                new PasswordAuthenticationMethod(Username.Text, "t0talc0ntr0l4!")
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
                IpAddress.Text,
                Username.Text,
                authMethods.ToArray()
            )
            {
                RetryAttempts = 1,
                Timeout = TimeSpan.FromSeconds(5)
            };

            return sshConnectionInfo;
        }

        /// <summary>
        /// Patches the director's cert chains via SSH.
        /// Returns true if a reboot is needed (either from new cert changes or a previous pending reboot).
        /// </summary>
        private bool PatchDirector(LogWindow log, List<string> warnings)
        {
            if (!File.Exists($"{Constants.CertsFolder}/public.pem"))
            {
                log.WriteError($"Couldn't find {Constants.CertsFolder}/public.pem - have you generated certificates?\n");
                return false;
            }

            var macAddress = MacAddress.Text;
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

            try
            {
                scp = new ScpClient(SshConnection());

                log.WriteNormal("Connecting to director via SCP... ");
                try
                {
                    scp.Connect();
                }
                catch (Exception)
                {
                    log.WriteWarning("failed — attempting to restore SSH access\n");

                    log.WriteNormal("Restoring SSH password authentication... ");
                    ApplySshRestoreExploit(GetWritableDriverId());
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Reloading SSH service... ");
                    ReloadSshService();
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Waiting for SSH to reload... ");
                    System.Threading.Thread.Sleep(1000);
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Reconnecting via SCP... ");
                    scp.Dispose();
                    scp = new ScpClient(SshConnection());
                    scp.Connect();
                }
                log.WriteSuccess("connected\n");

                if (DownloadRootDeviceSshKeys(log, scp, localKeysFolder, warnings))
                {
                    PatchDirectorySshAuthorizedKeysFile(log, scp, localKeysFolder);
                }

                log.WriteNormal($"Reading {Constants.CertsFolder}/public.pem... ");
                var localCert = File.ReadAllText($"{Constants.CertsFolder}/public.pem").Trim();
                log.WriteSuccess("done\n");

                anyModified |= PatchRemoteCertChain(log, scp, "/etc/openvpn/clientca-prod.pem", localCert);
                anyModified |= PatchRemoteCertChain(log, scp, "/opt/control4/etc/ssl/certs/clientca-prod.pem", localCert);
                anyModified |= PatchRemoteCertChain(log, scp, "/etc/mosquitto/certs/ca-chain.pem", localCert);

                if (anyModified)
                {
                    // Write reboot marker — /tmp is cleared on reboot
                    log.WriteTrace("Writing reboot marker...\n");
                    UploadFile(scp, Constants.RebootMarkerPath, DateTime.UtcNow.ToString("o"));
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
            }

            return anyModified;
        }

        /// <summary>
        /// Returns true if the cert chain was actually modified, false if already patched or the file doesn't exist.
        /// </summary>
        private static bool PatchRemoteCertChain(LogWindow log, ScpClient scp, string remoteFile, string localCert)
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
            UploadFile(scp, $"{directory}/{backupFilename}", remoteCertChain);
            log.WriteSuccess("done\n");

            log.WriteNormal($"  Saving local backup to {Constants.CertsFolder}/{backupFilename}... ");
            File.WriteAllText($"{Constants.CertsFolder}/{backupFilename}", remoteCertChain);
            log.WriteSuccess("done\n");

            remoteCertChain = DedupeX509CertChain(dedupedRemoteCertChain + "\n" + localCert);

            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, remoteFile, remoteCertChain);
            log.WriteSuccess("done\n");

            return true;
        }

        private static string DedupeX509CertChain(string certChain)
        {
            return string.Join("\n", certChain
                    .Split(new[] { "-----END CERTIFICATE-----" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(cert => !string.IsNullOrWhiteSpace(cert))
                    .Select(cert => cert.Trim() + "\n-----END CERTIFICATE-----")
                    .GroupBy(cert => new X509Certificate2(Encoding.UTF8.GetBytes(cert)).Subject)
                    .Select(group => group.Last()))
                .Trim();
        }

        private static bool DownloadRootDeviceSshKeys(LogWindow log, ScpClient scp, string localKeysFolder, List<string> warnings)
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
                var remoteFile = $"/etc/ssh/{file}";
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

        private static void PatchDirectorySshAuthorizedKeysFile(LogWindow log, ScpClient scp, string localKeysFolder)
        {
            var localPubKeyFiles = new List<string>
            {
                $"{localKeysFolder}/ssh_host_rsa_key.pub",
                $"{localKeysFolder}/ssh_host_ed25519_key.pub"
            }.Where(File.Exists).ToArray();
            if (localPubKeyFiles.Length == 0)
            {
                return;
            }

            var localAuthorizedKeysFile = $"{localKeysFolder}/authorized_keys";
            const string remoteAuthorizedKeysFile = "/home/root/.ssh/authorized_keys";

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
            UploadFile(scp, $"{remoteAuthorizedKeysFile}{backupSuffix}", authorizedKeys);
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
            UploadFile(scp, remoteAuthorizedKeysFile, authorizedKeys);
            log.WriteSuccess("done\n");
        }

        private void RebootDirector(LogWindow log, List<string> warnings)
        {
            try
            {
                log.WriteNormal("Connecting to director... ");

                using (var ssh = new SshClient(SshConnection()))
                {
                    ssh.Connect();
                    log.WriteSuccess("connected\n");

                    log.WriteNormal("Running reboot command... ");
                    ssh.RunCommand("nohup sh -c '( sleep 2 ; reboot )' >/dev/null 2>&1 &");
                    ssh.Disconnect();
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

        private static void UploadFile(ScpClient scp, string remoteFilename, string fileContents)
        {
            var remoteDirectory = Path.GetDirectoryName(remoteFilename);

            using (var ssh = new SshClient(scp.ConnectionInfo))
            {
                ssh.Connect();
                ssh.RunCommand($"mkdir -p {remoteDirectory}");
                ssh.Disconnect();
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
                    return null;
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

                var name = result.TryGetValue("directorName", out var directorName) ? directorName?.ToString() : ipAddress;
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

        private string GetWritableDriverId()
        {
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken()}");

                var response = client.GetAsync($"https://{IpAddress.Text}:443/api/v1/items").Result;
                response.EnsureSuccessStatusCode();

                var content = response.Content.ReadAsStringAsync().Result;

                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue
                };
                if (serializer.DeserializeObject(content) is object[] items)
                {
                    foreach (var item in items)
                    {
                        if (item is Dictionary<string, object> itemDict &&
                            itemDict.TryGetValue("name", out var nameObj))
                        {
                            var name = nameObj.ToString();
                            if (name == "Data Analytics Agent" || name == "Stations")
                            {
                                if (itemDict.TryGetValue("id", out var idObj))
                                {
                                    return idObj.ToString();
                                }
                            }
                        }
                    }
                }

                throw new Exception("No writable driver found in project!");
            }
        }

        private void ApplySshRestoreExploit(string driverId)
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

            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken()}");

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
                var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

                var response = client
                    .PostAsync($"https://{IpAddress.Text}:443/api/v1/items/{driverId}/commands", content).Result;
                response.EnsureSuccessStatusCode();
            }
        }

        private void ReloadSshService()
        {
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken()}");

                var response = client
                    .GetAsync($"https://{IpAddress.Text}:443/api/v1/sysman/ssh?command=pkill%20-HUP%20sshd").Result;
                response.EnsureSuccessStatusCode();
            }
        }

        private string GetJwtToken()
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

                var response = client.PostAsync($"https://{IpAddress.Text}:443/api/v1/jwt", content).Result;
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

        private void InstallManagementPack(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Install Management Pack");
            try
            {
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

                // Find a matching SOAP version
                log.WriteNormal("Querying Control4 update service... ");
                var versions = GetComposerVersions();
                if (versions == null || versions.Length == 0)
                {
                    log.WriteError("No versions found from update service.\n");
                    return;
                }
                log.WriteSuccess("done\n");

                var matchedVersion = versions.FirstOrDefault(v => v.StartsWith(shortVersion));
                if (matchedVersion == null)
                {
                    log.WriteError($"No management pack found for OS {shortVersion}.\n");
                    return;
                }
                log.WriteNormal($"Matched version: {matchedVersion}\n");

                log.WriteHeader("PACKAGE INFO");
                log.WriteNormal("Querying packages... ");
                if (!GetDriversPackageInfo(matchedVersion, out var pkgName, out var pkgUrl, out var pkgSize, out var pkgChecksum))
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
                    client.DownloadFile(pkgUrl, tempFile);
                }

                log.WriteSuccess("Download complete!\n");

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
        }

        private static XDocument CallSoapService(string action, string innerXml)
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

                var response = client.UploadString(Constants.UpdatesServiceUrl, soapBody);
                return XDocument.Parse(response);
            }
        }

        private static string[] GetComposerVersions()
        {
            var doc = CallSoapService("GetVersions",
                "<upd:GetVersions>" +
                "<upd:currentVersion>3.0.0</upd:currentVersion>" +
                "</upd:GetVersions>");

            var ns = XNamespace.Get(Constants.UpdatesSoapNamespace);

            return doc.Descendants(ns + "string")
                .Select(x => x.Value)
                .Where(v => v.EndsWith("+Composer"))
                .OrderByDescending(v => v)
                .ToArray();
        }

        private static bool GetDriversPackageInfo(string version,
            out string name, out string url, out long size, out string checksum)
        {
            name = url = checksum = null;
            size = 0;

            var escapedVersion = System.Security.SecurityElement.Escape(version);
            var doc = CallSoapService("GetPackagesByVersion",
                "<upd:GetPackagesByVersion>" +
                "<upd:version>" + escapedVersion + "</upd:version>" +
                "</upd:GetPackagesByVersion>");

            var ns = XNamespace.Get(Constants.UpdatesSoapNamespace);

            foreach (var pkg in doc.Descendants(ns + "Package"))
            {
                var pkgName = pkg.Element(ns + "Name")?.Value ?? "";
                if (pkgName.StartsWith("Drivers-", StringComparison.OrdinalIgnoreCase))
                {
                    name = pkgName;
                    url = pkg.Element(ns + "Url")?.Value ?? "";
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
                                    else if (displayName.StartsWith("Composer OS Management Package ", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ver = displayName.Substring("Composer OS Management Package ".Length).Trim();
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
}
