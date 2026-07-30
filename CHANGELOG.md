# Changelog

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
