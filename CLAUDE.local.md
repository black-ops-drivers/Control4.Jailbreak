# Control4 Jailbreak Tool - Technical Knowledge Base

## Project Overview
A WinForms C# app that enables DIY users to use Control4 Composer Pro without a dealer account. It patches certificates, configs, and feature flags to bypass authentication and licensing checks.

## Architecture

### How Composer Authentication Works
1. **Certificates**: Composer connects to the controller's Director service over TLS. The controller validates the Composer certificate against its CA store at `/etc/mosquitto/certs/ca-chain.pem`. The jailbreak tool generates a self-signed CA + Composer cert and deploys them to both sides.
2. **Dead Proxy**: `ComposerPro.exe.config` is patched with `<defaultProxy>` pointing to `http://127.0.0.1:31337/` which blocks outbound HTTP (license validation, dealer auth). A `<bypasslist>` allows `services.control4.com`, `update2.control4.com`, and `c4updates.control4.com` through for updates.
3. **Feature Flags**: Control4 uses Split.io SDK for feature flags. When split.io is blocked or unreachable, `FeatureOffline` reads cached values from `%AppData%/Control4/Composer/FeaturesConfiguration.json`.
4. **Dealer Account**: A fake `dealeraccount.xml` with username=`no` password=`way` prevents login loops.

### Key File Locations (Windows VM)
- Composer install: `C:\Program Files (x86)\Control4\Composer\Pro\`
- Composer config: `ComposerPro.exe.config` (in install dir)
- Feature flags cache: `%AppData%\Control4\Composer\FeaturesConfiguration.json`
- Dealer account: `%AppData%\Control4\dealeraccount.xml`
- Composer cert: `%AppData%\Control4\Composer\cacert-dev.pem`
- Update settings: `%AppData%\Control4\Composer\ComposerUpdateManagerSettings.Config`
- OpenSSL config: `Certs\openssl.cfg` (in jailbreak tool dir)

### Key File Locations (Controller - Linux)
- CA cert chain: `/etc/mosquitto/certs/ca-chain.pem`
- SSH access: root user, used for deploying certs

## Feature Flag System

### How It Works
1. `FeatureFlag.cs` (online): Calls `SplitFactory.Client().GetTreatment(key, featureName)`
2. If split.io returns `"control"` (SDK can't reach servers), falls back to `FeatureOffline`
3. `FeatureOffline.cs`: Reads `FeaturesConfiguration.json` as `Dictionary<string, ConfigurationResult>`
4. Lookup tries `key/featureName` first (where key = dealer username), then just `featureName`
5. If no cached value exists, returns `null` which resolves to `false`
6. `ConfigurationResult` = `{ Result: bool, Config: string }`
7. When split.io IS reachable, results are cached to JSON via `RefreshOfflineConfiguration()`
8. `BlockUntilReady(10000)` - 10-second timeout on split.io SDK initialization

### Known Feature Flags (from FeatureV2.cs)
| Flag Name | Purpose | Required Value |
|---|---|---|
| `composer-x4-updatemanger-restrict-override` | Skips cloud-based dealer auth in Update Manager | `true` (Config: null) |
| `connection-whitelist` | Controls connection restrictions in native RT DLL | `false` (Config: "[]") |
| `os-pack-on-connect` | Enables management pack check on connect | `true` (Config: null) |
| `completely-disable-terminal` | Controls terminal access | varies |
| `refactor-startup` | Affects startup/connection flow | unclear |
| `refactor-project-properties` | Project properties UI | unused |
| `composer-disable-zigbee-modification-delay` | ZigBee timing | varies |
| `enable-remove-registration` | Registration removal UI | varies |
| `composer-gen4-lux-lighting` | Gen4 Lux lighting features | varies |
| `update-manager-report` | Update manager reporting | varies |
| `composer-enable-light-4766-changes` | Light driver changes | varies |

### Current FeaturesConfiguration.json
```json
{
  "composer-x4-updatemanger-restrict-override": {"Result": true, "Config": null},
  "connection-whitelist": {"Result": false, "Config": "[]"},
  "os-pack-on-connect": {"Result": true, "Config": null}
}
```
**CRITICAL**: `connection-whitelist` must be `false` with `Config: "[]"`. Setting it to `true` or omitting it causes "Unable to communicate with project" in the Connect to Project dialog.

### What Happens When Split.io is Blocked
- Hosts file: `127.0.0.1  split.io sdk.split.io`
- SDK `BlockUntilReady(10000)` times out after 10 seconds
- `GetTreatment()` returns `"control"` for all flags
- Falls back to `FeatureOffline` which reads the JSON file
- Any flag NOT in the JSON returns `false` by default
- This is why we must pre-populate ALL required flags in the JSON

## Update Manager

### Version Fetching Flow
1. On load, checks `composer-x4-updatemanger-restrict-override` flag
2. If flag is FALSE (normal flow): calls `GetConnectStatusAsync()` which authenticates with cloud services and returns `ConnectStatus` containing `UpdateManagerUrl`, `X4UpdatesUrl`, `LegacyUpdatesUrl`
3. If flag is TRUE (our override): skips cloud auth, uses `Services.UpdatesUrl` as default (the old `Updates2x` endpoint)
4. `RefreshVersions()` calls `GetVersions(URL)` which calls `GetAuthorizedVersions(commonName, systemVersion, includeEarlierVersions, userAgent)` via SOAP

### Update Service URLs
| URL | Purpose |
|---|---|
| `https://services.control4.com/Updates2x/v2_0/Updates.asmx` | Legacy SOAP endpoint (default in code) |
| `https://services.control4.com/Updates2x-experience/v2_0/Updates.asmx` | X4+ SOAP endpoint (returned by cloud service) |
| `http://update2.control4.com/release/{version}/win/{PackageName}.exe` | Management pack download URL |
| `https://c4updates.control4.com/update` | Apt update server |

### The `-experience` URL Problem
- `ConnectStatus.UpdateManagerUrl` provides the `-experience` URL
- Since we bypass `GetConnectStatusAsync()`, we never get this URL
- Both endpoints return X4 versions via `GetVersions`, BUT `GetAuthorizedVersions` (which the Update Manager uses) requires a real registered controller and the experience endpoint is more permissive with authorization
- `Services.UpdatesUrl` is implemented in native `Control4ClientRT.dll` and **ignores config overrides** - adding `UpdatesURL` to `<UpdateManager>` config section does NOT work
- **Fix**: Write `ComposerUpdateManagerSettings.Config` with BinaryFormatter-serialized Hashtable containing `UpdateURLList30` = ArrayList with the experience URL. This pre-populates the dropdown. User must select it once (persists after that).
- The default URL in the dropdown will still be the old `Updates2x` endpoint (hardcoded in native DLL), but the experience URL appears as an option

### Standard vs Experience Endpoint Comparison
- **Standard (`Updates2x`)**: 58 versions, full 3.x-4.x catalog, uses strict `GetAuthorizedVersions` check
- **Experience (`Updates2x-experience`)**: 9 versions, curated 4.x+ list, more permissive authorization, includes intermediate builds (4.1.0.743847-res, 4.1.0.742633-res+Composer, 4.0.0.734549-res) not on standard endpoint
- Both share: 2025.11.26.463, 2025.10.1.425, 2025.8.20.386, 4.1.0.744089, 4.0.0.734960, 3.4.3.741643

### SOAP Service (Updates2x)
- Namespace: `http://services.control4.com/updates/v2_0/`
- No authentication required for basic operations
- 10 operations: GetAuthorizedVersions, GetVersions, GetPackagesByVersion, GetAllVersions, GetLanguagePackagesByVersion, GetLanguagePackagesByBaseVersion, GetLanguagePackagesByVersionAndName, GetPackagesVersionsByName, GetPackagesVersionsByNameAndByVersions, GetUpdateInfoByVersion
- Version strings ending in `+Composer` return Windows packages (ComposerPro, ComposerHE, Drivers EXEs)
- Version strings ending in `-res` (without `+Composer`) return controller `.deb` packages
- Example: `GetPackagesByVersion("4.1.0.744089-res+Composer")` returns `Drivers-4.1.0.744089-res.exe`

### Management Packs
- Windows EXE installers containing driver definitions
- Downloaded from `http://update2.control4.com/release/{version}/win/{PackageName}.exe`
- Must be installed BEFORE launching Composer if the dead proxy blocks downloads
- `GetPackagesByVersion` returns package name, URL, size, and MD5 checksum
- The jailbreak tool has a built-in management pack download/install feature

## Dead Proxy Configuration

### What It Blocks
Everything except:
- `services.control4.com` (SOAP version queries)
- `update2.control4.com` (firmware/pack downloads)
- `c4updates.control4.com` (apt updates)
- Local addresses (bypassonlocal=true)

### What It Allows Through (via bypasslist)
These are needed for the Update Manager to function:
- Version queries (SOAP calls to services.control4.com)
- Firmware downloads (from update2.control4.com)
- Controller apt updates (from c4updates.control4.com)

### Services Still Blocked
- `my.control4.com` - customer portal / licensing
- `apis.control4.com` - cloud API services
- `drivers.control4.com` - driver downloads (may need bypass if online driver search is desired)
- Split.io domains (if also using hosts block)

## Decompiled Code Locations
Most decompiled code is at `/Users/demiller/Downloads/`:
- `DesignerDecompiled/Control4.Designer.decompiled.cs` - Connection flow, startup, management packs
- `OSUpdateManagerDecompiled/OSUpdateManager.decompiled.cs` - Update Manager form
- `ClientDecompiled/Control4.Client/` - Feature flags, services, connection models
- `FeaturesDecompiled/Control4.Features/` - Split.io integration, offline fallback
- `ComposerDecompiled/` - Main Composer app (heavily obfuscated)
- `CommonDecompiled/Control4.Common.decompiled.cs` - Common utilities

### Native RT DLL
`Control4ClientRT.dll` / `Control4ClientRT64.dll` is a native (non-.NET) DLL that contains the actual implementations of many methods marked with `[MethodImpl(MethodImplOptions.NoInlining)]`. It cannot be decompiled with ILSpy/dnSpy. Key functionality in native code:
- `Services.UpdatesUrl` and all `Services.*Url` properties
- `FeatureV2.Initialize()`
- `connection-whitelist` and `refactor-startup` flag consumption
- `ComposerRestriction.GetConnectionRestriction()` - controls "Unable to communicate" error
- `ControllerConnectionRestriction.CanConnect` and `.ErrorMessage`
- `C4System.Instance.WhatsUp()` - startup/init

### Decompile Command (PowerShell)
```powershell
$composerDir = "$env:LOCALAPPDATA\Control4\Composer"
$outDir = "$env:USERPROFILE\Downloads\AllDecompiled"
New-Item -ItemType Directory -Force -Path $outDir
Get-ChildItem "$composerDir\*.dll","$composerDir\*.exe" | ForEach-Object {
    $name = $_.BaseName
    Write-Host "Decompiling $name..."
    ilspycmd $_.FullName -o "$outDir\$name" 2>$null
}
```

## Known Issues & Fixes

### "Integrator account not authorized" (Update Manager red text)
- **Cause**: `GetConnectStatusAsync()` fails because fake dealer credentials can't authenticate
- **Fix**: Set `composer-x4-updatemanger-restrict-override` = true in FeaturesConfiguration.json

### "Unable to communicate with project" (Connect to Project dialog)
- **Cause**: `connection-whitelist` flag has no cached value when split.io is blocked; native code in RT DLL treats missing value as restrictive
- **Fix**: Set `connection-whitelist` = false with Config = "[]" in FeaturesConfiguration.json
- **Key insight**: This flag must be `false` (disabled), NOT `true`. The whitelist being disabled means no restrictions. An empty config "[]" is an empty exclusion list.

### No update versions shown in Update Manager
- **Cause**: Dead proxy blocks the SOAP call to services.control4.com, AND/OR the default URL (`Updates2x`) doesn't return X4 versions (need `Updates2x-experience`)
- **Fix**: Added bypasslist to dead proxy for services.control4.com; inject `UpdatesURL` into `<UpdateManager>` config section pointing to the `-experience` endpoint
- **Fallback**: User can manually enter `https://services.control4.com/Updates2x-experience/v2_0/Updates.asmx` in the URL combo box

### Management packs section goes off end of GUI
- **Fix**: Added `AutoScroll = true` to the Composer UserControl in Designer.cs

## OS 4.1 Specific Notes
- The `os-4.1-support` branch had WIP work including MQTT cert chain patching and feature flags
- OS 4.1 version string: `4.1.0.744089-res`
- Drivers pack: `Drivers-4.1.0.744089-res.exe` (~127MB)
- The `+Composer` suffix on version strings is needed to get Windows packages from the SOAP service

## Split.io API Key
Encrypted and stored in `app.config`:
```xml
<add key="SplitIOEncryptedKey" value="CZDWphNkIYmiXdSpnQ4Km1OaIxEGwUBGqYeLTxhRw4NXhRGxsnhQbg==" />
```
Retrieved via `C4SystemInformation.GetSplitIoApiKey()`.

## ConnectStatus Cloud Service Response Fields
| Field | Type | Purpose |
|---|---|---|
| `connectRequired` | bool | Whether Connect license is needed |
| `dealerIsConnectEligible` | bool | Whether dealer has Connect eligibility |
| `ovrcCreated` | bool | Whether OVRC account exists |
| `noConnect` | bool | No Connect flag |
| `updateManagerUrl` | string | The `-experience` SOAP URL for version queries |
| `legacyUpdatesUrl` | string | Legacy update URL |
| `x4UpdatesUrl` | string | X4-specific update URL |

## Jailbreak Tool File Structure
- `Constants.cs` - All hardcoded values (URLs, cert names, paths)
- `UI/Composer.cs` - Main Composer tab logic (patching, cert gen, management packs)
- `UI/Composer.Designer.cs` - WinForms UI layout
- `UI/Certificates.cs` - Certificate tab
- `UI/DirectorPatch.cs` - Director/controller patching tab
- `Resources/openssl.cfg` - OpenSSL config for cert generation