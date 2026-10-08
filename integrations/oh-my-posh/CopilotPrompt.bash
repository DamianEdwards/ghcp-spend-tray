#!/bin/bash
# Compatible with macOS system Bash 3.2. Helper output is data, never shell code.

initialize_copilot_prompt() {
    local helper=ghcp-spend-prompt install_hook=false option
    local arguments=(prompt)
    while [ "$#" -gt 0 ]; do
        option=$1; shift
        case $option in
            --install-hook) install_hook=true ;;
            --helper-executable)
                [ "$#" -gt 0 ] || return 1
                helper=$1; shift ;;
            --hostname|--cache-dir|--refresh-minutes|--request-timeout|--gh-executable)
                [ "$#" -gt 0 ] || return 1
                arguments+=("$option" "$1"); shift ;;
            *) printf 'Unknown Copilot adapter option: %s\n' "$option" >&2; return 1 ;;
        esac
    done
    helper=$(type -P "$helper") || { printf '%s\n' 'Install ghcp-spend-prompt or pass --helper-executable.' >&2; return 1; }
    [ "${_ghcp_initialized-false}" != true ] || disable_copilot_prompt
    _ghcp_helper=$helper; _ghcp_arguments=("${arguments[@]}")
    _ghcp_initialized=true; _ghcp_hook_installed=false; _ghcp_previous=
    if [ "$install_hook" = true ]; then
        if declare -F set_poshcontext >/dev/null; then
            _ghcp_previous=$(declare -f set_poshcontext)
            eval "${_ghcp_previous/set_poshcontext/_ghcp_previous_context}"
        fi
        set_poshcontext() {
            local original_status=$?
            [ -z "$_ghcp_previous" ] || _ghcp_previous_context "$@"
            update_copilot_prompt
            return "$original_status"
        }
        _ghcp_installed=$(declare -f set_poshcontext)
        _ghcp_hook_installed=true
    fi
}

update_copilot_prompt() {
    local output version spend forecast color connection extra
    if [ "${_ghcp_initialized-false}" = true ] &&
        output=$("$_ghcp_helper" "${_ghcp_arguments[@]}") &&
        IFS=$'\t' read -r version spend forecast color connection extra <<< "$output" &&
        [ "$version" = GHCP-SPEND/1 ] && [ -n "$spend" ] && [ -n "$forecast" ] &&
        [[ $color = green || $color = yellow || $color = red || $color = unknown ]] &&
        [[ $connection = connected || $connection = in_progress || $connection = not_connected ]] &&
        [ -z "$extra" ] && [[ $output != *$'\n'* ]]; then
        export COPILOT_SPEND="$spend" COPILOT_FORECAST="$forecast"
        export COPILOT_FORECAST_STATE="$color" COPILOT_CONNECTION_STATE="$connection"
    else
        export COPILOT_SPEND=unavailable COPILOT_FORECAST='?'
        export COPILOT_FORECAST_STATE=unknown COPILOT_CONNECTION_STATE=not_connected
        printf '%s\n' 'Copilot adapter failed. Check the native helper and options.' >&2
        return 1
    fi
}

disable_copilot_prompt() {
    [ "${_ghcp_initialized-false}" = true ] || return 0
    if [ "$_ghcp_hook_installed" = true ] && [ "$(declare -f set_poshcontext)" = "$_ghcp_installed" ]; then
        unset -f set_poshcontext
        [ -z "$_ghcp_previous" ] || eval "$_ghcp_previous"
    fi
    unset -f _ghcp_previous_context
    unset COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    _ghcp_initialized=false
}
