#!/bin/bash

if [[ -z "${SDKROOT:-}" ]]; then
    sdk_version="$(xcrun --show-sdk-version)"
    # Preview CLT can omit SwiftUI's macro plugin; use the installed stable SDK.
    if [[ "${sdk_version%%.*}" -ge 27 && "$(xcode-select -p)" == */CommandLineTools ]]; then
        export SDKROOT
        SDKROOT="$(xcode-select -p)/SDKs/MacOSX26.sdk"
        if [[ ! -d "$SDKROOT" ]]; then
            echo "Select a stable Xcode toolchain or set SDKROOT to a stable macOS SDK." >&2
            exit 1
        fi
        echo "Using stable macOS SDK: $SDKROOT"
    fi
fi
