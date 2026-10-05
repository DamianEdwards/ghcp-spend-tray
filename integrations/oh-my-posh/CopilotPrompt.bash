#!/usr/bin/env bash

__copilot_now() { printf '%(%s)T' -1; }

__copilot_config_path() {
    if [[ -n ${GH_CONFIG_DIR-} ]]; then printf '%s/hosts.yml' "$GH_CONFIG_DIR"
    elif [[ -n ${XDG_CONFIG_HOME-} ]]; then printf '%s/gh/hosts.yml' "$XDG_CONFIG_HOME"
    else printf '%s/.config/gh/hosts.yml' "$HOME"; fi
}

__copilot_config_stamp() {
    if [[ -f $1 ]]; then stat -Lc '%y:%z:%s:%i' -- "$1"
    else printf missing; fi
}

__copilot_token() {
    if [[ $1 == github.com || $1 == *.ghe.com ]]; then
        printf '%s' "${GH_TOKEN:-${GITHUB_TOKEN-}}"
    else printf '%s' "${GH_ENTERPRISE_TOKEN:-${GITHUB_ENTERPRISE_TOKEN-}}"; fi
}

__copilot_key() {
    local digest
    digest=$(printf '%s\0' "$1" "$2" "$3" "$4" "$5" "$6" | sha256sum) || return 1
    printf '%s' "${digest%% *}"
}

__copilot_epoch() {
    local value=$1
    if [[ $value == null ]]; then printf '%s' -1
    elif [[ $value =~ ^[0-9]{1,12}$ && $((10#$value)) -le 253402300799 ]]; then
        printf '%s' "$((10#$value))"
    elif [[ $value =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})?)?$ ]]; then
        date -u --date="$value" +%s
    else return 1; fi
}

__copilot_publish() {
    local path=$1 document=$2 temporary
    temporary=$(mktemp "$path.XXXXXX") || return 1
    if ! printf '%s\n' "$document" > "$temporary" || ! mv -f -- "$temporary" "$path"; then
        rm -f -- "$temporary"
        return 1
    fi
}

__copilot_request() {
    local endpoint=$1 result code first name value epoch
    _copilot_retry=$((_copilot_worker_now + 300))
    if result=$(env -u GH_DEBUG -u DEBUG -u GH_FORCE_TTY -u CLICOLOR_FORCE \
        GH_PROMPT_DISABLED=1 GH_NO_UPDATE_NOTIFIER=1 NO_COLOR=1 \
        timeout --kill-after=2 "${_copilot_timeout}s" "$_copilot_gh" api \
        --hostname "$_copilot_hostname" --method GET --include "$endpoint" 2>/dev/null); then
        code=0
    else code=$?; fi
    if [[ ${#result} -gt 1048576 ]]; then
        _copilot_diagnostic="GitHub response exceeded the size limit."
        return 1
    fi
    result=${result//$'\r'/}
    first=${result%%$'\n'*}
    if [[ ! $first =~ ^HTTP/[^[:space:]]+[[:space:]]+([0-9]{3}) || $result != *$'\n\n'* ]]; then
        _copilot_diagnostic="gh request failed or timed out (exit $code). Check gh authentication."
        return 1
    fi
    local status=${BASH_REMATCH[1]} headers=${result%%$'\n\n'*} remaining= reset=
    while IFS= read -r first; do
        [[ $first == *:* ]] || continue
        name=${first%%:*}; name=${name,,}
        value=${first#*:}; value=${value# }
        case $name in
            retry-after)
                if [[ $value =~ ^[0-9]{1,8}$ ]]; then epoch=$((_copilot_worker_now + 10#$value))
                elif epoch=$(date -u --date="$value" +%s 2>/dev/null); then :
                else _copilot_diagnostic="GitHub returned an invalid retry deadline."; return 1; fi
                ((epoch > _copilot_retry)) && _copilot_retry=$epoch
                ;;
            x-ratelimit-remaining) remaining=$value ;;
            x-ratelimit-reset) reset=$value ;;
        esac
    done <<< "$headers"
    if [[ $remaining == 0 && $reset =~ ^[0-9]{1,12}$ ]]; then
        epoch=$((10#$reset))
        ((epoch > _copilot_retry)) && _copilot_retry=$epoch
    fi
    if [[ $status != 200 || $code != 0 ]]; then
        _copilot_diagnostic="GitHub $endpoint request failed (HTTP $status). Check authentication and endpoint access."
        return 1
    fi
    _copilot_body=${result#*$'\n\n'}
}

__copilot_worker() {
    set +x
    umask 077
    _copilot_hostname=$1; _copilot_cache=$2; _copilot_interval=$3
    _copilot_gh=$4; _copilot_timeout=$5
    local key=$6 filter=$7 path="$2/$6.bash.json" lease="$2/$6.refresh"
    local descriptor config stamp token current next document account reset observed start end month
    _copilot_worker_now=$(__copilot_now)
    mkdir -p -- "$_copilot_cache" || { printf '%s\n' 'Copilot: cannot create cache directory.' >&2; return 1; }
    exec {descriptor}> "$path.lock" || return 1
    if ! flock -n "$descriptor"; then exec {descriptor}>&-; return 0; fi
    if [[ -f $path ]]; then
        if next=$(jq -er --arg mode schedule --arg hostname "$_copilot_hostname" --arg key "$key" -f "$filter" "$path" 2>/dev/null); then
            if [[ $next =~ ^[0-9]{1,12}$ ]] && ((next > _copilot_worker_now)); then
                local repaired_view
                repaired_view=$(jq -r --arg mode view -f "$filter" "$path") || return 1
                __copilot_publish "$path.view" "$repaired_view" || return 1
                return 0
            fi
        else printf '%s\n' 'Copilot: invalid cache; refreshing a validated observation.' >&2; fi
    fi
    _copilot_worker_lease=$lease
    trap 'rm -f -- "$_copilot_worker_lease"' EXIT
    trap 'exit 143' TERM INT
    __copilot_publish "$lease" "$((_copilot_worker_now + 2 * _copilot_timeout + 30))" || return 1
    _copilot_retry=$((_copilot_worker_now + 300))
    _copilot_diagnostic="Unsupported billing or invalid quota data. Current spend is unavailable."
    config=$(__copilot_config_path)
    stamp=$(__copilot_config_stamp "$config") || return 1
    token=$(__copilot_token "$_copilot_hostname")
    current=$(__copilot_key "$_copilot_hostname" "$_copilot_gh" "$_copilot_interval" "$config" "$stamp" "$token") || return 1
    document=
    if [[ $current != "$key" ]]; then
        _copilot_diagnostic="Credential context changed before refresh."
    elif __copilot_request user && account=$(jq -er '.id | select(type == "number" and . > 0 and . == floor and . <= 9007199254740991) | tostring' <<< "$_copilot_body" 2>/dev/null); then
        if __copilot_request copilot_internal/user; then
            _copilot_diagnostic="Unsupported billing or invalid quota data. Current spend is unavailable."
            if jq -e --arg mode validate -f "$filter" <<< "$_copilot_body" >/dev/null 2>&1; then
                _copilot_worker_now=$(__copilot_now)
                reset=$(jq -r '.quota_reset_date_utc // .quota_reset_date // null' <<< "$_copilot_body")
                observed=$(jq -r '.quota_snapshots.premium_interactions.timestamp_utc // null' <<< "$_copilot_body")
                if reset=$(__copilot_epoch "$reset") && observed=$(__copilot_epoch "$observed") &&
                    ((reset < 0 || reset > _copilot_worker_now)) && ((observed <= _copilot_worker_now)); then
                    month=$(date -u --date="@$_copilot_worker_now" +%Y-%m-01)
                    start=$(date -u --date="$month" +%s)
                    end=$(date -u --date="$month +1 month" +%s)
                    document=$(jq -c --arg mode snapshot --arg hostname "$_copilot_hostname" \
                        --arg key "$key" --arg account "$account" --argjson now "$_copilot_worker_now" \
                        --argjson interval "$_copilot_interval" --argjson reset "$reset" \
                        --argjson observed "$observed" --argjson start "$start" --argjson end "$end" \
                        -f "$filter" <<< "$_copilot_body" 2>/dev/null) || document=
                fi
            fi
        fi
    else
        [[ ${_copilot_diagnostic-} == "Unsupported billing or invalid quota data. Current spend is unavailable." ]] &&
            _copilot_diagnostic="GitHub returned an invalid account identity."
    fi
    stamp=$(__copilot_config_stamp "$config") || return 1
    current=$(__copilot_key "$_copilot_hostname" "$_copilot_gh" "$_copilot_interval" "$config" "$stamp" "$token") || return 1
    if [[ $current != "$key" ]]; then document=; _copilot_diagnostic="Credential context changed during refresh."; fi
    if [[ -z $document ]]; then
        document=$(jq -nc --arg mode failure --arg hostname "$_copilot_hostname" --arg key "$key" \
            --argjson now "$_copilot_worker_now" --argjson retry "$_copilot_retry" \
            --arg diagnostic "$_copilot_diagnostic" -f "$filter") || return 1
    fi
    __copilot_publish "$path" "$document" || return 1
    local view
    view=$(jq -r --arg mode view -f "$filter" <<< "$document") || return 1
    __copilot_publish "$path.view" "$view" || return 1
}

__copilot_job_running() {
    [[ -n ${_copilot_pid-} ]] || return 1
    local running
    running=$(jobs -pr)
    [[ $'\n'$running$'\n' == *$'\n'"$_copilot_pid"$'\n'* ]]
}

__copilot_update() {
    local now stamp token key version context status fetched fresh forecast_until next text forecast state diagnostic
    now=$(__copilot_now)
    if ((now != _copilot_read_at)); then
        stamp=$(__copilot_config_stamp "$_copilot_config") || return 1
        token=$(__copilot_token "$_copilot_hostname")
        if [[ $stamp != "$_copilot_stamp" || $token != "$_copilot_token_value" ]]; then
            key=$(__copilot_key "$_copilot_hostname" "$_copilot_gh" "$_copilot_interval" "$_copilot_config" "$stamp" "$token") || return 1
            _copilot_key_value=$key; _copilot_stamp=$stamp; _copilot_token_value=$token
            _copilot_retry_at=0
        fi
        _copilot_read_at=$now
        _copilot_next=0; _copilot_fetched=0; _copilot_fresh=0; _copilot_forecast_until=0
        _copilot_text=unavailable; _copilot_forecast='?'; _copilot_forecast_state=unknown
        _copilot_status=unavailable; _copilot_diagnostic_value=-
        local path="$_copilot_cache/$_copilot_key_value.bash.json"
        if [[ -f $path.view ]]; then
            if IFS=$'\t' read -r version context status fetched fresh forecast_until next text forecast state diagnostic < "$path.view" &&
                [[ $version == 1 && $context == "$_copilot_key_value" && $status =~ ^(fresh|unavailable)$ &&
                $fetched =~ ^[0-9]{1,12}$ && $fresh =~ ^[0-9]{1,12}$ && $forecast_until =~ ^[0-9]{1,12}$ &&
                $next =~ ^[0-9]{1,12}$ && $state =~ ^(green|yellow|red|unknown)$ &&
                -n $text && -n $forecast && -n $diagnostic && ${#diagnostic} -le 256 ]]; then
                _copilot_next=$next; _copilot_fetched=$fetched; _copilot_fresh=$fresh
                _copilot_forecast_until=$forecast_until; _copilot_status=$status
                _copilot_text=$text; _copilot_forecast=$forecast; _copilot_forecast_state=$state
                _copilot_diagnostic_value=$diagnostic
                if [[ $status == fresh ]] && ((fetched >= _copilot_job_started)); then _copilot_job_failed=false; fi
            else _copilot_diagnostic_value="Copilot cache is invalid or unreadable."; fi
        fi
        _copilot_lease_until=0
        if [[ -f $_copilot_cache/$_copilot_key_value.refresh ]]; then
            IFS= read -r _copilot_lease_until < "$_copilot_cache/$_copilot_key_value.refresh" || _copilot_lease_until=0
            [[ $_copilot_lease_until =~ ^[0-9]{1,12}$ ]] || _copilot_lease_until=0
        fi
    fi
    local refreshing=false
    if __copilot_job_running || ((now < _copilot_lease_until)); then refreshing=true
    elif [[ -n ${_copilot_pid-} ]]; then
        if ! wait "$_copilot_pid"; then
            _copilot_diagnostic_value="Copilot background refresh failed. Check cache access and dependencies."
            _copilot_status=unavailable
            _copilot_job_failed=true
        else _copilot_job_failed=false; fi
        _copilot_pid=
        _copilot_read_at=0
    fi
    if [[ $refreshing == false ]] && ((now >= _copilot_next && now >= _copilot_retry_at)); then
        _copilot_retry_at=$((now + 300))
        bash "$_copilot_script" --worker "$_copilot_hostname" "$_copilot_cache" "$_copilot_interval" \
            "$_copilot_gh" "$_copilot_timeout" "$_copilot_key_value" "$_copilot_filter" >/dev/null 2>&1 &
        _copilot_pid=$!
        _copilot_job_started=$now
        refreshing=true
    fi
    COPILOT_SPEND=unavailable; COPILOT_FORECAST='?'; COPILOT_FORECAST_STATE=unknown
    if [[ $_copilot_status == fresh && $_copilot_job_failed == false ]] && ((now >= _copilot_fetched && now < _copilot_fresh)); then
        COPILOT_SPEND=$_copilot_text
        if ((now < _copilot_forecast_until)); then COPILOT_FORECAST=$_copilot_forecast; COPILOT_FORECAST_STATE=$_copilot_forecast_state; fi
    fi
    if [[ $refreshing == true ]]; then COPILOT_CONNECTION_STATE=in_progress
    elif [[ $_copilot_status == fresh && $_copilot_job_failed == false ]]; then COPILOT_CONNECTION_STATE=connected
    else COPILOT_CONNECTION_STATE=not_connected; fi
    export COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    if [[ $_copilot_diagnostic_value != - && $_copilot_diagnostic_value != "$_copilot_last_diagnostic" ]]; then
        printf '%s\n' "$_copilot_diagnostic_value" >&2
    fi
    _copilot_last_diagnostic=$_copilot_diagnostic_value
}

update_copilot_prompt() {
    local trace=false result=0
    [[ $- != *x* ]] || { trace=true; set +x; }
    if [[ ${_copilot_initialized-false} == true ]]; then __copilot_update || result=$?
    else printf '%s\n' 'Run initialize_copilot_prompt first.' >&2; result=1; fi
    if ((result != 0)); then
        export COPILOT_SPEND=unavailable COPILOT_FORECAST='?' COPILOT_FORECAST_STATE=unknown COPILOT_CONNECTION_STATE=not_connected
        printf '%s\n' 'Copilot prompt update failed. Check cache access and dependencies.' >&2
    fi
    [[ $trace == false ]] || set -x
    return "$result"
}

initialize_copilot_prompt() {
    local hostname=github.com cache="${XDG_CACHE_HOME:-$HOME/.cache}/GHCPSpendPrompt"
    local minutes=60 gh_executable=gh request_timeout=15 install_hook=false argument directory
    while (($#)); do
        argument=$1; shift
        case $argument in
            --hostname|--cache-dir|--refresh-minutes|--gh-executable|--request-timeout)
                (($#)) || { printf 'Missing value for %s\n' "$argument" >&2; return 1; }
                case $argument in
                    --hostname) hostname=$1 ;; --cache-dir) cache=$1 ;;
                    --refresh-minutes) minutes=$1 ;; --gh-executable) gh_executable=$1 ;;
                    --request-timeout) request_timeout=$1 ;;
                esac
                shift ;;
            --install-hook) install_hook=true ;;
            *) printf 'Unknown Copilot option: %s\n' "$argument" >&2; return 1 ;;
        esac
    done
    [[ (${BASH_VERSINFO[0]} -gt 4 || (${BASH_VERSINFO[0]} -eq 4 && ${BASH_VERSINFO[1]} -ge 4)) &&
        $hostname =~ ^[a-zA-Z0-9]+([.-][a-zA-Z0-9]+)*$ &&
        $minutes =~ ^[1-9][0-9]{0,3}$ && $minutes -le 1440 &&
        $request_timeout =~ ^[1-9][0-9]?$ && $request_timeout -le 60 ]] ||
        { printf '%s\n' 'Invalid Copilot hostname/interval/timeout or unsupported Bash version.' >&2; return 1; }
    for argument in jq date flock stat sha256sum timeout mktemp; do
        command -v "$argument" >/dev/null || { printf 'Copilot requires %s.\n' "$argument" >&2; return 1; }
    done
    date -u --date=@0 +%s >/dev/null 2>&1 || { printf '%s\n' 'Copilot requires GNU date.' >&2; return 1; }
    gh_executable=$(type -P -- "$gh_executable") || { printf '%s\n' 'Copilot requires gh.' >&2; return 1; }
    [[ $cache == /* && $cache != / && ${cache%/} != "${HOME%/}" ]] ||
        { printf '%s\n' 'Use an absolute, dedicated Copilot cache directory.' >&2; return 1; }
    directory=${BASH_SOURCE[0]%/*}
    directory=$(cd -- "$directory" && pwd -P) || return 1
    [[ ${_copilot_initialized-false} != true ]] || disable_copilot_prompt
    _copilot_hostname=${hostname,,}; _copilot_cache=${cache%/}; _copilot_interval=$((minutes * 60))
    _copilot_gh=$gh_executable; _copilot_timeout=$request_timeout
    _copilot_script="$directory/CopilotPrompt.bash"; _copilot_filter="$directory/copilot-prompt.jq"
    _copilot_config=$(__copilot_config_path)
    _copilot_stamp=$(__copilot_config_stamp "$_copilot_config") || return 1
    local trace=false
    [[ $- != *x* ]] || { trace=true; set +x; }
    _copilot_token_value=$(__copilot_token "$_copilot_hostname")
    _copilot_key_value=$(__copilot_key "$_copilot_hostname" "$_copilot_gh" "$_copilot_interval" "$_copilot_config" "$_copilot_stamp" "$_copilot_token_value") || return 1
    [[ $trace == false ]] || set -x
    (umask 077; mkdir -p -- "$_copilot_cache") || return 1
    _copilot_initialized=true; _copilot_read_at=0; _copilot_retry_at=0
    _copilot_lease_until=0; _copilot_pid=; _copilot_last_diagnostic=-
    _copilot_job_started=0; _copilot_job_failed=false
    _copilot_hook_installed=false; _copilot_previous_hook=
    if [[ $install_hook == true ]]; then
        if declare -F set_poshcontext >/dev/null; then
            _copilot_previous_hook=$(declare -f set_poshcontext)
            eval "${_copilot_previous_hook/set_poshcontext/__copilot_previous_hook}"
        fi
        set_poshcontext() {
            local original_status=$?
            [[ -z $_copilot_previous_hook ]] || __copilot_previous_hook "$@"
            update_copilot_prompt
            return "$original_status"
        }
        _copilot_installed_hook=$(declare -f set_poshcontext)
        _copilot_hook_installed=true
    fi
}

disable_copilot_prompt() {
    [[ ${_copilot_initialized-false} == true ]] || return 0
    if __copilot_job_running; then kill "$_copilot_pid"; fi
    if [[ -n ${_copilot_pid-} ]]; then wait "$_copilot_pid" || :; fi
    if [[ $_copilot_hook_installed == true && $(declare -f set_poshcontext) == "$_copilot_installed_hook" ]]; then
        unset -f set_poshcontext
        [[ -z $_copilot_previous_hook ]] || eval "$_copilot_previous_hook"
    fi
    unset -f __copilot_previous_hook
    unset COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    unset _copilot_token_value _copilot_pid
    _copilot_initialized=false
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    if [[ ${1-} == --worker && $# == 8 ]]; then shift; __copilot_worker "$@"
    else printf '%s\n' 'Source CopilotPrompt.bash, then call initialize_copilot_prompt.' >&2; exit 1; fi
fi
