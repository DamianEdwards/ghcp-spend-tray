#!/bin/bash
set -eu
root=$(cd -- "${BASH_SOURCE[0]%/*}/../.." && pwd -P)
cd -- "$root"
app=$1
dotnet build tests/GHCPSpendTray.PromptTests/GHCPSpendTray.PromptTests.csproj -c Release --nologo -v:q
fixture="$root/tests/GHCPSpendTray.PromptTests/bin/Release/net10.0/GHCPSpendTray.PromptTests"
"$fixture" --app "$app"
/bin/bash integrations/oh-my-posh/Test-CopilotPrompt.bash "$app" "$fixture" "${2-}"
/bin/zsh integrations/oh-my-posh/Test-CopilotPrompt.zsh "$app" "$fixture" "${2-}"
