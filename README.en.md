# ProxyDock

[Русский](README.md) · [Release notes in English and Russian](docs/releases/v0.1.1.md)

ProxyDock launches Windows 11 x64 desktop apps with a per-app HTTP proxy. It sets the proxy environment on the launched process and can pass Chromium's proxy flag. Windows proxy settings and other running apps keep their own configuration.

## Download v0.1.1

| Distribution | Updates |
| --- | --- |
| [Windows x64 installer](https://github.com/RomanBukatov/ProxyDock/releases/download/v0.1.1/ProxyDockApp-win-Setup.exe) | Installs for the current user and updates through Velopack. |
| [Portable ZIP](https://github.com/RomanBukatov/ProxyDock/releases/download/v0.1.1/ProxyDockApp-win-Portable.zip) | Extract the entire folder to keep Velopack updates available. |
| [Standalone executable](https://github.com/RomanBukatov/ProxyDock/releases/download/v0.1.1/ProxyDock-v0.1.1-win-x64-standalone.exe) | Replace the executable manually when updating. |

Installer and portable users on v0.1.0 can check for v0.1.1 in **Settings → Updates**. Updates require confirmation. The release includes [SHA-256 checksums](https://github.com/RomanBukatov/ProxyDock/releases/download/v0.1.1/SHA256SUMS.txt).

## Launch and manage an app

1. Add an HTTP proxy and test the connection.
2. Add a detected Claude Desktop, Codex Desktop or Google Antigravity profile, or select a Windows executable. Choose the proxy or direct connection on the app card.
3. Launch the app from the card. To apply a different proxy to a running app, select it and restart the app from ProxyDock.

For Codex Desktop, graceful exit uses the Quit command in the open window's File menu. Claude Desktop handles a Windows session-end notification. If processes remain, the card offers force quit with confirmation. ProxyDock reports the PID and Win32 error code when Windows denies termination. Restart waits for the old instance to exit. The separately installed Claude and ChatGPT system services can remain visible in Task Manager.

ProxyDock stores settings under `%LOCALAPPDATA%\ProxyDock` and protects saved proxy passwords with Windows DPAPI. Closing ProxyDock leaves launched apps running. The UI supports Russian and English.

## Build and test

Use Windows x64 and the .NET 10 SDK. Published binaries include their runtime.

```powershell
dotnet build ProxyDock.slnx -c Release
dotnet test tests/ProxyDock.Tests -c Release --no-build --filter "Category!=LocalLive"
```
