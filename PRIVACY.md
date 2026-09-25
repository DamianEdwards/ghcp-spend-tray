# GHCPSpendTray privacy policy

Last updated: September 24, 2026.

GHCPSpendTray is an independent Windows desktop application maintained by
Damian Edwards. It displays GitHub Copilot AI-credit consumption for accounts you
choose to connect. It is not affiliated with or endorsed by GitHub.

## Information the app uses

When you connect an account, the app uses GitHub's device authorization flow.
You authorize access on the GitHub host's website; you do not enter your GitHub
password into GHCPSpendTray. The app receives access tokens and, where supported
and requested, refresh tokens.

The app retrieves account information such as your login, account ID and host,
and Copilot consumption information such as credits used, allocation, billing
period, reset dates and observation timestamps. It also stores preferences you
choose, including account display names, refresh intervals and alert thresholds.
This information is used to display consumption, maintain history and generate
alerts on your device.

## Where information is stored

Account configuration, consumption history, alert state and diagnostic logs are
stored locally in the app's data directory. For the Store version, this is inside
the Windows package's local application data folder. You can open it from
**Settings > General > Open data folder**.

Access and refresh tokens are stored in Windows Credential Manager, not in the
app's JSON settings or history files. Configuration and history files are not
separately encrypted by GHCPSpendTray; their protection depends on your Windows
account, device security and any disk encryption you use.

History retention defaults to 90 days and is maintained while the app runs.
Recovery copies and files retained after storage errors may remain longer.
Configuration and credentials remain until you remove them or use the applicable
Windows reset/uninstall controls. Diagnostic logs are size-bounded and intended
to contain technical error categories rather than tokens, device authorization
codes, account identities or consumption amounts.

## Network requests and third parties

GHCPSpendTray communicates directly over HTTPS with the GitHub host and API
associated with each connected account for authorization, identity lookup, token
refresh and consumption updates. Those services receive the information needed
for the request, including applicable authorization tokens and ordinary network
information such as your IP address.

GHCPSpendTray does not operate a developer-hosted account or consumption backend,
and the app does not send account information, consumption history or diagnostic
logs to the developer automatically. It does not include app-operated advertising
or analytics tracking.

GitHub's handling of information is governed by its
[privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)
and any policies applicable to your enterprise host. Microsoft Store delivery and
Windows services are governed separately by
[Microsoft's privacy statement](https://privacy.microsoft.com/privacystatement).
Operating-system diagnostics, backups or synchronization you configure may handle
local app data independently of GHCPSpendTray.

## Notifications

If enabled, Windows notifications can display account labels and consumption
information on your desktop. Other people who can see your screen may see that
information. You can disable app notifications in GHCPSpendTray or control their
display through Windows settings.

## Your choices and deletion

- Connecting accounts is optional. You can cancel sign-in before completing it.
- You can change refresh and notification preferences in the app.
- Removing an account stops monitoring it and deletes its app-managed credential.
  Account removal does not necessarily immediately erase every historical,
  recovery or backup file.
- To remove all local app state, remove connected accounts first, exit the app,
  and use the applicable Windows app reset/uninstall controls. Package-local data
  is normally removed by Windows, but external backups may remain.
- Generic Windows Credential Manager entries are not guaranteed to be deleted by
  uninstall. If needed, remove only entries beginning `GHCPSpendTray/` that belong
  to this app.
- Deleting a local credential or uninstalling the app does not revoke its OAuth
  authorization at GitHub. Use **Manage OAuth grants** in account details, or the
  connected host's application settings, to revoke access.

## Support information you choose to share

If you report a problem, share only the information necessary to describe it.
GitHub issues are public. Do not post access tokens, refresh tokens, device codes,
unredacted configuration/history files, or private account and consumption data.
Information you voluntarily post to GitHub is handled under GitHub's policies.

For questions about this policy or the app, contact the maintainer through the
[project's issue tracker](https://github.com/DamianEdwards/ghcp-spend-tray/issues)
without including sensitive information.

## Changes

This policy may be updated to reflect changes in the app. The policy maintained
in the project's repository will describe the applicable data handling.
