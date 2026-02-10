using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class Composer : UserControl
    {
        private readonly MainWindow _mainWindow;

        public Composer(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            InitializeComponent();
            checkBoxBlockSplitIo.CheckedChanged += checkBoxBlockSplitIo_CheckedChanged;
            Load += Composer_Load;

        }

        private void Composer_Load(object sender, EventArgs e)
        {
            // Load the checkbox state from the application settings
            checkBoxBlockSplitIo.Checked = Properties.Settings.Default.BlockSplitIoChecked;
        }

        private void checkBoxBlockSplitIo_CheckedChanged(object sender, EventArgs e)
        {
            // Save the checkbox state to the application settings
            Properties.Settings.Default.BlockSplitIoChecked = checkBoxBlockSplitIo.Checked;
            Properties.Settings.Default.Save();
        }


        private void PatchComposer(object sender, EventArgs eventargs)
        {
            var log = new LogWindow(_mainWindow);

            log.WriteTrace("Asking for ComposerPro.exe.config location\n");

            var open = new OpenFileDialog();
            open.Filter = @"Config Files|*.config";
            open.Title = @"Find Original ComposerPro.exe.config";
            open.InitialDirectory = "C:\\Program Files (x86)\\Control4\\Composer\\Pro";
            open.FileName = "ComposerPro.exe.config";

            if (open.ShowDialog() != DialogResult.OK)
            {
                log.WriteError("Cancelled\n");
                return;
            }

            if (string.IsNullOrEmpty(open.FileName))
            {
                log.WriteError("Filename was invalid\n");
                return;
            }

            log.WriteNormal("Opening ");
            log.WriteHighlight($"{open.FileName}\n");

            try
            {
                // Load the XML document
                var xmlDoc = XDocument.Load(open.FileName);

                // Find the <system.net> element
                var systemNet = xmlDoc.Root?.Element("system.net");
                if (systemNet == null)
                {
                    log.WriteError("Could not find the <system.net> node in the configuration file.\n");
                    return;
                }

                // Add a dead proxy to block most outbound HTTP from Composer.
                // This prevents license validation and dealer auth checks.
                // The bypasslist allows services.control4.com through so the
                // Update Manager can still fetch available versions via SOAP.
                var defaultProxy = systemNet.Element("defaultProxy");
                if (defaultProxy != null)
                {
                    log.WriteNormal("Removing existing <defaultProxy> to replace it...\n");
                    defaultProxy.Remove();
                }

                log.WriteNormal("Adding <defaultProxy> node...\n");
                defaultProxy = new XElement("defaultProxy",
                    new XElement("proxy",
                        new XAttribute("usesystemdefault", "false"),
                        new XAttribute("proxyaddress", "http://127.0.0.1:31337/"),
                        new XAttribute("bypassonlocal", "true")
                    ),
                    new XElement("bypasslist",
                        // Allow the Updates SOAP service through for version checks
                        new XElement("add",
                            new XAttribute("address", @"services\.control4\.com")),
                        // Allow the update download server through
                        new XElement("add",
                            new XAttribute("address", @"update2\.control4\.com")),
                        // Allow the apt update server through
                        new XElement("add",
                            new XAttribute("address", @"c4updates\.control4\.com"))
                    )
                );

                systemNet.Add(defaultProxy);

                log.WriteHighlight("Added <defaultProxy> node.\n");

                // Backup and save the modified XML
                var backupPath = open.FileName + $".backup-{DateTime.Now:yyyy-dd-M--HH-mm-ss}";
                log.WriteHighlight("Writing Backup..\n");
                File.Copy(open.FileName, backupPath);

                log.WriteHighlight("Writing New File..\n");
                xmlDoc.Save(open.FileName);

                log.WriteHighlight("Done!\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"An error occurred: {ex.Message}\n");
            }
        }

        private void InstallManagementPack(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Install Management Pack");
            try
            {
                log.WriteNormal("Querying Control4 update service for available versions...\n");

                var versions = GetComposerVersions();
                if (versions == null || versions.Length == 0)
                {
                    log.WriteError("No versions found from update service.\n");
                    return;
                }

                log.WriteHighlight($"Found {versions.Length} versions with driver packs.\n\n");

                // Show a version picker dialog
                string selectedVersion;
                using (var picker = new Form())
                {
                    picker.Text = "Select OS Version";
                    picker.Size = new System.Drawing.Size(500, 400);
                    picker.StartPosition = FormStartPosition.CenterParent;
                    picker.FormBorderStyle = FormBorderStyle.FixedDialog;
                    picker.MaximizeBox = false;
                    picker.MinimizeBox = false;

                    var pickerLabel = new Label
                    {
                        Text = "Select the version matching your controller OS:",
                        Dock = DockStyle.Top,
                        Height = 30,
                        Padding = new Padding(5)
                    };

                    var listBox = new ListBox
                    {
                        Dock = DockStyle.Fill,
                        Font = new System.Drawing.Font("Consolas", 10F)
                    };
                    foreach (var v in versions)
                        listBox.Items.Add(v);

                    var okButton = new Button
                    {
                        Text = "Download && Install",
                        Dock = DockStyle.Bottom,
                        Height = 40,
                        DialogResult = DialogResult.OK
                    };

                    picker.Controls.Add(listBox);
                    picker.Controls.Add(pickerLabel);
                    picker.Controls.Add(okButton);
                    picker.AcceptButton = okButton;

                    if (picker.ShowDialog() != DialogResult.OK || listBox.SelectedItem == null)
                    {
                        log.WriteError("Cancelled.\n");
                        return;
                    }

                    selectedVersion = listBox.SelectedItem.ToString();
                }

                log.WriteHighlight($"Selected: {selectedVersion}\n\n");

                // Get packages for selected version
                log.WriteNormal("Querying packages for this version...\n");
                string pkgName, pkgUrl, pkgChecksum;
                long pkgSize;
                if (!GetDriversPackageInfo(selectedVersion, out pkgName, out pkgUrl, out pkgSize, out pkgChecksum))
                {
                    log.WriteError("No Drivers package found for this version.\n");
                    return;
                }

                log.WriteNormal("Package: ");
                log.WriteHighlight($"{pkgName}\n");
                log.WriteNormal("URL: ");
                log.WriteHighlight($"{pkgUrl}\n");
                log.WriteNormal("Size: ");
                log.WriteHighlight($"{pkgSize / 1024 / 1024} MB\n\n");

                // Download to temp
                var tempFile = Path.Combine(Path.GetTempPath(), pkgName);
                log.WriteNormal($"Downloading to {tempFile}...\n");
                log.WriteNormal("This may take a few minutes for large files.\n\n");

                using (var client = new WebClient())
                {
                    client.DownloadFile(pkgUrl, tempFile);
                }

                log.WriteHighlight("Download complete!\n\n");

                // Verify checksum
                if (!string.IsNullOrEmpty(pkgChecksum))
                {
                    log.WriteNormal("Verifying MD5 checksum...\n");
                    var md5 = ComputeMD5(tempFile);
                    if (string.Equals(md5, pkgChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        log.WriteHighlight("Checksum verified OK.\n\n");
                    }
                    else
                    {
                        log.WriteError($"Checksum mismatch! Expected {pkgChecksum}, got {md5}\n");
                        log.WriteError("The download may be corrupted. Aborting.\n");
                        return;
                    }
                }

                // Run installer
                log.WriteNormal("Launching installer...\n");
                Process.Start(tempFile);
                log.WriteSuccess("Installer launched. Follow the prompts to complete installation.\n");
                log.WriteSuccess("After installation, you can launch Composer.\n");
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

        private static string ComputeMD5(string filename)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var stream = File.OpenRead(filename))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private void SearchGoogleForComposer(object sender, EventArgs e)
        {
            Process.Start("https://www.google.com/search?q=ComposerPro-3.1.3.574885-res.exe");
        }

        private void OpenControl4Reddit(object sender, EventArgs e)
        {
            Process.Start("https://www.reddit.com/r/C4diy/");
        }

        private void UpdateCertificates(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Update Composer Certificates");
            try
            {
                UpdateComposerCertificate(log, checkBoxBlockSplitIo.Checked);
            }
            catch (Exception ex)
            {
                log.WriteError(ex);
            }
        }

        private static string GetComposerConfigFolder()
        {
            var configFolder = $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}/Control4";
            // These directories won't exist if Composer has not yet been opened.
            Directory.CreateDirectory($"{configFolder}/Composer");
            return configFolder;
        }

        private static bool GenerateComposerCert(LogWindow log)
        {
            log.WriteNormal("\nCreating Signing Request + Key\n");
            var exitCode = RunProcessPrintOutput(
                log,
                Constants.OpenSslExe,
                "req -new -nodes " +
                $"-newkey rsa:2048 -keyout {Constants.CertsFolder}/composer.key " +
                $"-subj \"/C=US/ST=Utah/L=Draper/CN={Constants.CertificateCn}\" " +
                $"-out {Constants.CertsFolder}/composer.csr"
            );

            if (exitCode != 0)
            {
                log.WriteError("Failed.");
                return false;
            }

            WriteFile(log, $"{Constants.CertsFolder}/ext.conf",
                "[v3_client]\n" +
                $"subjectAltName=DNS:{Constants.CertificateCn}\n" +
                "extendedKeyUsage=clientAuth,serverAuth\n" +
                "basicConstraints=CA:FALSE\n" +
                "keyUsage=digitalSignature,keyEncipherment");

            log.WriteNormal("\nSigning Request\n");
            exitCode = RunProcessPrintOutput(
                log,
                Constants.OpenSslExe,
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
                log.WriteError("Failed.");
                return false;
            }

            log.WriteNormal("Creating composer.p12\n");
            exitCode = RunProcessPrintOutput(
                log,
                Constants.OpenSslExe,
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
                log.WriteError("Failed.");
                return false;
            }

            return true;
        }

        private static void DeployComposerFiles(LogWindow log, string configFolder)
        {
            CopyFile(log, $"{Constants.CertsFolder}/{Constants.ComposerCertName}",
                $"{configFolder}/Composer/{Constants.ComposerCertName}");
            CopyFile(log, $"{Constants.CertsFolder}/composer.p12", $"{configFolder}/Composer/composer.p12");
        }

        private static void WriteFeatureFlags(LogWindow log, string configFolder)
        {
            // Override split.io feature flags locally so Composer works correctly
            // when split.io is blocked (or unreachable). Without these cached values,
            // the FeatureOffline fallback returns false/null for unknown flags, which
            // breaks connection and update flows.
            //
            // Values below match what split.io actually returns for a working session:
            //   composer-x4-updatemanger-restrict-override - bypasses dealer auth for X4 updates
            //   connection-whitelist - must be false with empty "[]" config (disables restrictions)
            //   os-pack-on-connect - enables management pack check on connect
            WriteFile(log, $"{configFolder}/Composer/FeaturesConfiguration.json",
                @"{" +
                @"""composer-x4-updatemanger-restrict-override"":{""Result"":true,""Config"":null}," +
                @"""connection-whitelist"":{""Result"":false,""Config"":""[]""}," +
                @"""os-pack-on-connect"":{""Result"":true,""Config"":null}" +
                @"}");
        }

        private static void UpdateUpdateManagerSettings(LogWindow log, string configFolder)
        {
            // Set the Update Manager's saved URL list to the experience endpoint.
            // The native Control4ClientRT.dll hardcodes Services.UpdatesUrl to the
            // old Updates2x endpoint and ignores config overrides. By writing the
            // experience URL into the settings file, it appears in the dropdown.
            var settingsPath = $"{configFolder}/Composer/ComposerUpdateManagerSettings.Config";
            try
            {
                XDocument settingsDoc;
                if (File.Exists(settingsPath))
                {
                    settingsDoc = XDocument.Load(settingsPath);
                }
                else
                {
                    settingsDoc = XDocument.Parse("<settings/>");
                }

                var root = settingsDoc.Root ?? new XElement("settings");
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                XNamespace xsd = "http://www.w3.org/2001/XMLSchema";

                // Remove existing URL list and replace with just the experience URL
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
                log.WriteNormal($"Set Update Manager URL to experience endpoint.\n");
            }
            catch (Exception ex)
            {
                log.WriteError($"Could not update settings: {ex.Message}\n");
                log.WriteNormal($"Manually enter this URL in the Update Manager:\n");
                log.WriteHighlight($"  {Constants.UpdatesExperienceUrl}\n");
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
            // The first time opening Composer will stick you in a login loop
            // without this file present.
            if (!File.Exists($"{configFolder}/dealeraccount.xml"))
            {
                // This is just the file contents after entering username=no and password=way
                WriteFile(log, $"{configFolder}/dealeraccount.xml", @"<?xml version=""1.0"" encoding=""utf-8""?>
<DealerAccount>
  <Username>no</Username>
  <Employee>False</Employee>
  <Password>+bJjU5zcsEI=</Password>
  <UserHash>9390298f3fb0c5b160498935d79cb139aef28e1c47358b4bbba61862b9c26e59</UserHash>
</DealerAccount>");
            }
        }

        private static void UpdateComposerCertificate(LogWindow log, bool blockSplitIo = false)
        {
            if (!File.Exists(Constants.OpenSslExe))
            {
                log.WriteError($"Couldn't find {Constants.OpenSslExe} - do you have composer installed?");
                return;
            }

            if (!File.Exists(Constants.OpenSslConfig))
            {
                log.WriteError($"Couldn't find {Constants.OpenSslConfig} - do you have composer installed?");
                return;
            }
            if (Process.GetProcessesByName("ComposerPro").Length > 0)
            {
                log.WriteError("ComposerPro.exe is currently running. Please close Composer and try again.");
                return;
            }

            if (!GenerateComposerCert(log))
                return;

            var configFolder = GetComposerConfigFolder();

            DeployComposerFiles(log, configFolder);
            WriteFeatureFlags(log, configFolder);
            UpdateUpdateManagerSettings(log, configFolder);
            ConfigureSplitIoBlock(log, blockSplitIo);
            EnsureDealerAccount(log, configFolder);

            log.WriteNormal("\n\n");
            log.WriteSuccess("Success - composer should be good for 30 days\n\n");
            log.WriteSuccess(
                "Once it starts complaining that you have x days left to renew, just run this step again\n\n");
            log.WriteSuccess(
                $"You shouldn't need to patch your Director again unless you update to a new version or delete the {Constants.CertsFolder} folder next to this exe.\n\n");
        }

        private static void CopyFile(LogWindow log, string a, string b)
        {
            log.WriteNormal("Copying ");
            log.WriteHighlight(a);
            log.WriteNormal(" to ");
            log.WriteHighlight(b);
            log.WriteNormal("\n");

            File.Copy(a, b, true);
        }

        private static void WriteFile(LogWindow log, string file, string content)
        {
            log.WriteNormal("Writing ");
            log.WriteHighlight(file);
            log.WriteNormal("\n");

            File.WriteAllText(file, content);
        }

        private static void RemoveLineFromFile(LogWindow log, string file, string line, bool ignoreWhitespace = true)
        {
            log.WriteNormal("Removing line '");
            log.WriteTrace(line);
            log.WriteNormal("' from ");
            log.WriteHighlight(file);
            log.WriteNormal("\n");

            if (!File.Exists(file))
            {
                return;
            }
            var lines = File.ReadAllLines(file);
            var newLines = lines.Where(s => ignoreWhitespace
                ? s.Trim() != line.Trim()
                : s != line).ToArray();

            if (newLines.Length != lines.Length)
            {
                File.WriteAllLines(file, newLines);
            }
        }

        private static void AddLineToFile(LogWindow log, string file, string line, bool ignoreWhitespace = true)
        {
            log.WriteNormal("Adding line '");
            log.WriteTrace(line);
            log.WriteNormal("' to ");
            log.WriteHighlight(file);
            log.WriteNormal("\n");

            if (File.Exists(file))
            {
                var lines = File.ReadAllLines(file);
                if (!lines.Select(s => ignoreWhitespace ? s.Trim() : s).Contains(ignoreWhitespace ? line.Trim() : line))
                {
                    File.AppendAllText(file, line.TrimEnd() + Environment.NewLine);
                }
            }
            else
            {
                File.WriteAllText(file, line.TrimEnd() + Environment.NewLine);
            }
        }

        private static int RunProcessPrintOutput(LogWindow log, string exe, string arguments)
        {
            log.WriteNormal(Path.GetFileName(exe));
            log.WriteNormal(" ");
            log.WriteHighlight(arguments);
            log.WriteNormal("\n");

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

            log.WriteNormal("\n");

            return process.ExitCode;
        }
    }
}
