#!/usr/bin/env bash
set -euo pipefail
directory=$(cd -- "${BASH_SOURCE[0]%/*}" && pwd -P)
source "$directory/CopilotPrompt.bash"
command -v jq >/dev/null || { printf '%s\n' 'The Bash demo requires jq.' >&2; exit 1; }
command -v oh-my-posh >/dev/null || { printf '%s\n' 'The Bash demo requires native Oh My Posh.' >&2; exit 1; }
umask 077
if [[ ${1-} == --live ]]; then
    base="${XDG_CACHE_HOME:-$HOME/.cache}/GHCPSpendPrompt"
    mkdir -p -- "$base"
    temporary=$(mktemp -d "$base/demo-XXXXXX")
else
    [[ $# == 0 ]] || { printf '%s\n' 'Usage: bash demo.bash [--live]' >&2; exit 1; }
    temporary=$(mktemp -d)
    trap 'rm -f -- "$temporary/theme.omp.json"; rmdir -- "$temporary"' EXIT
fi
theme="$temporary/theme.omp.json"
jq -n --slurpfile segment "$directory/copilot.segment.json" '{
  version: 4, final_space: true, blocks: [{
    type: "prompt", alignment: "left", segments: [
      {type: "path", style: "plain", foreground: "#89B4FA",
        template: "{{ .Path }} ", options: {style: "folder"}},
      $segment[0],
      {type: "text", style: "plain", foreground: "#89B4FA", template: "> "}
    ]
  }]
}' > "$theme"
if [[ ${1-} == --live ]]; then
    export COPILOT_DEMO_DIRECTORY=$directory COPILOT_DEMO_THEME=$theme COPILOT_DEMO_CACHE="$temporary/cache"
    printf '%s\n' 'Isolated native Bash demo; your .bashrc is not changed.'
    printf 'Demo theme/cache directory: %s\n' "$temporary"
    printf '%s\n' 'Press Enter after a few seconds for the first background result.'
    printf '%s\n' 'Exit returns to your original shell; disable_copilot_prompt removes the hook.'
    printf '%s\n' 'eval "$(oh-my-posh init bash --config "$COPILOT_DEMO_THEME")"' \
        'source "$COPILOT_DEMO_DIRECTORY/CopilotPrompt.bash"' \
        'initialize_copilot_prompt --cache-dir "$COPILOT_DEMO_CACHE" --install-hook' > "$temporary/rc.bash"
    exec bash --noprofile --rcfile "$temporary/rc.bash" -i
fi
for example in 'green 6000' 'yellow 9000' 'red 12000'; do
    read -r label estimate <<< "$example"
    quota=$(jq -nc --arg mode example --argjson estimate "$estimate" -f "$directory/copilot-prompt.jq")
    record=$(jq -c --arg mode snapshot --argjson now 1792152000 --argjson start 1790812800 \
        --argjson end 1793491200 --argjson interval 3600 -f "$directory/copilot-prompt.jq" <<< "$quota")
    COPILOT_SPEND=$(jq -r .text <<< "$record")
    COPILOT_FORECAST=$(jq -r .forecast <<< "$record")
    COPILOT_FORECAST_STATE=$(jq -r .forecastState <<< "$record")
    COPILOT_CONNECTION_STATE=connected
    export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    printf 'Synthetic %s:\n' "$label"
    oh-my-posh print primary --config "$theme" --shell bash --escape=false
    printf '\n'
done
for connection in in_progress not_connected; do
    export COPILOT_CONNECTION_STATE=$connection COPILOT_SPEND=unavailable COPILOT_FORECAST='?' COPILOT_FORECAST_STATE=unknown
    printf 'Synthetic %s:\n' "$connection"
    oh-my-posh print primary --config "$theme" --shell bash --escape=false
    printf '\n'
done
