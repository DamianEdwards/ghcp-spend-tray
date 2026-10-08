# Helper output is data. This adapter does not own precmd, PS1 or refresh work.

initialize_copilot_prompt() {
    local helper=ghcp-spend-prompt install_hook=false option
    local -a arguments=()
    while (( $# )); do
        option=$1; shift
        case $option in
            --install-hook) install_hook=true ;;
            --helper-executable)
                (( $# )) || return 1
                helper=$1; shift ;;
            --hostname|--cache-dir|--refresh-minutes|--request-timeout|--gh-executable)
                (( $# )) || return 1
                arguments+=("$option" "$1"); shift ;;
            *) print -u2 -- "Unknown Copilot adapter option: $option"; return 1 ;;
        esac
    done
    helper=${commands[$helper]:-$helper}
    [[ -x $helper ]] || { print -u2 -- 'Install ghcp-spend-prompt or pass --helper-executable.'; return 1; }
    [[ ${_ghcp_initialized-false} != true ]] || disable_copilot_prompt
    typeset -g _ghcp_helper=$helper _ghcp_initialized=true _ghcp_hook_installed=false _ghcp_previous=''
    typeset -ga _ghcp_arguments=("${arguments[@]}")
    if [[ $install_hook == true ]]; then
        if (( $+functions[set_poshcontext] )); then
            _ghcp_previous=$functions[set_poshcontext]
            functions[_ghcp_previous_context]=$_ghcp_previous
        fi
        set_poshcontext() {
            local original_status=$?
            [[ -z $_ghcp_previous ]] || _ghcp_previous_context "$@"
            update_copilot_prompt
            return $original_status
        }
        typeset -g _ghcp_installed=$functions[set_poshcontext] _ghcp_hook_installed=true
    fi
}

update_copilot_prompt() {
    local output version spend forecast color connection extra
    if [[ ${_ghcp_initialized-false} == true ]] &&
        output=$("$_ghcp_helper" prompt "${_ghcp_arguments[@]}") &&
        IFS=$'\t' read -r version spend forecast color connection extra <<< "$output" &&
        [[ $version == GHCP-SPEND/1 && -n $spend && -n $forecast && -z $extra &&
        $output != *$'\n'* &&
        ($color == green || $color == yellow || $color == red || $color == unknown) &&
        ($connection == connected || $connection == in_progress || $connection == not_connected) ]]; then
        export COPILOT_SPEND=$spend COPILOT_FORECAST=$forecast
        export COPILOT_FORECAST_STATE=$color COPILOT_CONNECTION_STATE=$connection
    else
        export COPILOT_SPEND=unavailable COPILOT_FORECAST='?'
        export COPILOT_FORECAST_STATE=unknown COPILOT_CONNECTION_STATE=not_connected
        print -u2 -- 'Copilot adapter failed. Check the native helper and options.'
        return 1
    fi
}

disable_copilot_prompt() {
    [[ ${_ghcp_initialized-false} == true ]] || return 0
    if [[ $_ghcp_hook_installed == true && ${functions[set_poshcontext]-} == $_ghcp_installed ]]; then
        unfunction set_poshcontext
        [[ -z $_ghcp_previous ]] || functions[set_poshcontext]=$_ghcp_previous
    fi
    (( ! $+functions[_ghcp_previous_context] )) || unfunction _ghcp_previous_context
    unset COPILOT_SPEND COPILOT_FORECAST COPILOT_FORECAST_STATE COPILOT_CONNECTION_STATE
    _ghcp_initialized=false
}
