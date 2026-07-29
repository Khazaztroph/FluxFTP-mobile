# FluxFTP Mobile

FluxFTP Mobile is the Android companion to
[FluxFTP](https://github.com/Khazaztroph/FluxFTP). It is built with .NET MAUI
and shares its FTP/FTPS transport contracts and site format with the desktop
application.

## Version 1.3.0

- FTP, explicit FTPS, implicit FTPS and SFTP
- SHA256 SSH host-key verification with trust-on-first-use confirmation
- Site Manager with passwords protected by Android Secure Storage
- Dual-pane local/remote browser
- Android Storage Access Framework file and folder selection
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
