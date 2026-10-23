# App-backed shell usage and Oh My Posh

The **installed GHCPSpendTray app executable** exposes read-only command-line
queries. It uses the same accounts, custom budgets, selection, refresh interval,
and forecasts as the app and is updated with the app. There is no separate
helper, `gh` credential, network loop, cache scheduler, or executable to maintain.

Native Windows and Apple-silicon macOS are supported. Linux/WSL is not supported
by this app-backed integration.

## Query usage from any shell

On Windows, installation registers the `ghcp-spend-tray.exe` execution alias:

```powershell
ghcp-spend-tray.exe usage --format json
ghcp-spend-tray.exe usage --account github.com:123 --format json
ghcp-spend-tray.exe prompt
```

On macOS, invoke the app's executable directly, not `open -a`:

```bash
"/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" usage --format json
"/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" prompt
```

These commands print to stdout and exit before UI startup, account credentials,
telemetry initialization, or single-instance activation. They do not open the
flyout, start the monitoring app, refresh the API, or modify app settings/data.
They work while the tray/menu-bar app is stopped.

Run the updated app normally at least once to publish its saved usage.
Without a published snapshot, the command reports unavailable data rather than
starting the app. Keep the app running if you want regular refreshes.

### Selection, budgets, and forecasts

The default total uses the accounts selected in the tray/menu-bar settings.
`--account <immutable-host:id-key>` selects one account explicitly, even if it
is excluded from the tray. JSON lists account keys so callers do not have to
guess them.

A custom budget overrides the service allocation, including unlimited accounts
with a finite custom budget. Forecasts use the app's estimate setting and
already-computed daily-report-based estimate when available. They are not
recomputed with a separate shell billing model.

The forecast chart uses the same **unrounded** bands as the app:

| Projection versus effective budget | Color |
|---|---|
| Below 95% | Green |
| At least 95%, below 100% | Yellow |
| At least 100% | Red |
| Missing forecast or effective budget | Neutral |

Disabling **Show estimated period consumption** in account settings also
disables that account's shell forecast. A combined forecast requires all selected
accounts to have fresh data and estimates; missing/failed observations are not
invented as zero. Total percentages require a meaningful effective budget for
every included account so their numerator/denominator refer to the same accounts.

### JSON contract

`usage` defaults to JSON; `--format json` makes the choice explicit. The
versioned object includes:

- `version`, `queriedAtUtc`, `publishedAtUtc`, `pollMinutes`, and `status`.
- `isComplete`, `isRefreshing`, `includedAccounts`, `selectedAccounts`, fresh `consumptionUsd`,
  `effectiveBudgetUsd`, `percentConsumed`, `estimatedConsumptionUsd`, and
  `forecastState`.
- `accounts`: keys/names/hosts, tray selection, explicit freshness/current-period/
  refresh status, observation/source/reset timestamps, observed consumption and
  service allocation, custom/effective budget, current percentage and forecast.

Per-account observed dollars remain available as **last-known** data when stale,
failed or from a previous period, with explicit `status`, `isFresh` and
`isCurrentPeriod`. They do not contribute to fresh aggregate dollars. Inspect
these flags rather than treating any numeric field as a current observation.
Zero is a valid value only when actually observed.

Queries reevaluate age and billing rollover at invocation time, including when
the app is not running. Old snapshots cannot remain fresh indefinitely. A saved
refresh flag expires after 95 seconds, so stopping during a fetch does not leave
a permanent in-progress icon. No snapshot means `status: "unavailable"`; no
selected accounts means `status: "no-accounts"`.

Exit codes: `0` is a valid response (including unavailable/partial data), `1` is
unreadable/invalid saved data, and `2` is an invalid option/account selection.
Diagnostics go to stderr, not JSON or prompt output.

### Compact prompt contract

`prompt` returns one line of five nonempty tab-separated fields:

```text
GHCP-SPEND/1<TAB>spend<TAB>forecast<TAB>forecast-state<TAB>connection-state
```

Example synthetic rendering:

```text
[Copilot icon] $247 (~8%) [colored chart icon] $3.3K
```

Dollars and percentages are rounded; thousands use `K` with at most one decimal.
`!` before the dollars identifies a partial total. Stale/expired data appears as
`unavailable`, and an unavailable forecast is `?`.

The leading glyph is `nf-cod-copilot`, `nf-cod-copilot_in_progress`, or
`nf-cod-copilot_unavailable` (U+EC42). Here "connected" means the selected saved app
observations are fresh and complete, not that a query opened a network connection.
The projection separator is `nf-md-chart_timeline_variant_shimmer`.

## Oh My Posh setup

The app executable embeds the adapters and canonical segment. There is no
separate script download or versioned package path to maintain. Keep the
initialization line in your profile so each new shell loads the adapter from the
currently installed app.

Export the canonical theme fragment with:

```powershell
ghcp-spend-tray.exe integration oh-my-posh segment
```

```bash
"/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" integration oh-my-posh segment
```

Copy that JSON object into your existing theme's appropriate `segments`
array. The command does not edit your theme. Choose a recent Nerd Font that contains
the Copilot and chart glyphs. Ghostty needs no special app integration.

### Windows PowerShell 7.2+

After your existing Oh My Posh initialization in `$PROFILE`, add:

```powershell
ghcp-spend-tray.exe init pwsh | Invoke-Expression
```

PowerShell native pipelines pass stdout one line at a time. `init pwsh` therefore
emits one complete expression that decodes the embedded UTF-8 adapter from
Base64 before evaluating it, rather than streaming partial function definitions.
Base64 is transport encoding, not encryption; the canonical adapter source
remains `CopilotPrompt.ps1`. Bash/zsh initialization remains ordinary shell text.

This profile form uses the installed **console execution alias**, not a raw
GUI-subsystem development executable. Development scripts can still source the
canonical adapter from the repository and call
`Initialize-CopilotPrompt -AppExecutable <path> -DataDirectory <path>`; the adapter
uses explicit stdout pipes for that executable. Normal installations use their
package's own data directory. Windows Store and
GitHub packages have distinct identities and data; the registered execution
alias follows the installation selected in Windows **App execution aliases**.

### macOS zsh

After Oh My Posh initialization in `.zshrc`:

```zsh
eval "$("/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" init zsh)"
```

### macOS Bash 3.2+

After Oh My Posh initialization in `.bashrc`:

```bash
eval "$("/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" init bash)"
```

For login shells, source `.bashrc` from `.bash_profile` if not already configured:

```bash
[[ -f "$HOME/.bashrc" ]] && source "$HOME/.bashrc"
```

Use your app's actual path if it is installed somewhere else; emitted code
quotes that path safely. `init pwsh`, `init bash` and `init zsh` accept
`--account` and `--data-dir`, and include those options in the initialization
call. For example:

```powershell
ghcp-spend-tray.exe init pwsh --account github.com:123 | Invoke-Expression
```

`--data-dir` is an explicit absolute, read-only development/test override, not
a second profile or a place to put tokens. Initialization itself does not read
usage or create directories.

The adapters compose existing `Set-PoshContext`/`set_poshcontext` hooks and
preserve the previous command's exit status. Loading a script only defines
functions; installation is explicit and repeatable. Output is parsed as data,
never evaluated as shell code. Only `init` intentionally emits trusted
executable code from the installed app, just as Oh My Posh initialization does.
`usage`, `prompt`, and segment JSON remain data-only. Adapters do not overwrite `PS1`, `PROMPT_COMMAND`,
zsh hooks, profiles or theme configuration.

## Updates, privacy, and migration

The query code ships inside the app: Microsoft Store/app updates update the
query implementation and embedded adapters too. Theme fragments are user
configuration and are not silently rewritten during updates.

The running app atomically publishes `shell-usage-v1.json` in its normal data
directory after dashboard/settings changes and refresh status updates. The
file contains local account identities and consumption values but no access/
refresh tokens, OAuth configuration, enterprise credentials or raw error bodies.
It is private app data: do not share it or commit it to source control.

This replaces the old standalone `ghcp-spend-prompt` binary and its `native-v1`
cache. Those are no longer built/distributed or read. No `gh`, jq, GNU tools,
Python, separate credential grant, or installed .NET runtime is needed for
normal query use. Remove the old profile initialization and source the updated
adapter in a fresh shell; the segment's four environment variables and TSV
schema name are unchanged, but forecast bands now match the app's 95/100% rules.
Previously sourced/copied shim files can be replaced by the single `init` profile
line; new packages no longer need loose `ShellIntegration` scripts.

`Disable-CopilotPrompt` / `disable_copilot_prompt` removes the adapter's variables
and restores only the hook it still owns. Remove profile lines and the segment
to uninstall shell integration. This does not log out, change budgets, stop the
app, or remove account history.

## Verification and demonstrations

`demo.ps1` / `demo.bash` display your saved app prompt without replacing your
profile. They need the installed app and Oh My Posh, and perform no live fetch.

Tests generate isolated **synthetic app snapshots**, invoke the actual app
executable through redirected pipes, and check budget overrides, account
selection, forecast bands, stopped-app freshness, JSON/TSV, exit status and
absence of UI activation. The app controller publication is covered separately
by shared application tests. Synthetic render tests exercise all glyph states
and ANSI forecast colors. Initialization tests evaluate app-emitted code with
quoted paths and options, compose/restore existing hooks, and check repeatable
initialization. Packaged Windows smoke also exercises the exact profile pipeline.

```powershell
.\tools\prompt\verify.ps1 -AppExecutable .\artifacts\publish\win-x64\GHCPSpendTray.exe -Render
```

```bash
bash tools/prompt/verify.sh "$PWD/artifacts/macos/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" --render
```

The dedicated CI job covers native Windows/macOS app invocations. Windows
packaged smoke also invokes the installed execution alias before UI activation.
Native Apple-silicon macOS execution requires macOS; running shell-version tests
under WSL is not evidence of native macOS behavior.
