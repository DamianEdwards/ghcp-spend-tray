# Copilot spend in Oh My Posh

A standalone PowerShell integration that adds GitHub Copilot consumption and a
period-spend forecast to an existing Oh My Posh theme. No tray app, custom
executable, .NET SDK, or custom Oh My Posh segment is required.

The compact layout is:

```text
[Copilot icon] $247 (~8%) [colored chart icon] $3.3K
```

These are synthetic example values. Current dollars are first, followed by the
approximate allocation percentage and projected period dollars. Whole dollars
and percentages are rounded; amounts of $1,000 or more use `K` with at most one
decimal (for example, `$10K / $4.4K`). Color is based on the unrounded forecast:

| Forecast relative to allocation | Chart color |
|---|---|
| At most 80% | Green |
| Above 80%, through 100% | Yellow |
| Above 100% | Red |
| Unavailable | Neutral, with `?` instead of a forecast |

The leading glyph is `nf-cod-copilot` normally,
`nf-cod-copilot_in_progress` during background refreshes, and
`nf-cod-copilot_not_connected` after authentication, API, or local read failures.
The projection separator is `nf-md-chart_timeline_variant_shimmer`.

## Requirements

- Windows with PowerShell **7.2 or newer**, not Windows PowerShell 5.1.
- [GitHub CLI](https://cli.github.com/), already authenticated to the desired host.
- [Oh My Posh](https://ohmyposh.dev/) with an existing theme.
- A recent [Nerd Font](https://www.nerdfonts.com/) containing the Copilot and chart
  glyphs, configured in your terminal. Missing glyphs generally indicate an old
  font, not a failed API request.

Windows is the currently verified platform. macOS/Linux shell integration is
not claimed as supported.

## Try it without changing your profile

From the repository root, display all five synthetic forecast/connection states:

```powershell
pwsh -NoProfile -File .\integrations\oh-my-posh\demo.ps1
```

This requires Oh My Posh but does not invoke `gh`, read credentials, or call an
API. It does not replace your prompt.

For an isolated interactive shell using your current `gh` credentials:

```powershell
pwsh -NoLogo -NoProfile -NoExit -File .\integrations\oh-my-posh\demo.ps1 -Live
```

The first prompt starts a background fetch. Press Enter after a few seconds to
display the result. `Show-CopilotPromptExamples` displays synthetic states.
An enterprise host can be selected with `-Hostname your-enterprise.ghe.com`.
The demo prints its unique, user-owned theme/cache directory under
`%LOCALAPPDATA%\GHCPSpendPrompt`; it retains that directory after the interactive
demonstration. Remove that
specific directory after exiting if you no longer need its local observations.

## Add it to your profile and theme

Copy `CopilotPrompt.ps1` and `copilot.segment.json` into a stable local directory,
or keep a checkout and use absolute paths to those files. Do not depend on your
shell's current directory.

After your existing Oh My Posh initialization in `$PROFILE`, add:

```powershell
# Keep your existing Oh My Posh theme and initialization above these lines.
. 'C:\Users\YOUR_USER\prompt\CopilotPrompt.ps1'
Initialize-CopilotPrompt -InstallHook
```

Loading the script only defines functions. Initialization installs the hook
only when `-InstallHook` is specified. It composes an existing `Set-PoshContext`
hook rather than discarding it, and preserves the previous command's exit code.
Reinitialization stops only this integration's worker and avoids duplicate hooks.

Insert the object from `copilot.segment.json` into the desired prompt block's
`segments` array in your theme. It is a **segment fragment**, not a full theme.
Keep your other segments and adapt its foreground/projection colors to your
terminal palette. Reinitialize Oh My Posh after changing your theme.

Do not add an hour-long Oh My Posh segment cache: it would delay showing newly
refreshed data, failures, and billing rollover. This integration owns the API
cache.

For a different host or refresh interval:

```powershell
Initialize-CopilotPrompt -Hostname your-enterprise.ghe.com -RefreshMinutes 60 -InstallHook
```

Optional parameters are `-CacheDirectory`, `-RequestTimeoutSeconds` (default 15,
per request), and `-GhExecutable` (default `gh`, resolved once at initialization).
Refresh intervals may be 1-1440 minutes. Use a dedicated, user-owned cache
directory, not the repository or a shared/public directory.

If you manage your own hook, omit `-InstallHook` and call `Update-CopilotPrompt`
before rendering the prompt. It sets these process-local variables:

| Variable | Value |
|---|---|
| `COPILOT_SPEND` | Current formatted USD and percentage, or `unavailable` |
| `COPILOT_FORECAST` | Compact projected USD, or `?` |
| `COPILOT_FORECAST_STATE` | `green`, `yellow`, `red`, or `unknown` |
| `COPILOT_CONNECTION_STATE` | `connected`, `in_progress`, or `not_connected` |

`Disable-CopilotPrompt` stops this integration's job, removes its variables,
and restores the previous alias/hook if this integration still owns it. It
does not delete observations, change your theme, log out, or revoke credentials.
Remove the two profile lines and the segment to uninstall permanently.

## Authentication and troubleshooting

The worker uses `gh api --hostname <host>` and `gh`'s normal credential
resolution. It never runs `gh auth login`, requests scopes, rotates credentials,
or copies a token into its cache. `GH_TOKEN`/`GITHUB_TOKEN` take precedence over
stored credentials on github.com and ghe.com hosts; GitHub Enterprise Server
uses the corresponding enterprise environment variables.

Check access in the **same terminal** where you use the integration:

```powershell
gh api --hostname github.com copilot_internal/user --silent
```

No quota body is printed. A successful request exits with code 0. Do not assume
a generic `read:org` warning from `gh auth status` explains a Copilot failure.
There is no verified universal scope recipe for this internal endpoint. Access
can depend on token/application type, account entitlement, host support,
enterprise approval, or SSO policy. A token working on github.com is not proof
that it works on another host.

The worker verifies numeric account identity with `GET /user`, then reads
`GET /copilot_internal/user`. Safe diagnostics include the failed operation and
HTTP status, not raw API responses. Failures have a five-minute cooldown;
`Retry-After` and exhausted rate-limit reset deadlines can extend it. Resolve
access using your own normal `gh` authentication process; the integration
does not modify credentials.

## Caching, privacy, and performance

The default cache is `%LOCALAPPDATA%\GHCPSpendPrompt`, independent of the tray
app. Each credential context has a small JSON observation, a lock file, and a
short-lived refresh marker. An opaque SHA-256 fingerprint partitions hosts,
executables, refresh intervals, environment credentials, and `gh` configuration metadata. The
configuration file's contents are never read by this script.

Changes to environment credentials or `gh` account-selection metadata are
detected on the next disk check, normally within one second. Keyring changes
that do not update that metadata require reinitialization with a new dedicated
`-CacheDirectory`, or deletion of the old observations. A currently running
worker rejects results if its credential context changes during the request.

The prompt path reuses memory between disk checks. It never launches `gh` or
waits for a network request. Once due, a PowerShell background job performs
the refresh; two prompts/terminals cannot hold the same refresh lock. Other
terminals see a lease marker while the fetch is running. Abandoned markers
expire, and writes use atomic replacement so readers cannot see partial JSON.
The first background-job launch and periodic disk checks cost more than the
steady-state memory-only path; timings depend on the machine.

Successful observations expire after the configured interval or billing reset.
Stale, expired, failed, and unsupported observations are **unavailable, not
zero**. Unknown, zero, and unlimited allocations can show valid consumption
dollars, but do not invent a percentage or colored forecast. Warnings appear
when the diagnostic changes rather than on every prompt.

Cache files contain verified account IDs and consumption values, which are
private local data. They do not contain tokens, logins, raw HTTP payloads, or
invoice information. The script sends requests only to the GitHub host through
`gh`; there is no additional hosted service. Do not share cache files or commit
them. To clear local history, exit shells using the integration and delete
only its dedicated cache directory.

## Forecast limitations

USD consumption is GitHub's token-based `credits_used / 100`, **not an invoice**
or total GitHub spend. Premium-request billing is unsupported.

The forecast extrapolates average observed consumption over the current UTC
calendar month, using the source timestamp when supplied. It assumes that pace
continues. It requires at least 24 hours of the period; the first three days
remain particularly uncertain. A supplied reset must match the next first of
the month at midnight UTC. A stale/invalid observation, non-calendar reset, or
unknown allocation produces a neutral forecast instead of a misleading color.

## Offline verification

These tests use synthetic accounts, fixtures, time, and transport. They require
neither `gh` nor credentials, do not make network requests, and do not build the
tray apps:

```powershell
pwsh -NoProfile -File .\integrations\oh-my-posh\Test-CopilotPrompt.ps1
```

When Oh My Posh is installed, add `-Render` to check actual ANSI colors and
glyph output. `-Measure` reports cached update timings without imposing
machine-dependent pass/fail thresholds. The lightweight CI job runs the offline
tests for integration changes; documentation-only changes retain Markdown and
the aggregate Verification check.

The harness clears the synthetic exit codes used by its preservation assertions
only after successful completion, so the GitHub Actions PowerShell wrapper also
reports success. Assertion failures still terminate the test run.
The Windows harness also launches an isolated child through that wrapper to
regression-test its exit status without requiring the integration on other OSes.
