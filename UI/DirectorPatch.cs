using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class DirectorPatch : UserControl
    {
        private readonly MainWindow _mainWindow;
        private string _cachedJwtToken;

        public DirectorPatch(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            InitializeComponent();
        }

        private ConnectionInfo SshConnection()
        {
            var authMethods = new List<AuthenticationMethod>
            {
                new PasswordAuthenticationMethod(Username.Text, Password.Text),
                // Always add in the default password as an auth method in case it is an
                // unprovisioned controller.
                new PasswordAuthenticationMethod(Username.Text, "t0talc0ntr0l4!")
            };
            // We will just add all known keys since we don't necessarily know which host we are
            // connecting to ahead of time.
            var privateKeyFiles = new List<IPrivateKeySource>();
            // Support for keys in the Keys folder containing MAC address folders with keys
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

        private bool PatchDirector(LogWindow log)
        {
            // Can't patch if we don't have any generated certs.
            if (!File.Exists($"{Constants.CertsFolder}/public.pem"))
            {
                log.WriteError($"Couldn't find {Constants.CertsFolder}/public.pem - have you generated certificates?");
                return false;
            }

            // We must have the MAC address to store ssh keys locally.
            // This way the user can connect to multiple controllers with the same JB tool/folders.
            log.WriteNormal("Connecting to controller to verify MAC address... ");
            var macAddress = GetDirectorMacAddressUsingSsh();
            if (string.IsNullOrEmpty(macAddress))
            {
                log.WriteError("failed\n\n");
                try
                {
                    log.WriteNormal("Attempting to restore SSH password authentication... ");
                    ApplySshRestoreExploit(GetWritableDriverId());
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Reloading SSH service... ");
                    ReloadSshService();
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Waiting for SSH to reload... ");
                    System.Threading.Thread.Sleep(1000);
                    log.WriteSuccess("done\n");

                    log.WriteNormal("Retrieving MAC address from controller API... ");
                    macAddress = GetDirectorMacAddressUsingApi();
                }
                catch (Exception ex)
                {
                    log.WriteError("failed\n\n");
                    log.WriteError(ex);
                    log.WriteError(
                        "\n\nPlease check your ip and password and try again, or enter the MAC address manually.\n\n");
                    return false;
                }
            }

            log.WriteSuccess($"done - got {macAddress}\n\n");
            if (MacAddress.Text != macAddress)
            {
                Invoke((Action)(() =>
                {
                    MacAddress.Text = macAddress;
                    WorkoutPassword();
                }));
            }

            var localKeysFolder = $"{Constants.KeysFolder}/{macAddress}";
            if (!Directory.Exists(localKeysFolder))
            {
                log.WriteTrace($"Creating {localKeysFolder} Folder\n");
                Directory.CreateDirectory(localKeysFolder);
            }

            using (var scp = new ScpClient(SshConnection()))
            {
                log.WriteNormal("Connecting to director via SCP... ");
                scp.Connect();
                log.WriteSuccess("connected\n\n");

                // Always download the controller's ssh keys so they can be used to connect later.
                // This is especially important when jailbreaking OS3 before upgrading to X4.
                if (DownloadRootDeviceSshKeys(log, scp, localKeysFolder))
                {
                    PatchDirectorySshAuthorizedKeysFile(log, scp, localKeysFolder);
                }

                log.WriteNormal($"Reading {Constants.CertsFolder}/public.pem... ");
                var localCert = File.ReadAllText($"{Constants.CertsFolder}/public.pem").Trim();
                log.WriteSuccess($"done - got {localCert.Length} bytes\n\n");

                // Patch all client CA cert chain files that Director and nginx use.
                // OS 4.0 and earlier used /etc/openvpn/clientca-prod.pem.
                // OS 4.1+ moved to /opt/control4/etc/ssl/certs/clientca-prod.pem for both
                // the Director (port 5021) and nginx (port 443).
                PatchRemoteCertChain(log, scp, "/etc/openvpn/clientca-prod.pem", localCert);
                PatchRemoteCertChain(log, scp, "/opt/control4/etc/ssl/certs/clientca-prod.pem", localCert);

                // Also patch the MQTT broker CA chain
                PatchRemoteCertChain(log, scp, "/etc/mosquitto/certs/ca-chain.pem", localCert);
            }

            // Reload nginx so it picks up the new client CA cert
            using (var ssh = new SshClient(SshConnection()))
            {
                ssh.Connect();
                log.WriteNormal("\nReloading nginx to apply cert changes... ");
                ssh.RunCommand("nginx -s reload");
                log.WriteSuccess("done\n");
                ssh.Disconnect();
            }

            return true;
        }

        private static void PatchRemoteCertChain(LogWindow log, ScpClient scp, string remoteFile, string localCert)
        {
            log.WriteNormal($"Patching {remoteFile}:\n");

            string remoteCertChain;
            try
            {
                log.WriteNormal($"  Downloading {remoteFile}... ");
                remoteCertChain = DownloadFile(scp, remoteFile).Trim();
                log.WriteSuccess($"done - got {remoteCertChain.Length} bytes\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("  file doesn't exist - skipping\n");
                return;
            }

            // Dedupe here just to ensure we don't need to remove any previous duplicate JB certs.
            var dedupedRemoteCertChain = DedupeX509CertChain(remoteCertChain);
            if (remoteCertChain == dedupedRemoteCertChain && remoteCertChain.Contains(localCert))
            {
                log.WriteTrace("  (already patched)\n");
                return;
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

            // We just add our public key to the end then dedupe the certs. Dedupe is important
            // because if multiple certs end up in this file with the same subject, the
            // controller appears to not load any CAs and composer will not connect.
            remoteCertChain = DedupeX509CertChain(dedupedRemoteCertChain + "\n" + localCert);

            log.WriteNormal($"  Updating {remoteFile}... ");
            UploadFile(scp, remoteFile, remoteCertChain);
            log.WriteSuccess("done\n");
        }

        private static string DedupeX509CertChain(string certChain)
        {
            return string.Join("\n", certChain
                    .Split(new[] { "-----END CERTIFICATE-----" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(cert => !string.IsNullOrWhiteSpace(cert))
                    .Select(cert => cert.Trim() + "\n-----END CERTIFICATE-----")
                    .GroupBy(cert => new X509Certificate2(Encoding.UTF8.GetBytes(cert)).Subject)
                    // Prefer the last cert as it is more likely to be more recently added
                    .Select(group => group.Last()))
                .Trim();
        }

        private static bool DownloadRootDeviceSshKeys(LogWindow log, ScpClient scp, string localKeysFolder)
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

                // We need to ensure the key hasn't changed as it will after a factory reset.
                if (File.Exists(localFile) && File.ReadAllText(localFile) == key)
                {
                    log.WriteSuccess("ignoring - file already exists\n");
                    continue;
                }

                File.WriteAllText(localFile, key);
                log.WriteSuccess($"done - got {key.Length} bytes\n");
                keysDownloaded = true;
            }

            if (keysDownloaded)
            {
                log.WriteHighlight(
                    $"\n-- ATTENTION ---\nIf you lose the '{Constants.KeysFolder}' folder, " +
                    "connecting to X4 systems becomes more difficult! While SSH password " +
                    "authentication can currently be restored using customer credentials, this " +
                    "capability may not always be available in future firmware versions.\n\n" +
                    "Back up this folder somewhere safe to avoid the hassle later.\n" +
                    "----------------\n");
            }

            log.WriteNormal("\n");
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

                // Get the existing files
                log.WriteNormal($"  Reading {localPubKeyFile}... ");
                localPubKeys.Add(File.ReadAllText(localPubKeyFile).Trim());
                log.WriteSuccess("done\n");
            }

            log.WriteNormal($"  Downloading {remoteAuthorizedKeysFile}... ");
            var authorizedKeys = "";
            try
            {
                authorizedKeys = DownloadFile(scp, remoteAuthorizedKeysFile);
                log.WriteSuccess($"done - got {authorizedKeys.Length} bytes\n");
            }
            catch (ScpException)
            {
                log.WriteTrace("ignoring - file doesnt exist\n");
            }

            if (localPubKeys.All(localPubKey => authorizedKeys.Contains(localPubKey)))
            {
                log.WriteTrace("  (already patched)\n\n");
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
                    // We just add our public key to the end
                    authorizedKeys = authorizedKeys.Trim() + "\n" + localPubKey.Trim();
                }
            }

            log.WriteNormal("  Updating remote authorized_keys file... ");
            UploadFile(scp, remoteAuthorizedKeysFile, authorizedKeys);
            log.WriteSuccess("done\n\n");
        }

        private void PatchDirectorCertificates(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Patch Director");
            try
            {
                if (PatchDirector(log))
                {
                    log.WriteSuccess("\nFinished!");
                }
            }
            catch (Exception ex)
            {
                log.WriteError("\n" + ex);
            }
        }

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
                    stream.Position = 0; // Reset the position before uploading
                    scp.Upload(stream, remoteFilename);
                }
            }
        }


        private void OnIpAddressChanged(object sender, EventArgs e)
        {
            _ = WorkoutMacAddress();
        }

        private async Task WorkoutMacAddress()
        {
            var ipAddress = IpAddress.Text;

            await Task.Run(() =>
                {
                    var macAddress = GetDirectorMacAddressUsingArp(ipAddress);
                    if (!string.IsNullOrEmpty(macAddress))
                    {
                        Invoke((Action)(() =>
                        {
                            if (IpAddress.Text == ipAddress)
                            {
                                MacAddress.Text = macAddress;
                            }
                        }));
                    }
                }
            );
        }

        private void OnMacAddressChanged(object sender, EventArgs e)
        {
            WorkoutPassword();
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

        private string GetDirectorMacAddressUsingSsh()
        {
            using (var client = new SshClient(SshConnection()))
            {
                try
                {
                    client.Connect();

                    foreach (var command in new[]
                             {
                                 // Prefer the MAC of eth0, but fall back to any interface
                                 "ip link show eth0",
                                 "ip link show"
                             })
                    {
                        // Use "ip link show" or any other command suitable for your target machine
                        var commandOutput = client.RunCommand(command).Result;
                        // Parse the output to find the MAC address
                        var lines = commandOutput.Split('\n');
                        foreach (var line in lines)
                        {
                            if (!line.Contains(" link/ether "))
                            {
                                continue;
                            }

                            // Extract MAC address after "ether"
                            var parts = line.Trim().Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                            var etherIndex = Array.IndexOf(parts, "link/ether");
                            if (etherIndex == -1 || etherIndex + 1 >= parts.Length)
                            {
                                continue;
                            }

                            var macAddress = parts[etherIndex + 1];
                            return macAddress.Replace(":", "").ToUpper();
                        }
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            return null;
        }


        // ReSharper disable InconsistentNaming
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int DestIP, int SrcIP, [Out] byte[] pMacAddr, ref int PhyAddrLen);
        // ReSharper restore InconsistentNaming

        private static string GetDirectorMacAddressUsingArp(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress) || !IPAddress.TryParse(ipAddress, out var hostIpAddress))
            {
                return null;
            }

            try
            {
                var ab = new byte[6];
                var len = ab.Length;
                var r = SendARP(BitConverter.ToInt32(hostIpAddress.GetAddressBytes(), 0), 0, ab, ref len);
                return r != 0 ? null : BitConverter.ToString(ab, 0, 6).Replace("-", "").ToUpper();
            }
            catch (Exception)
            {
                return null;
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

        private static System.Net.Http.HttpClient CreateHttpClient()
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
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

                // Parse JSON to find driver ID
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
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

        private string GetDirectorMacAddressUsingApi()
        {
            using (var client = CreateHttpClient())
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetJwtToken()}");

                var response = client.GetAsync($"https://{IpAddress.Text}:443/api/v1/platform_status").Result;
                response.EnsureSuccessStatusCode();
                var content = response.Content.ReadAsStringAsync().Result;

                var serializer = new JavaScriptSerializer();
                if (serializer.DeserializeObject(content) is Dictionary<string, object> data &&
                    data.TryGetValue("directorMAC", out var macObj))
                {
                    var macAddress = macObj.ToString().Replace(":", "").ToUpper();
                    if (macAddress.Length == 12)
                    {
                        return macAddress;
                    }
                }
            }

            throw new Exception("Failed to get director MAC address from API response");
        }

        private string GetJwtToken()
        {
            // Return cached token if we already have one
            if (!string.IsNullOrEmpty(_cachedJwtToken))
            {
                return _cachedJwtToken;
            }

            // Show login dialog for customer.control4.com credentials
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
                response.EnsureSuccessStatusCode();

                var responseContent = response.Content.ReadAsStringAsync().Result;

                if (serializer.DeserializeObject(responseContent) is Dictionary<string, object> responseData &&
                    responseData.TryGetValue("token", out var value))
                {
                    _cachedJwtToken = value.ToString();
                    return _cachedJwtToken;
                }

                throw new Exception("Invalid credentials provided!");
            }
        }

        private void RebootDirector(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Reboot Director");

            try
            {
                log.WriteNormal("Connecting to director... ");

                using (var ssh = new SshClient(SshConnection()))
                {
                    ssh.Connect();

                    log.WriteSuccess("connected\n");

                    log.WriteNormal("Running Reboot Command... ");
                    // Use nohup here to ensure the reboot command is executed without requiring the
                    // SSH session to remain active.
                    // The delay allows us to disconnect before the actual reboot, so no exceptions
                    // are thrown to the user.
                    ssh.RunCommand("nohup sh -c '( sleep 2 ; reboot )' >/dev/null 2>&1 &");
                    ssh.Disconnect();
                    log.WriteSuccess("done\n\n");

                    log.WriteHighlight(
                        "Your system is rebooting - it can take a while - don't panic, give it 10 minutes!");
                }
            }
            catch (Exception ex)
            {
                log.WriteError("failed\n\n");
                log.WriteError(ex);
            }
        }
    }
}