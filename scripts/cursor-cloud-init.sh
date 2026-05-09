#!/usr/bin/env bash
# Idempotent setup for Cursor Cloud Agent / remote Linux workspaces.
# Installs the .NET 8 SDK user-local (no sudo) and ensures shell PATH.

set -euo pipefail

DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
MARKER="# infantry-online-server: dotnet PATH"

_ensure_path() {
  if ! grep -qF "$MARKER" "$HOME/.bashrc" 2>/dev/null; then
    {
      echo ""
      echo "$MARKER"
      echo "export DOTNET_ROOT=\"\${DOTNET_ROOT:-$DOTNET_INSTALL_DIR}\""
      echo "export PATH=\"\$DOTNET_ROOT:\$PATH\""
    } >>"$HOME/.bashrc"
  fi
}

if command -v dotnet >/dev/null 2>&1; then
  if dotnet --version 2>/dev/null | grep -qE '^8\.'; then
    echo "[cursor-cloud-init] dotnet 8.x already on PATH ($(dotnet --version))"
    _ensure_path
    exit 0
  fi
fi

if [[ -x "$DOTNET_INSTALL_DIR/dotnet" ]]; then
  if "$DOTNET_INSTALL_DIR/dotnet" --version 2>/dev/null | grep -qE '^8\.'; then
    echo "[cursor-cloud-init] dotnet 8.x already installed at $DOTNET_INSTALL_DIR"
    _ensure_path
    export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
    export PATH="$DOTNET_ROOT:$PATH"
    exit 0
  fi
fi

echo "[cursor-cloud-init] Installing .NET SDK 8.0 to $DOTNET_INSTALL_DIR ..."
tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
curl -sSL "https://dot.net/v1/dotnet-install.sh" -o "$tmpdir/dotnet-install.sh"
chmod +x "$tmpdir/dotnet-install.sh"
bash "$tmpdir/dotnet-install.sh" --channel 8.0 --install-dir "$DOTNET_INSTALL_DIR"

_ensure_path

export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_ROOT:$PATH"
echo "[cursor-cloud-init] dotnet $(dotnet --version) ready (DOTNET_ROOT=$DOTNET_ROOT)"
