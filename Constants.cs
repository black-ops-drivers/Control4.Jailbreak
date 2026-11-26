namespace Garry.Control4.Jailbreak
{
    public static class Constants
    {
        public const string Version = "9.3";

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
        /// Manifest name of the embedded c4diagd check script (Tools/c4diag/c4diag-check.sh)
        /// </summary>
        public const string C4DiagCheckResource = "Control4.Jailbreak.c4diag-check.sh";

        /// <summary>
        /// Where c4diagd check reports are saved
        /// </summary>
        public const string C4DiagReportsFolder = "c4diag reports";

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
        /// Lifetime, in days, of the forged MQTT JWT (the "exp" claim). The controller's
        /// mosquitto-jwt-auth plugin enforces this, so it can't just be set to a huge value
        /// like the certificate — keep it modest and let the tool re-sign on each run.
        /// </summary>
        public const int JwtExpireDays = 30;

        /// <summary>
        /// Re-sign the MQTT JWT once it gets within this many days of expiring, so a
        /// re-run of the jailbreak refreshes a soon-to-be-stale token proactively.
        /// </summary>
        public const int JwtRefreshMarginDays = 5;

        /// <summary>
        /// The OS version this tool was tested against.
        /// </summary>
        public const string TargetOsVersion = @"4.3.0.766029";

        /// <summary>
        /// The Composer version this tool was tested against.
        /// </summary>
        public const string TargetComposerVersion = @"2026.10.28";

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
        /// Legacy host entry that blocked the Control4 cloud service *locator* (apis.control4.com).
        /// Blocking the locator forces OnlineServicesAvailable=false, which does suppress the
        /// "Register Composer" prompt but also disables every online feature, including Composer's
        /// online driver add/update. Kept only so the jailbreak and revert can strip it from
        /// machines set up by earlier versions; new runs no longer add it.
        /// </summary>
        public const string BlockCloudLocatorHostsEntry = @"127.0.0.1  apis.control4.com apis-beta.control4.com";

        /// <summary>
        /// Host entry blocking services.control4.com, the host of the Composer licensing SOAP
        /// (Services.LicensingUrl = /Security/v1_0/Licensing.asmx). On Composer 2026.x, once the
        /// locator (apis.control4.com) reports online, Composer reauthorizes the Composer license via
        /// ReauthorizeComposerPro on this host; with the jailbreak's dealer account that call is
        /// rejected and the modal "Register Composer" window closes the app. Blocking it makes Composer
        /// fall back to the local license.xml. The locator (apis.control4.com) and the REST cloud API
        /// (apiz.control4.com) are left reachable, so OnlineServicesAvailable stays true and the online
        /// driver add/update actions keep working. Note: this also blocks the Updates2x service on
        /// services.control4.com, so the Update Manager version list and the tool's own management-pack
        /// downloader cannot reach the cloud while the block is in place.
        /// </summary>
        public const string BlockCloudServicesHostsEntry = @"127.0.0.1  services.control4.com";

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
        public const string UpdatesExperienceUrl =
            "https://services.control4.com/Updates2x-experience/v2_0/Updates.asmx";

        /// <summary>
        /// The "external" Updates SOAP endpoint. Beta / not-yet-GA OS builds (and their
        /// management packs) are published only here, not on the main Updates2x endpoint.
        /// The management pack downloader falls back to this when a version isn't found on
        /// the main service. Package download URLs come from the SOAP response itself.
        /// </summary>
        public const string UpdatesExternalUrl =
            "https://services.control4.com/Updates2x-external/v2_0/Updates.asmx";

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

        // -------------------------------------------------------------------
        // Controller (director) file paths — shared by the SSH patch/unpatch code
        // -------------------------------------------------------------------

        /// <summary>Directory holding the controller's SSL certificates.</summary>
        public const string ControllerSslCertsDir = "/opt/control4/etc/ssl/certs";

        /// <summary>Controller API cert bundle the mosquitto-jwt-auth plugin reads.</summary>
        public const string ControllerApiPem = ControllerSslCertsDir + "/api.pem";

        /// <summary>Controller cert used to derive the JWT CommonName claim.</summary>
        public const string ControllerAgentPem = ControllerSslCertsDir + "/agent.pem";

        /// <summary>Fallback controller cert for the JWT CommonName claim.</summary>
        public const string ControllerClientPem = ControllerSslCertsDir + "/client.pem";

        /// <summary>
        /// OS 4.2.1 device cert location for the JWT CommonName claim. On 4.2.1 agent.pem is a
        /// dangling symlink and the device cert moved here (note: /opt/control4/etc/certs, not
        /// the ssl/certs dir). Tried after the legacy paths.
        /// </summary>
        public const string ControllerCvmDevicePem = "/opt/control4/etc/certs/cvm-device.pem";

        /// <summary>Client CA chain under the Control4 SSL directory.</summary>
        public const string ControllerClientCaProdPem = ControllerSslCertsDir + "/clientca-prod.pem";

        /// <summary>OpenVPN client CA chain.</summary>
        public const string OpenVpnClientCaProdPem = "/etc/openvpn/clientca-prod.pem";

        /// <summary>Mosquitto CA chain the controller validates the Composer cert against.</summary>
        public const string MosquittoCaChainPem = "/etc/mosquitto/certs/ca-chain.pem";

        /// <summary>root user's authorized_keys on the controller.</summary>
        public const string ControllerAuthorizedKeys = "/home/root/.ssh/authorized_keys";

        /// <summary>Directory holding the controller's SSH host keys.</summary>
        public const string ControllerSshDir = "/etc/ssh";

        // -------------------------------------------------------------------
        // Process names
        // -------------------------------------------------------------------

        /// <summary>Composer Pro process name (without .exe).</summary>
        public const string ComposerProcessName = "ComposerPro";

        /// <summary>Controller MQTT JWT auth plugin process name.</summary>
        public const string MosquittoJwtAuthProcess = "mosquitto-jwt-auth";

        // -------------------------------------------------------------------
        // Composer / jailbreak file names
        // -------------------------------------------------------------------

        /// <summary>Composer's app config, patched in the install directory.</summary>
        public const string ComposerConfigFileName = "ComposerPro.exe.config";

        /// <summary>Composer client cert (PKCS#12) deployed under %AppData%\Control4\Composer.</summary>
        public const string ComposerP12FileName = "composer.p12";

        /// <summary>Jailbreak CA public cert (Certs folder) appended to the controller cert chains.</summary>
        public const string CaPublicPemFileName = "public.pem";

        /// <summary>Jailbreak API cert (Certs folder) appended to the controller's api.pem.</summary>
        public const string JailbreakApiPemFileName = "jailbreak_api.pem";

        /// <summary>Composer feature-flag cache file (under the Composer config folder).</summary>
        public const string FeaturesConfigFileName = "FeaturesConfiguration.json";

        /// <summary>Fake dealer-account file at %AppData%\Control4.</summary>
        public const string DealerAccountFileName = "dealeraccount.xml";

        /// <summary>License marker file at %AppData%\Control4.</summary>
        public const string LicenseFileName = "license.xml";

        /// <summary>Update Manager settings file (holds the update URL list).</summary>
        public const string UpdateManagerSettingsFileName = "ComposerUpdateManagerSettings.Config";

        /// <summary>Controller SSH host public keys used as authorized keys.</summary>
        public const string SshHostRsaPubKey = "ssh_host_rsa_key.pub";

        public const string SshHostEd25519PubKey = "ssh_host_ed25519_key.pub";
    }
}
