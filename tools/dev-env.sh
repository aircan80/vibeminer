#!/usr/bin/env bash
# Source this file from bash or zsh to use optional repository-local SDKs and temporary storage.
# A git worktree without its own .local/toolchains uses the main checkout's
# toolchains and Electron download cache. TMPDIR stays per checkout.
if [ -z "${BASH_VERSION:-}" ] && [ -z "${ZSH_VERSION:-}" ]; then
  echo "tools/dev-env.sh: source this from bash or zsh; nothing was set." >&2
  return 1
fi
_vibeminer_dev_env() {
  # zsh: default options for this function only, and `%x` names the file that defined it.
  [ -z "${ZSH_VERSION:-}" ] || emulate -L zsh
  local root tools_root common shared file
  if [ -n "${ZSH_VERSION:-}" ]; then file="${(%):-%x}"; else file="${BASH_SOURCE[0]}"; fi
  # `cd -q` keeps zsh chpwd hooks from writing into the captured path; in bash the word vanishes.
  root="$(CDPATH='' cd ${ZSH_VERSION:+-q} -- "$(dirname -- "$file")/.." && pwd)" || return
  tools_root="$root"
  if [[ ! -d "$root/.local/toolchains" ]] && command -v git >/dev/null 2>&1; then
    common="$(unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR; git -C "$root" rev-parse --git-common-dir 2>/dev/null)" || common=""
    if [[ -n "$common" ]]; then
      [[ "$common" == /* ]] || common="$root/$common"
      shared="$(CDPATH='' cd ${ZSH_VERSION:+-q} -- "$common/.." 2>/dev/null && pwd)" || shared=""
      if [[ -n "$shared" && -d "$shared/.local/toolchains" ]]; then
        tools_root="$shared"
      fi
    fi
  fi

  local node_bin="$tools_root/.local/toolchains/node-v24.22.0-linux-x64/bin"
  if [[ -d "$node_bin" ]]; then
    case ":$PATH:" in *":$node_bin:"*) ;; *) export PATH="$node_bin:$PATH" ;; esac
  fi
  if [[ -x "$tools_root/.local/toolchains/dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$tools_root/.local/toolchains/dotnet"
    case ":$PATH:" in *":$DOTNET_ROOT:"*) ;; *) export PATH="$DOTNET_ROOT:$PATH" ;; esac
  fi

  if [[ -z "${TMPDIR:-}" ]]; then
    mkdir -p "$root/.local/tmp"
    export TMPDIR="$root/.local/tmp"
  fi
  if [[ -z "${electron_config_cache:-}" ]]; then
    mkdir -p "$tools_root/.local/electron-cache"
    export electron_config_cache="$tools_root/.local/electron-cache"
  fi
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1
}
_vibeminer_dev_env
unset -f _vibeminer_dev_env
