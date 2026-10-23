#!/bin/zsh
set -eu
directory=${0:A:h}
helper=$1 fixture=$2
source "$directory/CopilotPrompt.zsh"
temporary_root=$(mktemp -d)
temporary="$temporary_root/state ' \$()"
mkdir -- "$temporary"
cleanup() {
    disable_copilot_prompt
    local file
    for file in "$temporary"/*(N); do [[ ! -f $file ]] || rm -f -- "$file"; done
    rmdir -- "$temporary"
    rmdir -- "$temporary_root"
}
trap cleanup EXIT
"$fixture" --prepare-cache "$temporary"
checks=0
equal() {
    [[ $1 == $2 ]] || { print -u2 -- "$3: expected [$2], got [$1]"; exit 1; }
    checks=$((checks + 1))
}
previous_calls=0
set_poshcontext() { previous_calls=$((previous_calls + 1)); }
previous=$functions[set_poshcontext]
eval "$("$helper" init zsh --data-dir "$temporary")"
if (exit 42); then exit 1
else
    if set_poshcontext; then original=0; else original=$?; fi
fi
equal "$original" 42 'Previous status preserved'
equal "$previous_calls" 1 'Existing hook composed'
equal "$COPILOT_SPEND" '$40 (~80%)' 'App custom budget data'
equal "$COPILOT_CONNECTION_STATE" connected 'Connected state'
eval "$("$helper" init zsh --data-dir "$temporary")"
set_poshcontext
equal "$previous_calls" 2 'Repeatable initialization'
disable_copilot_prompt
equal "$functions[set_poshcontext]" "$previous" 'Previous hook restored'
equal "${COPILOT_SPEND-unset}" unset 'Exports removed'
if [[ ${3-} == --render ]]; then
    { printf '{"version":4,"blocks":[{"type":"prompt","alignment":"left","segments":['
      cat "$directory/copilot.segment.json"; printf ']}]}\n'; } > "$temporary/theme.omp.json"
    for scenario in green yellow red in_progress not_connected; do
        "$fixture" --prepare-cache "$temporary" "$scenario"
        output=$("$helper" prompt --data-dir "$temporary")
        IFS=$'\t' read -r schema COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE <<< "$output"
        export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
        rendered=$(oh-my-posh print primary --config "$temporary/theme.omp.json" --shell zsh --escape=false)
        case $scenario in
            green) expected='166;227;161' ;; yellow) expected='249;226;175' ;; red) expected='243;139;168' ;;
            in_progress) expected='' ;; not_connected) expected='' ;;
        esac
        equal "$([[ $rendered == *"$expected"* ]] && print yes || print no)" yes 'Actual color/connection glyph'
        equal "$([[ $rendered == *'󱖶'* ]] && print yes || print no)" yes 'Actual chart glyph'
    done
fi
print -- "PASS: $checks zsh adapter assertions against app usage (zsh $ZSH_VERSION)."
