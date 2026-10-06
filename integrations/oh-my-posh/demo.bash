#!/bin/bash
set -eu
directory=$(cd -- "${BASH_SOURCE[0]%/*}" && pwd -P)
helper=${1-ghcp-spend-prompt}
helper=$(type -P "$helper") || { printf '%s\n' 'Install the native helper or pass its absolute path.' >&2; exit 1; }
temporary=$(mktemp -d)
theme="$temporary/theme.omp.json"
"$helper" demo-theme "$directory/copilot.segment.json" "$theme"
if [[ ${2-} == --live || ${2-} == --live-zsh ]]; then
    export GHCP_DEMO_HELPER=$helper GHCP_DEMO_DIR=$directory GHCP_DEMO_THEME=$theme GHCP_DEMO_CACHE="$temporary/cache"
    printf 'Isolated native helper demo; no profile changed. Directory: %s\n' "$temporary"
    if [[ $2 == --live-zsh ]]; then
        printf '%s\n' 'eval "$(oh-my-posh init zsh --config "$GHCP_DEMO_THEME")"' \
            'source "$GHCP_DEMO_DIR/CopilotPrompt.zsh"' \
            'initialize_copilot_prompt --helper-executable "$GHCP_DEMO_HELPER" --cache-dir "$GHCP_DEMO_CACHE" --install-hook' > "$temporary/.zshrc"
        export ZDOTDIR=$temporary
        exec zsh -d -i
    fi
    printf '%s\n' 'eval "$(oh-my-posh init bash --config "$GHCP_DEMO_THEME")"' \
        'source "$GHCP_DEMO_DIR/CopilotPrompt.bash"' \
        'initialize_copilot_prompt --helper-executable "$GHCP_DEMO_HELPER" --cache-dir "$GHCP_DEMO_CACHE" --install-hook' > "$temporary/rc.bash"
    exec bash --noprofile --rcfile "$temporary/rc.bash" -i
fi
trap 'rm -f -- "$theme"; rmdir -- "$temporary"' EXIT
for state in green yellow red in_progress not_connected; do
    output=$("$helper" demo "$state")
    IFS=$'\t' read -r schema COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE <<< "$output"
    export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    printf 'Synthetic %s\n' "$state"
    oh-my-posh print primary --config "$theme" --shell bash --escape=false
    printf '\n'
done
