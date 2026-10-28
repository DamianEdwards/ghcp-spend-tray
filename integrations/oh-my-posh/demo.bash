#!/bin/bash
set -eu
directory=$(cd -- "${BASH_SOURCE[0]%/*}" && pwd -P)
source "$directory/CopilotPrompt.bash"
app=${1-/Applications/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray}
temporary=$(mktemp -d)
trap 'disable_copilot_prompt; rm -f -- "$temporary/theme.omp.json"; rmdir -- "$temporary"' EXIT
{ printf '{"version":4,"blocks":[{"type":"prompt","alignment":"left","segments":['
  cat "$directory/copilot.segment.json"; printf ']}]}\n'; } > "$temporary/theme.omp.json"
initialize_copilot_prompt --app-executable "$app"
update_copilot_prompt
oh-my-posh print primary --config "$temporary/theme.omp.json" --shell bash --escape=false
printf '\n'
