using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Garry.Control4.Jailbreak.UI
{
    public partial class Certificates : UserControl
    {
        private readonly MainWindow _mainWindow;

        public Certificates(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            InitializeComponent();
        }

        private void GenerateCertificates(object sender, EventArgs e)
        {
            var log = new LogWindow(_mainWindow, "Generate Certificates");
            if (!GenerateCertificates(log))
            {
                return;
            }

            log.WriteSuccess("Certificate Generation Successful");
            log.WriteNormal("\n\n");
        }

        private void ViewCertificates(object sender, EventArgs e)
        {
            var folder = Path.GetFullPath(Constants.CertsFolder);

            if (!Directory.Exists(folder))
            {
                var log = new LogWindow(_mainWindow);
                log.WriteError($"{folder}doesn't exist - did you generate certificates yet?\n");
                return;
            }

            Process.Start("explorer.exe", folder);
        }

        private static bool GenerateCertificates(LogWindow log)
        {
            if (!File.Exists(Constants.OpenSslExe))
            {
                log.WriteError($"Couldn't find {Constants.OpenSslExe} - do you have composer installed?");
                return false;
            }

            if (!File.Exists(Constants.OpenSslConfig))
            {
                log.WriteError($"Couldn't find {Constants.OpenSslConfig} - do you have composer installed?");
                return false;
            }

            if (!Directory.Exists(Constants.CertsFolder))
            {
                log.WriteTrace($"Creating {Constants.CertsFolder} Folder\n");
                Directory.CreateDirectory(Constants.CertsFolder);
            }

            //
            // Don't regenerate the certificates. They might be copying the folder
            // over to another computer or some shit.
            //
            if (File.Exists($"{Constants.CertsFolder}/{Constants.ComposerCertName}") &&
                File.Exists($"{Constants.CertsFolder}/private.key") &&
                File.Exists($"{Constants.CertsFolder}/public.pem"))
            {
                log.WriteSuccess("\nThe certificates already exist - so we're going to use them.\n");
                System.Threading.Thread.Sleep(1000);
                log.WriteSuccess(
                    $"If you want to generate new certificates delete the {Constants.CertsFolder} folder.\n\n");
            }
            else
            {
                //
                // generate a self-signed private and public key
                //
                log.WriteNormal("\nGenerating private + public keys\n");
                var exitCode = RunProcessPrintOutput(
                    log,
                    Constants.OpenSslExe,
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
                    log.WriteError("Failed.");
                    return false;
                }
            }

            //
            // Get the text for the composer cacert-*.pem
            //
            log.WriteNormal($"Creating {Constants.ComposerCertName}\n");
            var output = RunProcessGetOutput(
                Constants.OpenSslExe,
                $"x509 -in \"{Constants.CertsFolder}/public.pem\" -text"
            );
            File.WriteAllText($"{Constants.CertsFolder}/{Constants.ComposerCertName}", output);

            return true;
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

        private static int RunProcessPrintOutput(LogWindow log, string exe, string arguments)
        {
            log.WriteNormal(Path.GetFileName(exe));
            log.WriteNormal(" ");
            log.WriteHighlight(arguments);
            log.WriteNormal("\n");

            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                EnvironmentVariables = { ["OPENSSL_CONF"] = Path.GetFullPath(Constants.OpenSslConfig) }
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                log.WriteError($"Failed to start {exe} {arguments}\n");
                return -1;
            }

            process.WaitForExit();

            var text = process.StandardOutput.ReadToEnd();

            log.WriteTrace(text);

            log.WriteNormal("\n");

            return process.ExitCode;
        }
    }
}