#!/usr/bin/env bash
# Set up a Fedora machine to develop VibeMiner. Run it from anywhere; it is safe to run again.
# It checks each requirement and installs only what is missing:
#   - Fedora packages: git, gh, tmux, the .NET runtime libraries and Electron's runtime libraries (e2e fonts come from the dejavu-fonts-ttf npm package)
#   - the .NET SDK pinned in global.json, into .local/toolchains/dotnet
#   - Node and npm pinned in .nvmrc and package.json, into .local/toolchains/node-v<version>-linux-x64
#   - npm packages and the locked NuGet restore
# The toolchains live in the checkout (ignored by git), and tools/dev-env.sh picks them up.
#
# Usage: tools/setup-fedora.sh [--dry-run] [--no-deps]
#   --dry-run  only report what is missing; change nothing
#   --no-deps  skip npm ci and the NuGet restore
set -euo pipefail

dry_run=false
deps=true
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=true ;;
    --no-deps) deps=false ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "setup-fedora: unknown option $arg" >&2; exit 2 ;;
  esac
done

root="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
toolchains="$root/.local/toolchains"
missing=0

say() { printf '%s\n' "$*"; }
ok() { printf '  ok       %s\n' "$*"; }
need() { printf '  missing  %s\n' "$*"; missing=$((missing + 1)); }
run() { if $dry_run; then printf '  would run: %s\n' "$*"; else "$@"; fi; }

if [[ ! -r /etc/fedora-release ]]; then
  echo "setup-fedora: this script is for Fedora (no /etc/fedora-release)." >&2
  exit 1
fi
[[ "$(uname -m)" == x86_64 ]] || { echo "setup-fedora: only x86_64 is supported (the pinned Node download is linux-x64)." >&2; exit 1; }

# Versions come from the repo, so the script never drifts from the pins.
dotnet_version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9.]*\)".*/\1/p' "$root/global.json" | head -1)"
node_version="$(tr -d '[:space:]v' < "$root/.nvmrc")"
npm_version="$(sed -n 's/.*"packageManager"[[:space:]]*:[[:space:]]*"npm@\([0-9.]*\)".*/\1/p' "$root/package.json" | head -1)"
[[ -n "$dotnet_version" && -n "$node_version" ]] || { echo "setup-fedora: couldn't read the pinned versions from global.json and .nvmrc." >&2; exit 1; }

say "Fedora packages"
packages=(git gh tmux curl tar xz
  libicu krb5-libs openssl-libs zlib libstdc++
  gtk3 nss alsa-lib libXScrnSaver libxkbfile at-spi2-atk libdrm mesa-libgbm fontconfig)
to_install=()
for p in "${packages[@]}"; do
  if rpm -q "$p" >/dev/null 2>&1; then ok "$p"; else need "$p"; to_install+=("$p"); fi
done
if ((${#to_install[@]})); then
  run sudo dnf install -y "${to_install[@]}"
fi

say ".NET SDK $dotnet_version"
dotnet="$toolchains/dotnet/dotnet"
if [[ -x "$dotnet" ]] && "$dotnet" --list-sdks 2>/dev/null | grep -q "^$dotnet_version "; then
  ok "$toolchains/dotnet"
else
  need ".NET SDK $dotnet_version in $toolchains/dotnet"
  if ! $dry_run; then
    mkdir -p "$toolchains"
    installer="$(mktemp)"
    trap 'rm -f "$installer"' EXIT
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --version "$dotnet_version" --install-dir "$toolchains/dotnet" --no-path
  else
    printf '  would run: dotnet-install.sh --version %s --install-dir %s\n' "$dotnet_version" "$toolchains/dotnet"
  fi
fi

say "Node $node_version"
node_dir="$toolchains/node-v$node_version-linux-x64"
if [[ -x "$node_dir/bin/node" ]] && [[ "$("$node_dir/bin/node" --version)" == "v$node_version" ]]; then
  ok "$node_dir"
else
  need "Node $node_version in $node_dir"
  if ! $dry_run; then
    mkdir -p "$toolchains"
    base="https://nodejs.org/dist/v$node_version"
    tarball="node-v$node_version-linux-x64.tar.xz"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    curl -fsSL "$base/$tarball" -o "$work/$tarball"
    curl -fsSL "$base/SHASUMS256.txt" -o "$work/SHASUMS256.txt"
    (cd "$work" && grep " $tarball\$" SHASUMS256.txt | sha256sum -c --quiet) || { echo "setup-fedora: Node download failed its checksum." >&2; exit 1; }
    tar -xJf "$work/$tarball" -C "$toolchains"
  else
    printf '  would download and verify %s\n' "node-v$node_version-linux-x64.tar.xz"
  fi
fi

# Everything below runs with the repo's own toolchains first on PATH.
# shellcheck source=dev-env.sh
source "$root/tools/dev-env.sh"

if [[ -n "$npm_version" ]]; then
  say "npm $npm_version"
  if command -v npm >/dev/null 2>&1 && [[ "$(npm --version)" == "$npm_version" ]]; then
    ok "npm $npm_version"
  else
    need "npm $npm_version"
    run npm install -g "npm@$npm_version"
  fi
fi

if $deps; then
  say "Project dependencies"
  (cd "$root" && run npm ci && run node tools/dotnet.mjs restore VibeMiner.slnx --locked-mode)
fi

say "GitHub CLI"
if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
  ok "gh is logged in"
else
  say "  note     run 'gh auth login' to use issues and pushes"
fi

say ""
if $dry_run; then
  say "Dry run: $missing item(s) missing. Run without --dry-run to install them."
else
  say "Done. In each new shell: source tools/dev-env.sh, then npm run build && npm start (see BUILDING.md)."
fi
