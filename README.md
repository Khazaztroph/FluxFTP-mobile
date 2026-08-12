# FluxFTP Mobile

FluxFTP Mobile is the Android companion to
[FluxFTP](https://github.com/Khazaztroph/FluxFTP). It is built with .NET MAUI
and shares its FTP/FTPS transport contracts and site format with the desktop
application.

## Version 1.12.3

Visningsläget **SINGEL/DUAL** ligger högerställt i en egen kolumn och överlappar inte
längre bokmärkesknappen.

Fjärrnavigeringen använder nu en kompakt sökvägsknapp, så att navigeringsknapparna och
**Öppna** alltid ryms på små mobilskärmar. Tryck på sökvägsknappen för att skriva in en
fjärrmapp manuellt.

- FTP, explicit FTPS, implicit FTPS and SFTP
- Per-site Broken PASV mode that uses PORT/active FTP directly
- Live transfer speed, byte progress and activity in a compact bottom status bar
- Elapsed and estimated remaining transfer time
- SFTP symbolic-link navigation plus rename, delete and chmod operations in the shared core
- Per-site FTPS policy: automatic TLS 1.3/1.2, require TLS 1.3, or TLS 1.2 only
- Negotiated TLS version and cipher shown in the mobile connection status
- AUTH SSL compatibility and automatic TLS 1.2 retry for malformed server TLS frames
- Per-site SOCKS4, SOCKS5 and HTTP CONNECT proxy settings for FTP/FTPS
- Secure Storage protection for both site and proxy passwords
- SHA256 SSH host-key verification with trust-on-first-use confirmation
- Site Manager with passwords protected by Android Secure Storage
- Dual-pane local/remote browser
- Android Storage Access Framework file and folder selection
- Navigable SAF local folders with Back, Forward, Up and Refresh
- Persistent local start folder, current-path display and folders-first sorting
- Remote Back/Forward history and per-site bookmarks
- Persistent Name, Size and Modified sorting with folders kept first
- Persistent folders-first or files-first grouping
- Navigable Unix FTP symbolic-link shortcuts such as `!Today_*`
- FluxFTP-style file tables with Name, Size and Modified columns
- Responsive stacked portrait panes and side-by-side landscape panes
- Horizontal toolbar scrolling and independent vertical file-list scrolling
- DrFTPD-compatible FTPS login and protected-data fallback
- PASV-first compatibility for servers that do not advertise EPSV
- Multi-selection and batch transfers
- Recursive folder upload with the directory structure preserved
- Recursive remote-folder download as ZIP
- Transfer progress
- Compact `Sites` action menu instead of a full-width site picker
- Persistent single/dual-pane toggle optimized for small phone screens
- Full-width remote browser in single-pane mode, in portrait and landscape
- FTP `LIST` summary rows are filtered instead of appearing as files
- Interrupted uploads reconnect and resume when supported
- Light, dark and system themes

The first release targets ARM64 Android devices and requires Android 5.0
(API 21) or later. It has been device-tested on Android 13/MIUI 14 over both
Wi-Fi and mobile data.

## Build

Requirements:

- .NET SDK 8
- `maui-android` workload
- Android SDK 34
- JDK 21

```powershell
dotnet workload install maui-android
dotnet restore .\FluxFTP-mobile.sln
dotnet publish .\src\IoFtp.Mobile\IoFtp.Mobile.csproj `
  -c Release -f net8.0-android -p:AndroidPackageFormat=apk
```

The signed APK is generated below
`src/IoFtp.Mobile/bin/Release/net8.0-android/android-arm64/publish/`.

## Security

Passwords are kept outside the site JSON in Android Secure Storage. FTPS
certificates are validated by default. SFTP servers must present the saved
SHA256 host-key fingerprint; a new fingerprint requires explicit confirmation.
