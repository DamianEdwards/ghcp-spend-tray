#!/bin/bash
set -eu
directory=$(cd -- "${BASH_SOURCE[0]%/*}" && pwd -P)
helper=$1 fixture=$2
source "$directory/CopilotPrompt.bash"
temporary=$(mktemp -d)
cleanup() {
    disable_copilot_prompt
    for file in "$temporary"/*; do [ ! -f "$file" ] || rm -f -- "$file"; done
    rmdir -- "$temporary"
}
trap cleanup EXIT
export GH_CONFIG_DIR="$temporary" GH_TOKEN=synthetic-token
unset GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN
"$fixture" --prepare-cache "$temporary" "$helper"
checks=0
equal() {
    [ "$1" = "$2" ] || { printf '%s: expected [%s], got [%s]\n' "$3" "$2" "$1" >&2; exit 1; }
    checks=$((checks + 1))
}
previous_calls=0
set_poshcontext() { previous_calls=$((previous_calls + 1)); }
previous=$(declare -f set_poshcontext)
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
equal "$(declare -f set_poshcontext)" "$previous" 'Existing hook restored'
equal "${COPILOT_SPEND-unset}" unset 'Exports removed'
# Data from a configured helper must never be evaluated as assignments/commands.
cat > "$temporary/data-helper" <<'FIXTURE'
#!/bin/bash
printf 'GHCP-SPEND/1\t$(touch SHOULD_NOT_EXIST)\t?\tunknown\tconnected\n'
FIXTURE
chmod 700 "$temporary/data-helper"
initialize_copilot_prompt --helper-executable "$temporary/data-helper"
update_copilot_prompt
equal "$COPILOT_SPEND" '$(touch SHOULD_NOT_EXIST)' 'Data-only parsing'
equal "$([ -e SHOULD_NOT_EXIST ] && printf yes || printf no)" no 'No command evaluation'
disable_copilot_prompt
if [ "${3-}" = --render ]; then
    "$helper" demo-theme "$directory/copilot.segment.json" "$temporary/theme.omp.json"
    for scenario in green yellow red in_progress not_connected; do
        output=$("$helper" demo "$scenario")
        IFS=$'\t' read -r schema COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE <<< "$output"
        export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
        rendered=$(oh-my-posh print primary --config "$temporary/theme.omp.json" --shell bash --escape=false)
        case $scenario in
            green) expected='166;227;161' ;; yellow) expected='249;226;175' ;; red) expected='243;139;168' ;;
            in_progress) expected=$'\uEC4C' ;; not_connected) expected=$'\uEC3E' ;;
        esac
        # Bash 3.2 lacks \u expansion: use the literal UTF-8 glyph from its protocol-independent segment.
        case $scenario in in_progress) expected='' ;; not_connected) expected='' ;; esac
        equal "$([[ $rendered = *"$expected"* ]] && printf yes || printf no)" yes 'Actual color/connection glyph'
        equal "$([[ $rendered = *'󱖶'* ]] && printf yes || printf no)" yes 'Actual chart glyph'
        equal "$([[ $rendered = *"$COPILOT_SPEND"* ]] && printf yes || printf no)" yes 'Actual native spend'
    done
fi
printf 'PASS: %s Bash adapter assertions against published helper (Bash %s).\n' "$checks" "$BASH_VERSION"
