# Changelog

## 1.8.0 — 2026-08-04

- Ported the mobile-relevant compatibility fixes from FluxFTP 1.0.33 and 1.0.34.
- Explicit FTPS falls back from `AUTH TLS` to `AUTH SSL` when required by an older server.
- Automatic TLS mode reconnects with TLS 1.2 only after a malformed TLS frame from a server or bouncer.
- Forced TLS 1.3 never silently falls back to TLS 1.2.
- The mobile status bar now shows the negotiated TLS version and cipher suite, or SFTP/SSH.

## 1.7.0 — 2026-08-03

- Added an explicit per-site FTPS TLS policy.
- Automatic mode negotiates TLS 1.3 or TLS 1.2 and remains the default.
- Added options to require TLS 1.3 or use TLS 1.2 only for compatibility.
- Applied the selected policy to both FTPS control and data connections.
- TLS 1.0 and TLS 1.1 remain disabled in every mode.

## 1.6.0 — 2026-08-03

- Ported the FlashFXP-inspired transfer status details from FluxFTP 1.0.29.
- Added elapsed time and estimated remaining time to the mobile status bar.
- Improved live speed sampling with a smoothed current-speed value.
- Ported the working SFTP improvements from FluxFTP 1.0.28.
- Added SFTP symbolic-link navigation and shared-core rename, delete, chmod and keep-alive operations.

## 1.5.0 — 2026-07-30

- Ported the relevant live-throughput behavior from FluxFTP 1.0.26.
- Added live speed calculated from the actual FTP/SFTP byte stream.
- Added a compact bottom status bar for connection state, current activity, bytes, percentage and speed.
- Added an activity indicator while connecting, listing and transferring.

## 1.4.0 — 2026-07-30

- Added the FluxFTP 1.0.25-compatible `Broken PASV` site option.
- Broken PASV sites use PORT/active FTP immediately for listings, uploads and downloads.
- Confirmed SFTP browsing and transfers through SSH.NET, including saved SHA256 host-key verification.
- Kept Broken PASV isolated from SFTP sites.

## 1.3.1 — 2026-07-29

- Matched the Android interface to the FluxFTP desktop color palette.
- Replaced the purple MAUI styling with FluxFTP dark blue, teal and cyan colors.
- Added the FluxFTP wordmark to the mobile navigation bar.
- Reworked the Android launcher icon and splash screen to use the FluxFTP server identity.
- Applied the new identity to buttons, lists, forms, progress indicators and Android system chrome.

## 1.3.0 — 2026-07-29

- Replaced the wide site picker with a compact `Sites` action button.
- Added persistent single-pane and dual-pane modes.
- Made the remote file browser full width in single-pane mode on small screens.
- Preserved the selected site and view mode between launches.
- Ported the relevant FluxFTP 1.0.19-era FTP listing fix.
- Added reconnect and resume handling for interrupted uploads.

## 1.2.0 — 2026-07-27

Initial public Android release.

- Added FTP, explicit FTPS, implicit FTPS and SFTP connections.
- Added Site Manager and Android Secure Storage.
- Added SHA256 SSH host-key verification.
- Added SAF file and recursive folder selection.
- Added multi-selection and batch transfers.
- Added recursive remote-folder downloads as ZIP.
- Added Android 13/MIUI-compatible ARM64 Release packaging.
