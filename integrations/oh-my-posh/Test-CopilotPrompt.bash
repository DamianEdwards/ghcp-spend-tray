#!/usr/bin/env bash
set -euo pipefail
[[ $# == 0 || ($# == 1 && $1 == --render) ]] || { printf '%s\n' 'Usage: Test-CopilotPrompt.bash [--render]' >&2; exit 1; }
render=${1-}
directory=$(cd -- "${BASH_SOURCE[0]%/*}" && pwd -P)
source "$directory/CopilotPrompt.bash"
checks=0
assert_equal() {
    [[ $1 == "$2" ]] || { printf '%s: expected [%s], got [%s]\n' "$3" "$2" "$1" >&2; exit 1; }
    checks=$((checks + 1))
}
temporary=$(mktemp -d)
trap 'disable_copilot_prompt; rm -f -- "$temporary/"*; rmdir -- "$temporary"' EXIT
export GH_CONFIG_DIR="$temporary" GH_TOKEN=synthetic-token GH_ENTERPRISE_TOKEN=synthetic-enterprise-token
export COPILOT_FAKE_CALLS="$temporary/calls" COPILOT_FAKE_CASE=success
reset=$(date -u --date="$(date -u +%Y-%m-01) +1 month" +%Y-%m-%d)
export COPILOT_FAKE_RESET=$reset
cat > "$temporary/fake-gh" <<'MOCK'
#!/usr/bin/env bash
set -eu
endpoint=${!#}
printf '%s\n' "$endpoint" >> "$COPILOT_FAKE_CALLS"
if [[ $COPILOT_FAKE_CASE == unauthorized ]]; then
    printf 'HTTP/2.0 401 Unauthorized\n\n{"message":"SYNTHETIC-PRIVATE-ERROR"}\n'
    exit 1
fi
if [[ $endpoint == user ]]; then printf 'HTTP/2.0 200 OK\n\n{"id":123}\n'; exit 0; fi
case $COPILOT_FAKE_CASE in
    limited) printf 'HTTP/2.0 429 Too Many Requests\nRetry-After: 3600\n\n{}\n'; exit 1 ;;
    slow) sleep 0.5 ;;
    invalid) printf 'HTTP/2.0 200 OK\n\n{}\n'; exit 0 ;;
esac
printf 'HTTP/2.0 200 OK\n\n{"quota_reset_date":"%s","quota_snapshots":{"premium_interactions":{"token_based_billing":true,"has_quota":true,"unlimited":false,"credits_used":4000,"entitlement":10000}}}\n' "$COPILOT_FAKE_RESET"
MOCK
chmod 700 "$temporary/fake-gh"
for script in CopilotPrompt.bash Test-CopilotPrompt.bash; do bash -n "$directory/$script"; done
assert_equal "${_copilot_initialized-false}" false 'Sourcing does not initialize'
if initialize_copilot_prompt --hostname 'https://invalid' --gh-executable "$temporary/fake-gh" 2>/dev/null; then
    printf '%s\n' 'Invalid hostname accepted.' >&2; exit 1
fi
if initialize_copilot_prompt --cache-dir / --gh-executable "$temporary/fake-gh" 2>/dev/null; then
    printf '%s\n' 'Cache root accepted.' >&2; exit 1
fi

now=1792152000 start=1790812800 end=1793491200
quota='{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"has_quota":true,"unlimited":false,"credits_used":4000,"entitlement":10000}}}'
snapshot() {
    jq -c --arg mode snapshot --arg hostname github.com --arg key synthetic --arg account 123 \
        --argjson now "$now" --argjson start "$start" --argjson end "$end" --argjson observed -1 \
        --argjson interval 3600 --argjson reset "$end" -f "$directory/copilot-prompt.jq"
}
for pair in '0 green' '79.99 green' '80 green' '80.01 yellow' '100 yellow' '100.01 red'; do
    read -r estimate expected <<< "$pair"
    input=$(jq --argjson estimate "$estimate" '.quota_snapshots.premium_interactions.credits_used = $estimate * 50' <<< "$quota")
    assert_equal "$(snapshot <<< "$input" | jq -r .forecastState)" "$expected" "Unrounded threshold $estimate"
done
record=$(snapshot <<< "$quota")
assert_equal "$(jq -r .text <<< "$record")" '$40 (~40%)' 'Spend-first layout'
assert_equal "$(jq -r .forecast <<< "$record")" '$80' 'UTC month projection'
for pair in '0.49 $0' '0.5 $1' '246.55 $247' '999.5 $1K' '1000 $1K' '1050 $1.1K' '4426 $4.4K' '10000 $10K'; do
    read -r dollars expected <<< "$pair"
    input=$(jq --argjson dollars "$dollars" '.quota_snapshots.premium_interactions.credits_used = $dollars * 100' <<< "$quota")
    actual=$(snapshot <<< "$input" | jq -r .text)
    assert_equal "${actual%% (*}" "$expected" "Compact USD $dollars"
done
for invalid in '{}' '[]' 'null' '{"quota_snapshots":{"premium_interactions":{"token_based_billing":false}}}'; do
    if jq -e --arg mode validate -f "$directory/copilot-prompt.jq" <<< "$invalid" >/dev/null 2>&1; then
        printf '%s\n' 'Invalid quota accepted.' >&2; exit 1
    fi
    checks=$((checks + 1))
done
unknown=$(jq '.quota_snapshots.premium_interactions.entitlement = null' <<< "$quota" | snapshot)
assert_equal "$(jq -r .text <<< "$unknown")" '$40' 'Unknown allocation retains dollars'
assert_equal "$(jq -r .forecastState <<< "$unknown")" unknown 'Unknown allocation has no color'
unlimited=$(jq '.quota_snapshots.premium_interactions.unlimited = true' <<< "$quota" | snapshot)
assert_equal "$(jq -r .forecastState <<< "$unlimited")" unknown 'Unlimited allocation has no color'
first_day=$(jq -c --arg mode snapshot --argjson now "$((start + 43200))" --argjson start "$start" \
    --argjson end "$end" --argjson interval 3600 -f "$directory/copilot-prompt.jq" <<< "$quota")
assert_equal "$(jq -r .forecast <<< "$first_day")" '?' 'First 24 hours have no forecast'
stale=$(jq -c --arg mode snapshot --argjson now "$now" --argjson observed "$((now - 3600))" \
    --argjson start "$start" --argjson end "$end" --argjson interval 3600 -f "$directory/copilot-prompt.jq" <<< "$quota")
assert_equal "$(jq -r .forecast <<< "$stale")" '?' 'Stale source has no forecast'
assert_equal "$(__copilot_epoch 2026-10-16T12:00:00)" "$now" 'Naive source timestamp is UTC'
assert_equal "$(__copilot_epoch 2026-10-16T05:00:00-07:00)" "$now" 'Offset timestamp preserves UTC'
assert_equal "$(__copilot_epoch "$now")" "$now" 'Epoch timestamp'
if __copilot_epoch 'tomorrow' >/dev/null 2>&1; then printf '%s\n' 'Relative API timestamp accepted.' >&2; exit 1; fi

initialize_copilot_prompt --cache-dir "$temporary" --gh-executable "$temporary/fake-gh"
key=$_copilot_key_value
path="$temporary/$key.bash.json"
__copilot_worker_args=("$directory/CopilotPrompt.bash" --worker github.com "$temporary" 3600 "$temporary/fake-gh" 2 "$key" "$directory/copilot-prompt.jq")
bash "${__copilot_worker_args[@]}"
assert_equal "$(jq -r .status "$path")" fresh 'Native worker succeeds'
assert_equal "$(jq -r .accountId "$path")" 123 'Verified identity persisted'
assert_equal "$(wc -l < "$COPILOT_FAKE_CALLS")" 2 'Identity and quota fetched'
assert_equal "$(stat -c %a "$path")" 600 'Private snapshot permissions'
assert_equal "$([[ -e $temporary/$key.refresh ]] && printf yes || printf no)" no 'Lease removed on completion'
bash "${__copilot_worker_args[@]}"
assert_equal "$(wc -l < "$COPILOT_FAKE_CALLS")" 2 'Fresh cache does not call gh'
rm -f -- "$path.view"
bash "${__copilot_worker_args[@]}"
assert_equal "$(wc -l < "$COPILOT_FAKE_CALLS")" 2 'Missing derived view does not refetch'
assert_equal "$([[ -f $path.view ]] && printf yes || printf no)" yes 'Missing derived view is repaired'
update_copilot_prompt
assert_equal "$COPILOT_SPEND" '$40 (~40%)' 'Foreground consumes cached view'
assert_equal "$COPILOT_CONNECTION_STATE" connected 'Connected glyph state'
assert_equal "${_copilot_pid-unset}" '' 'Fresh prompt launches no process'

printf '%s\n' "$(( $(__copilot_now) + 30 ))" > "$temporary/$key.refresh"
_copilot_read_at=0
update_copilot_prompt
assert_equal "$COPILOT_CONNECTION_STATE" in_progress 'Other terminal lease changes icon'
assert_equal "${_copilot_pid-unset}" '' 'Other terminal lease prevents duplicate worker'
rm -f -- "$temporary/$key.refresh"
force_expire() {
    jq '.nextAttemptEpoch = 0' "$path" > "$temporary/replacement"
    mv -f -- "$temporary/replacement" "$path"
    jq -r --arg mode view -f "$directory/copilot-prompt.jq" "$path" > "$path.view"
}
export COPILOT_FAKE_CASE=unauthorized
force_expire
bash "${__copilot_worker_args[@]}"
assert_equal "$(jq -r .status "$path")" unavailable 'Authentication failure is persisted'
assert_equal "$(jq -r '.diagnostic | contains("401")' "$path")" true 'HTTP status diagnostic'
assert_equal "$(jq -r '.diagnostic | contains("SYNTHETIC-PRIVATE")' "$path")" false 'Raw API response not logged'
previous_count=$(wc -l < "$COPILOT_FAKE_CALLS")
bash "${__copilot_worker_args[@]}"
assert_equal "$(wc -l < "$COPILOT_FAKE_CALLS")" "$previous_count" 'Failure cooldown avoids API requests'
_copilot_read_at=0
update_copilot_prompt 2>/dev/null
assert_equal "$COPILOT_CONNECTION_STATE" not_connected 'Failure glyph state'
assert_equal "$COPILOT_SPEND" unavailable 'Failure is not zero'

export COPILOT_FAKE_CASE=limited
force_expire
before=$(__copilot_now)
bash "${__copilot_worker_args[@]}"
next=$(jq -r .nextAttemptEpoch "$path")
assert_equal "$((next >= before + 3600))" 1 'Retry-After persisted'
force_expire
exec {lock}> "$path.lock"
flock -n "$lock"
previous_count=$(wc -l < "$COPILOT_FAKE_CALLS")
bash "${__copilot_worker_args[@]}"
assert_equal "$(wc -l < "$COPILOT_FAKE_CALLS")" "$previous_count" 'Cross-process lock excludes API calls'
exec {lock}>&-

export COPILOT_FAKE_CASE=slow
_copilot_read_at=0; _copilot_retry_at=0
update_copilot_prompt 2>/dev/null
assert_equal "$COPILOT_CONNECTION_STATE" in_progress 'Own worker changes progress icon'
own_pid=$_copilot_pid
update_copilot_prompt 2>/dev/null
assert_equal "$_copilot_pid" "$own_pid" 'Repeated prompt reuses worker'
wait "$own_pid"
_copilot_pid=; _copilot_read_at=0
update_copilot_prompt
assert_equal "$COPILOT_CONNECTION_STATE" connected 'Successful background refresh restores icon'
old_key=$_copilot_key_value
export GH_TOKEN=another-synthetic-token
_copilot_read_at=0
update_copilot_prompt
assert_equal "$([[ $old_key != "$_copilot_key_value" ]] && printf yes || printf no)" yes 'Credentials isolate cache'
wait "$_copilot_pid"
_copilot_pid=; _copilot_read_at=0
update_copilot_prompt
disable_copilot_prompt

previous_calls=0
set_poshcontext() { previous_calls=$((previous_calls + 1)); }
previous_definition=$(declare -f set_poshcontext)
initialize_copilot_prompt --cache-dir "$temporary" --gh-executable "$temporary/fake-gh" --install-hook
if (exit 42); then exit 1
else
    if set_poshcontext; then status=0; else status=$?; fi
fi
assert_equal "$status" 42 'Hook preserves previous exit status'
assert_equal "$previous_calls" 1 'Existing hook is composed'
initialize_copilot_prompt --cache-dir "$temporary" --gh-executable "$temporary/fake-gh" --install-hook
set_poshcontext
assert_equal "$previous_calls" 2 'Reinitialization does not recursively wrap'
disable_copilot_prompt
assert_equal "$(declare -f set_poshcontext)" "$previous_definition" 'Previous hook is restored'
assert_equal "${COPILOT_SPEND-unset}" unset 'Disable clears exports'

if [[ $render == --render ]]; then
    jq -n --slurpfile segment "$directory/copilot.segment.json" \
        '{version:4,blocks:[{type:"prompt",alignment:"left",segments:[$segment[0]]}]}' > "$temporary/render.omp.json"
    export COPILOT_SPEND='$247 (~8%)' COPILOT_FORECAST='$3.3K' COPILOT_CONNECTION_STATE=connected
    printf -v connected_icon '\uEC1E'
    printf -v progress_icon '\uEC4C'
    printf -v disconnected_icon '\uEC3E'
    printf -v chart_icon '\U000F15B6'
    for pair in 'green 166;227;161' 'yellow 249;226;175' 'red 243;139;168'; do
        read -r state color <<< "$pair"
        export COPILOT_FORECAST_STATE=$state
        output=$(oh-my-posh print primary --config "$temporary/render.omp.json" --shell bash --escape=false)
        assert_equal "$([[ $output == *"$color"* ]] && printf yes || printf no)" yes 'Rendered forecast color'
        assert_equal "$([[ $output == *"$connected_icon"* && $output == *"$chart_icon"* ]] && printf yes || printf no)" yes 'Rendered Copilot/chart glyphs'
        assert_equal "$([[ $output == *'$247 (~8%)'* && $output == *'$3.3K'* ]] && printf yes || printf no)" yes 'Rendered compact spend'
    done
    for connection in in_progress not_connected; do
        export COPILOT_CONNECTION_STATE=$connection
        expected=$progress_icon
        [[ $connection != not_connected ]] || expected=$disconnected_icon
        output=$(oh-my-posh print primary --config "$temporary/render.omp.json" --shell bash --escape=false)
        assert_equal "$([[ $output == *"$expected"* ]] && printf yes || printf no)" yes 'Rendered connection state'
    done
fi

initialize_copilot_prompt --cache-dir "$temporary" --gh-executable "$temporary/fake-gh"
update_copilot_prompt
before=$(date +%s%N)
for ((i=0; i<200; i++)); do update_copilot_prompt; done
after=$(date +%s%N)
printf 'Cached Bash update mean: %s us (200 calls, rendering excluded).\n' "$(((after - before) / 200000))"
printf 'PASS: %s offline Bash assertions.\n' "$checks"
