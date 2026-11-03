# Standalone Windows installation

For most users, [install from the Microsoft Store](https://www.microsoft.com/store/productId/9PMX96TSF295)
for automatic update checks and in-app installation. Use the standalone package
when the Store isn't an option.

## Install from GitHub Releases

Requires Windows 11 22H2 (build 22621) or newer, on x64 or ARM64.

1. Open [GitHub Releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases)
   and select a **Windows** release (`v<version>`, not `macos-v<version>`).
2. Download `GHCPSpendTray-<version>.msixbundle`. It contains both Windows
   architectures. The symbols archive and source archives aren't installers.
3. Open the signed bundle in **Windows App Installer** and select **Install**.
4. Launch **GHCPSpendTray** from Start. Click the tray icon, open Settings with
   the gear, and [add your accounts](../README.md#add-your-accounts).

No separate .NET runtime, `gh`, WebView, or backend service is required.
Don't bypass Windows trust warnings or your organization's installation policy.
If installation is blocked, ask your administrator rather than importing an
unverified certificate or disabling protections.

## Update a standalone install

Standalone GitHub builds **don't check for updates automatically** and don't
offer the Microsoft Store update controls in About.

1. Check [Windows releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases?q=GHCPSpendTray+for+Windows)
   for a newer version.
2. Exit GHCPSpendTray from its tray context menu.
3. Download and open the newer signed `.msixbundle`, then apply the update.
4. Launch the app again.

An update with the same package identity preserves settings and history.

## Switching to the Microsoft Store

The Store and GitHub packages have **different identities**: one isn't an
in-place upgrade of the other. Don't assume settings, history, or connected
accounts will migrate. Install the Store copy, connect your accounts, and
reapply your preferences before removing the old copy.

## Development packages aren't public installers

Local `tools\package.ps1` output is an **unsigned development bundle**.
Use signed public releases for ordinary installation.
See [development](DEVELOPMENT.md) or [release packaging](RELEASING.md) if you're
building the app yourself.

[Back to the user guide](../README.md)
