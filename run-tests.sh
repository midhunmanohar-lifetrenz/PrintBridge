#!/usr/bin/env bash
# Runs the whole automated suite. Needs the .NET 10 SDK (~/.dotnet is searched too).
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")" && dotnet test tests/LtzPrintBridge.Tests "$@"
