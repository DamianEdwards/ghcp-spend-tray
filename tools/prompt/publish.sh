#!/bin/bash
set -eu
root=$(cd -- "${BASH_SOURCE[0]%/*}/../.." && pwd -P)
cd -- "$root"
case "$(uname -s):$(uname -m)" in
    Darwin:arm64)
        rid=osx-arm64
        export MACOSX_DEPLOYMENT_TARGET=15.0
        source "$root/tools/macos/sdk.sh"
        ;;
    Linux:x86_64) rid=linux-x64 ;;
    *) printf '%s\n' 'Supported native hosts: Apple-silicon macOS and x64 glibc Linux.' >&2; exit 1 ;;
esac
[[ $# == 0 || ($# == 1 && $1 == --tests) ]] || { printf '%s\n' 'Usage: publish.sh [--tests]' >&2; exit 1; }
output="$root/artifacts/prompt/$rid"
dotnet publish src/GHCPSpendTray.Prompt/GHCPSpendTray.Prompt.csproj -c Release -r "$rid" \
    -p:IlcTreatWarningsAsErrors=true --nologo -v:q -o "$output"
if [[ ${1-} == --tests ]]; then
    dotnet publish tests/GHCPSpendTray.PromptTests/GHCPSpendTray.PromptTests.csproj -c Release -r "$rid" \
        -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true --nologo -v:q -o "$root/artifacts/tests/prompt/$rid"
fi
for file in CopilotPrompt.ps1 CopilotPrompt.bash CopilotPrompt.zsh copilot.segment.json demo.ps1 demo.bash README.md; do
    cp -- "$root/integrations/oh-my-posh/$file" "$output/$file"
done
cp -- "$root/LICENSE" "$output/LICENSE"
printf 'Standalone Native AOT bundle: %s\n' "$output"
