namespace Garry.Control4.Jailbreak
{
    public static class Constants
    {
        public const int Version = 7;

        /// <summary>
        /// The cert for composer needs to be named cacert-*.pem
        /// </summary>
        public const string ComposerCertName = "cacert-dev.pem";

        /// <summary>
        /// Needs to start with Composer_ and can be anything after
        /// </summary>
        public const string CertificateCn = "Composer_tech@control4.com_dev";

        /// <summary>
        /// Should always be this unless they change something internally
        /// </summary>
        public const string CertPassword = "R8lvpqtgYiAeyO8j8Pyd";

        /// <summary>
        /// Where the CA and composer certs are stored
        /// </summary>
        public const string CertsFolder = "Certs";

        /// <summary>
        /// Where ssh keys are stored
        /// </summary>
        public const string KeysFolder = "Keys - DO NOT DELETE";

        /// <summary>
        /// How many days until the certificate expires. Doesn't seem any harm in setting this to
        /// a huge value so you don't have to re-crack every year.
        /// </summary>
        public const int CertificateExpireDays = 3650;

        /// <summary>
        /// Where OpenSSL's Config is located (it's installed with Composer)
        /// </summary>
        public const string OpenSslConfig = @"Certs\openssl.cfg";

        /// <summary>
        /// The OS version this tool was tested against.
        /// </summary>
        public const string TargetOsVersion = @"4.1.0.743847";

        /// <summary>
        /// The Composer version this tool was tested against.
        /// </summary>
        public const string TargetComposerVersion = @"2025.11.26";

        /// <summary>
        /// The file path to the Windows Hosts file, typically used for mapping hostnames to IP addresses.
        /// </summary>
        public const string WindowsHostsFile = @"C:\Windows\System32\drivers\etc\hosts";

        /// <summary>
        /// Represents the host entry for Split.io to be added to the system's hosts file,
        /// redirecting "split.io" and "sdk.split.io" to localhost.
        /// </summary>
        public const string BlockSplitIoHostsEntry = @"127.0.0.1  split.io sdk.split.io";

        /// <summary>
        /// The SOAP endpoint for the Control4 Updates service that provides package listings.
        /// Used by the jailbreak tool's own management pack download feature.
        /// </summary>
        public const string UpdatesServiceUrl = "https://services.control4.com/Updates2x/v2_0/Updates.asmx";

        /// <summary>
        /// The "experience" Updates SOAP endpoint that returns X4+ versions.
        /// Normally provided by the cloud service's ConnectStatus.UpdateManagerUrl,
        /// but since we skip cloud auth, we write this into ComposerUpdateManagerSettings.Config.
        /// </summary>
        public const string UpdatesExperienceUrl = "https://services.control4.com/Updates2x-experience/v2_0/Updates.asmx";

        /// <summary>
        /// The XML namespace used in SOAP requests/responses for the Updates service.
        /// </summary>
        public const string UpdatesSoapNamespace = "http://services.control4.com/updates/v2_0/";

        /// <summary>
        /// Bump this when cert generation parameters change (openssl.cfg, key size, subject, etc.).
        /// Stored in Certs/.schema-version. Missing or mismatched triggers root CA regeneration.
        /// </summary>
        public const int CertSchemaVersion = 1;

        /// <summary>
        /// Marker file on the controller written after cert changes. Lives in /tmp so it's
        /// cleared on reboot, letting us detect whether a pending reboot has been completed.
        /// </summary>
        public const string RebootMarkerPath = "/tmp/.jailbreak-reboot-pending";
    }
}
