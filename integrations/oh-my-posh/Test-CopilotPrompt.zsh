#!/bin/zsh
set -eu
directory=${0:A:h}
helper=$1 fixture=$2
source "$directory/CopilotPrompt.zsh"
temporary=$(mktemp -d)
cleanup() {
    disable_copilot_prompt
    local file
    for file in "$temporary"/*(N); do [[ ! -f $file ]] || rm -f -- "$file"; done
    rmdir -- "$temporary"
}
trap cleanup EXIT
export GH_CONFIG_DIR=$temporary GH_TOKEN=synthetic-token
unset GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN
"$fixture" --prepare-cache "$temporary" "$helper"
checks=0
equal() {
    [[ $1 == $2 ]] || { print -u2 -- "$3: expected [$2], got [$1]"; exit 1; }
    checks=$((checks + 1))
}
previous_calls=0
set_poshcontext() { previous_calls=$((previous_calls + 1)); }
previous=$functions[set_poshcontext]
initialize_copilot_prompt --helper-executable "$helper" --gh-executable "$helper" --cache-dir "$temporary" --install-hook
if (exit 42); then exit 1
else
    if set_poshcontext; then original=0; else original=$?; fi
fi
equal "$original" 42 'Previous status preserved'
equal "$previous_calls" 1 'Existing hook composed'
equal "$COPILOT_SPEND" '$40 (~40%)' 'Published helper data'
equal "$COPILOT_CONNECTION_STATE" connected 'Connected state'
initialize_copilot_prompt --helper-executable "$helper" --gh-executable "$helper" --cache-dir "$temporary" --install-hook
set_poshcontext
equal "$previous_calls" 2 'Repeatable initialization'
disable_copilot_prompt
equal "$functions[set_poshcontext]" "$previous" 'Previous hook restored'
equal "${COPILOT_SPEND-unset}" unset 'Exports removed'
if [[ ${3-} == --render ]]; then
    "$helper" demo-theme "$directory/copilot.segment.json" "$temporary/theme.omp.json"
    for scenario in green yellow red in_progress not_connected; do
        output=$("$helper" demo "$scenario")
        IFS=$'\t' read -r schema COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE <<< "$output"
        export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
        rendered=$(oh-my-posh print primary --config "$temporary/theme.omp.json" --shell zsh --escape=false)
        case $scenario in
            green) expected='166;227;161' ;; yellow) expected='249;226;175' ;; red) expected='243;139;168' ;;
            in_progress) expected='' ;; not_connected) expected='' ;;
        esac
        equal "$([[ $rendered == *"$expected"* ]] && print yes || print no)" yes 'Actual color/connection glyph'
        equal "$([[ $rendered == *'󱖶'* ]] && print yes || print no)" yes 'Actual chart glyph'
    done
fi
print -- "PASS: $checks zsh adapter assertions against published helper (zsh $ZSH_VERSION)."
