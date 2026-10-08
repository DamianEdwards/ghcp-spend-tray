# Copilot spend in Oh My Posh

One standalone **.NET Native AOT helper** owns quota validation, forecasts,
credentials, HTTP, caching, locking, and background refresh. Thin PowerShell,
Bash, and zsh adapters put its output into a standard Oh My Posh text segment.
The tray app and an installed .NET runtime are **not** required.

```text
[Copilot icon] $247 (~8%) [colored chart icon] $3.3K
```

These are synthetic values. Current consumption comes first, followed by the
approximate allocation percentage and projected period dollars. Dollars and
percentages are rounded; thousands use `K` with at most one decimal.

| Unrounded projection versus allocation | Chart color |
|---|---|
| At most 80% | Green |
| Above 80%, through 100% | Yellow |
| Above 100% | Red |
| Unavailable | Neutral, with `?` |

The leading glyph is `nf-cod-copilot`, `nf-cod-copilot_in_progress` while a refresh
is running, or `nf-cod-copilot_not_connected` after a failure. The separator is
`nf-md-chart_timeline_variant_shimmer`. A recent Nerd Font is required in your
terminal. Ghostty needs no special integration beyond normal shell setup.

## Platforms and prerequisites

| Platform | Native bundle | Adapter |
|---|---|---|
| Windows x64 | `win-x64` | PowerShell 7.2+ |
| Windows ARM64 | `win-arm64` | PowerShell 7.2+ |
| Apple-silicon macOS 15/26 | `osx-arm64` | System Bash 3.2 or zsh 5.9 |
| x64 glibc Linux / WSL | `linux-x64` | Bash |

Mac Intel/universal and Linux ARM64/musl are not distributed. The Linux artifact
is built on Ubuntu 24.04; do not assume compatibility with older glibc or every
Linux distribution. Native .NET HTTP uses the OS TLS facilities: Linux needs
OpenSSL 3 and normal CA certificates. Native AOT does not mean a static libc or
TLS implementation.

Install [Oh My Posh](https://ohmyposh.dev/) and [GitHub CLI](https://cli.github.com/).
`gh` must already have a usable credential for the selected host. No jq, GNU
date/stat, flock, timeout, Homebrew Bash, Python, tray app, or .NET runtime is
needed to **use** the integration.

## Get the standalone bundle

The **Standalone Copilot prompt** CI job retains development bundles named
`copilot-prompt-<OS>-<architecture>`. Download the appropriate RID directory from
the PR/workflow artifact, or publish from this checkout:

```powershell
.\tools\prompt\publish.ps1 -RuntimeIdentifier win-x64
.\tools\prompt\publish.ps1 -RuntimeIdentifier win-arm64
```

```bash
bash tools/prompt/publish.sh
```

Publishing uses the SDK pinned by `global.json`. Windows builds need Visual
Studio C++ tools and the Windows SDK (including ARM64 cross tools for ARM64).
Linux builds need clang and zlib development headers; macOS builds need the
repository's stable Xcode/SDK toolchain and native Apple silicon. Build/publish
commands must run sequentially because their intermediates are shared.

Output is `artifacts/prompt/<RID>`. Copy the helper, adapter(s),
`copilot.segment.json`, and README into a dedicated, stable user-owned directory.
Keep them together and either add that directory to PATH or pass the helper's
absolute path. No administrator installation is necessary.

CI/local bundles are development artifacts, not signed/notarized public
releases. Do not bypass Windows/macOS trust or organization-policy warnings.
The helper is distributed independently of the Windows/macOS tray apps and
does not change their versioning or packages.

## Configure Oh My Posh

Insert the object in `copilot.segment.json` into the desired prompt block's
`segments` array. It is a segment fragment, not a complete theme. Keep your
other segments and adapt the colors to your palette.

Install the adapter **after** Oh My Posh initialization. Do not add a long
Oh My Posh segment cache: the helper already manages data freshness.

### PowerShell

In `$PROFILE`:

```powershell
oh-my-posh init pwsh --config 'C:\Users\YOUR_USER\theme.omp.json' | Invoke-Expression
. 'C:\Users\YOUR_USER\copilot-prompt\CopilotPrompt.ps1'
Initialize-CopilotPrompt -HelperExecutable 'C:\Users\YOUR_USER\copilot-prompt\ghcp-spend-prompt.exe' -InstallHook
```

### macOS zsh

In `.zshrc`:

```zsh
eval "$(oh-my-posh init zsh --config "$HOME/theme.omp.json")"
source "$HOME/.local/share/copilot-prompt/CopilotPrompt.zsh"
initialize_copilot_prompt --helper-executable "$HOME/.local/share/copilot-prompt/ghcp-spend-prompt" --install-hook
```

### Bash, including macOS system Bash and WSL

In `.bashrc`:

```bash
eval "$(oh-my-posh init bash --config "$HOME/theme.omp.json")"
source "$HOME/.local/share/copilot-prompt/CopilotPrompt.bash"
initialize_copilot_prompt --helper-executable "$HOME/.local/share/copilot-prompt/ghcp-spend-prompt" --install-hook
```

macOS terminals commonly start Bash as a login shell. Source `.bashrc` from
`.bash_profile` if your setup does not already do so:

```bash
[[ -f "$HOME/.bashrc" ]] && source "$HOME/.bashrc"
```

Adapters preserve existing `Set-PoshContext`/`set_poshcontext` hooks and command
exit status. Initialization is repeatable; disabling restores only a hook that
the adapter still owns. They do not replace unrelated `PS1`, `PROMPT_COMMAND`,
zsh hooks, profiles, or themes. Helper output is parsed as data, never evaluated.

## Options and protocol

The helper's prompt operation is:

```text
ghcp-spend-prompt prompt --hostname github.com --refresh-minutes 60 --request-timeout 15 --gh-executable gh
```

`--cache-dir` overrides the dedicated cache directory. Intervals are 1-1440
minutes and per-request timeouts 1-60 seconds. PowerShell exposes matching
`-Hostname`, `-RefreshMinutes`, `-RequestTimeoutSeconds`, `-GhExecutable`, and
`-CacheDirectory` parameters; Bash/zsh accept the native option spellings.

Stdout is exactly one line with five nonempty tab-separated fields:

```text
GHCP-SPEND/1<TAB>spend<TAB>forecast<TAB>forecast-state<TAB>connection-state
```

The adapters export `COPILOT_SPEND`, `COPILOT_FORECAST`,
`COPILOT_FORECAST_STATE` (`green/yellow/red/unknown`), and
`COPILOT_CONNECTION_STATE` (`connected/in_progress/not_connected`).
Safe diagnostics go to stderr, never into this data stream. The internal
`refresh` operation is for workers, not profile configuration.

## Cache, credentials, and background work

The foreground helper only reads local context metadata and validated cached
data, then launches a separate bounded worker if due. It never waits for
credential discovery or HTTP. There is no persistent daemon.

The worker uses host-appropriate environment-token precedence or captured,
timed-out `gh auth token --hostname <host>` output. It never logs in, requests
scopes, rotates tokens, or creates another credential store. Identity and quota
requests use shared Core .NET HTTP clients with redirect refusal, size limits,
validation, and rate-limit handling, not shell `gh api` parsing.

Windows defaults to `%LOCALAPPDATA%\GHCPSpendPrompt\native-v1`; Unix defaults to
`${XDG_CACHE_HOME:-$HOME/.cache}/GHCPSpendPrompt/native-v1`. Prompt caches are
independent of the tray app. An opaque fingerprint isolates host, executable
selection, interval, environment credentials, and `gh` configuration metadata.
No token or configuration-file contents are persisted. Keyring changes that
do not update configuration metadata need a new dedicated cache directory.

Cross-process exclusion uses BCL exclusive file sharing; no external locking
utility is needed. Contention recognizes the platform's sharing-violation code
(including macOS's distinct `EWOULDBLOCK` errno). The foreground tries the gate
without waiting; only the worker waits up to five seconds for the scheduler's
handoff. The native tests exercise competing processes and forced
worker termination on every CI OS. Refresh leases expire after a bounded
lifetime; a dead worker's OS file lock is released. Atomic private-file
replacement prevents partial JSON reads. Failure cooldowns (at least five
minutes) and server retry deadlines prevent network requests on every prompt.

Cached observations contain an account ID and consumption values: private local
data. Do not share or commit them. Unknown/unlimited allocations do not invent a
percentage or colored forecast. Stale/expired, failed, invalid and unsupported
observations are unavailable, never invented zero usage.

## Forecast and authentication limitations

Token-based credits are converted using the shared Core policy of 100 credits
per USD. This is consumption value, **not an invoice** or all GitHub spending.
Request-count billing is unsupported.

The shared forecast extrapolates average observed usage across a UTC calendar
month. It requires at least 24 hours of the period, a valid current observation,
a fresh source timestamp, and a reset matching the next UTC month boundary
when supplied. The first three days remain particularly uncertain.

An existing `gh` login is not proof that its token/application/account can read
the internal Copilot endpoint. There is no universal scope recipe. Check the
same shell/host explicitly:

```text
gh api --hostname github.com copilot_internal/user --silent
```

WSL credentials are separate from Windows Credential Manager. An explicitly
opt-in, bounded check performs the real background flow without printing
consumption values:

```text
ghcp-spend-prompt live-check --hostname github.com
```

It uses the dedicated native cache, not the repository. The demos and test
suites use synthetic accounts and quota fixtures; live evidence is separate.

## Demos, tests, and measurements

```powershell
pwsh -NoProfile -File .\integrations\oh-my-posh\demo.ps1 -HelperExecutable .\artifacts\prompt\win-x64\ghcp-spend-prompt.exe
```

```bash
bash integrations/oh-my-posh/demo.bash "$PWD/artifacts/prompt/linux-x64/ghcp-spend-prompt"
# Opt-in live isolated Bash or zsh, without editing profiles:
bash integrations/oh-my-posh/demo.bash /absolute/path/ghcp-spend-prompt --live
bash integrations/oh-my-posh/demo.bash /absolute/path/ghcp-spend-prompt --live-zsh
```

The helper generates synthetic presentations and the minimal theme from the
canonical segment. Add `-Live` to the PowerShell demo for an isolated live
session. Exit the demo shell to return to your normal shell.

Publish with `-Tests` (Windows) or `--tests` (Unix), then run the published harness
with `--helper <absolute-helper-path>`. It exercises domain rules, HTTP fixtures,
private/invalid/expired caches, identity/context isolation, exclusion, abandoned
workers, timeouts, cooldowns, rate limits and atomic writes. Adapter harnesses
take the helper and fixture-harness paths and optionally `-Render`/`--render`.
CI covers Windows x64/ARM64 publishing, Linux x64, and macOS arm64 with system
Bash 3.2/zsh; renderer binaries are version/digest pinned.

Representative development measurements (separate from Oh My Posh rendering):
Windows x64 first reads varied from 84-92 ms, warm medians were 30-51 ms and p95
varied from 48-67 ms; WSL Linux x64 first reads varied from 4-45 ms, warm medians
were 5-10 ms and p95 varied
from 6-43 ms, over 30 native processes per run. These are observations, not hardware-independent thresholds or
claims about a cold OS filesystem cache. macOS timings need its native CI/host
run. Native AOT process startup is intentionally measured before considering a
daemon.

## Upgrade and uninstall

This replaces the earlier shell-owned cache/backend. Install the helper and new
thin adapters together, update the helper path, and start a fresh shell rather
than mixing old/new function definitions. The shared segment and four variable
names are unchanged. `native-v1` does not import the legacy JSON/jq/TSV caches;
there is one fresh native observation per context.

Use `Disable-CopilotPrompt` or `disable_copilot_prompt` to remove this adapter's
hook and variables. Remove its profile lines and segment to uninstall. It does
not log out, revoke credentials or remove unrelated theme content. After
exiting shells, remove only the dedicated installation/cache directories if
you no longer want their private observations.
