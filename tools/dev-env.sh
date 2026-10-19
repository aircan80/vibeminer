#!/usr/bin/env bash
# Source this file to use optional repository-local SDKs and temporary storage.
_vibeminer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -d "$_vibeminer_root/.local/toolchains/node-v24.22.0-linux-x64/bin" ]]; then
  export PATH="$_vibeminer_root/.local/toolchains/node-v24.22.0-linux-x64/bin:$PATH"
fi
if [[ -x "$_vibeminer_root/.local/toolchains/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$_vibeminer_root/.local/toolchains/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
mkdir -p "$_vibeminer_root/.local/tmp" "$_vibeminer_root/.local/electron-cache"
export TMPDIR="${TMPDIR:-$_vibeminer_root/.local/tmp}"
export electron_config_cache="${electron_config_cache:-$_vibeminer_root/.local/electron-cache}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
unset _vibeminer_root
