#!/usr/bin/env bash
# Installs Tedd.DotNetLab and configures Codex, Claude Code, Cursor, and/or Google Antigravity on Linux/WSL x64.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.sh | bash
#   ./tools/install.sh --from-source --client all

set -euo pipefail

CLIENT="${DOTNETLAB_CLIENT:-all}"
SCOPE="${DOTNETLAB_SCOPE:-user}"
PROJECT_DIR="${DOTNETLAB_PROJECT_DIR:-}"
INSTALL_DIR="${DOTNETLAB_INSTALL_DIR:-}"
WORKSPACE="${DOTNETLAB_WORKSPACE:-}"
REPO="${DOTNETLAB_REPO:-tedd/Tedd.DotNetLab}"
VERSION="${DOTNETLAB_VERSION:-latest}"
CODEX_HOME_OPT="${DOTNETLAB_CODEX_HOME:-}"
CLAUDE_HOME_OPT="${DOTNETLAB_CLAUDE_HOME:-}"
CLAUDE_CONFIG_OPT="${DOTNETLAB_CLAUDE_CONFIG:-}"
CURSOR_HOME_OPT="${DOTNETLAB_CURSOR_HOME:-}"
ANTIGRAVITY_HOME_OPT="${DOTNETLAB_ANTIGRAVITY_HOME:-}"
FROM_SOURCE="${DOTNETLAB_FROM_SOURCE:-0}"
ENABLE_NATIVE="${DOTNETLAB_ENABLE_NATIVE:-0}"
SKIP_SYMLINK="${DOTNETLAB_SKIP_PATH:-0}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --client) CLIENT="$2"; shift 2 ;;
        --scope) SCOPE="$2"; shift 2 ;;
        --project-dir) PROJECT_DIR="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --workspace) WORKSPACE="$2"; shift 2 ;;
        --repo) REPO="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --codex-home) CODEX_HOME_OPT="$2"; shift 2 ;;
        --claude-home) CLAUDE_HOME_OPT="$2"; shift 2 ;;
        --claude-config) CLAUDE_CONFIG_OPT="$2"; shift 2 ;;
        --cursor-home) CURSOR_HOME_OPT="$2"; shift 2 ;;
        --antigravity-home) ANTIGRAVITY_HOME_OPT="$2"; shift 2 ;;
        --from-source) FROM_SOURCE=1; shift ;;
        --enable-native-extension) ENABLE_NATIVE=1; shift ;;
        --skip-path) SKIP_SYMLINK=1; shift ;;
        -h|--help)
            cat <<'EOF'
Usage: install.sh [options]
  --client <all|codex|claude|cursor|antigravity|none>  AI client(s) to configure (default: all)
  --scope <user|project>                               Configuration scope (default: user)
  --project-dir <path>                                 Project directory when --scope project
  --install-dir <path>                                 Binary directory (default: $XDG_DATA_HOME/dotnetlab/bin or ~/.local/share/dotnetlab/bin)
  --workspace <path>                                   Tedd.DotNetLab workspace directory
  --version <tag|latest>                               GitHub Release tag (default: latest)
  --from-source                                        Build from local checkout or shallow git clone
  --enable-native-extension                            Expose optional lab.patch MCP tool
  --skip-path                                          Do not create ~/.local/bin/dotnetlab symlink
EOF
            exit 0 ;;
        *) echo "[Tedd.DotNetLab] Unknown argument: $1" >&2; exit 1 ;;
    esac
done

log() { printf '[Tedd.DotNetLab] %s\n' "$*"; }

if [[ "$(uname -s)" != "Linux" ]] || [[ "$(uname -m)" != "x86_64" ]]; then
    echo "[Tedd.DotNetLab] Linux x86_64 (or WSL2 x86_64) is required. On Windows PowerShell, use tools/install.ps1." >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[Tedd.DotNetLab] 'dotnet' was not found on PATH. Install .NET 10+ (including .NET 11/12 previews & RCs) from https://dotnet.microsoft.com/download" >&2
    exit 1
fi
DOTNET_EXE="$(command -v dotnet)"

if ! "$DOTNET_EXE" --list-runtimes 2>/dev/null | grep -qE '^Microsoft\.NETCore\.App (10|[1-9][1-9]|[2-9][0-9])\.'; then
    echo "[Tedd.DotNetLab] Tedd.DotNetLab controller requires Microsoft.NETCore.App 10.0 or later (any stable, RC, or preview release; target apps on .NET 6+ are supported)." >&2
    exit 1
fi

if [[ -z "$INSTALL_DIR" ]]; then
    XDG_SHARE="${XDG_DATA_HOME:-$HOME/.local/share}"
    INSTALL_DIR="$XDG_SHARE/dotnetlab/bin"
fi
mkdir -p "$INSTALL_DIR"
INSTALL_DIR="$(cd "$INSTALL_DIR" && pwd)"

make_scratch() {
    mktemp -d "$(dirname "$INSTALL_DIR")/.dotnetlab-install.XXXXXX"
}

install_from_release() (
    local api_url
    if [[ "$VERSION" == "latest" ]]; then
        api_url="https://api.github.com/repos/$REPO/releases/latest"
    else
        api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
    fi
    log "Checking GitHub Releases ($REPO @ $VERSION)..."
    local scratch
    scratch="$(make_scratch)"
    trap 'rm -rf -- "$scratch"' EXIT

    local http_code
    http_code="$(curl -sSL -w '%{http_code}' -H 'Accept: application/vnd.github+json' -o "$scratch/release.json" "$api_url" || true)"
    if [[ "$http_code" == "404" && "$VERSION" == "latest" ]]; then
        log "No published GitHub Release found yet; falling back to source build..."
        install_from_source
        return 0
    fi
    if [[ "$http_code" != "200" ]]; then
        echo "[Tedd.DotNetLab] Failed to query GitHub Releases (HTTP $http_code)." >&2
        exit 1
    fi

    local asset_url asset_name
    asset_url="$(grep -oE '"browser_download_url":[[:space:]]*"[^"]*(linux-x64\.(tar\.gz|zip))"' "$scratch/release.json" | head -n1 | sed -E 's/.*"([^"]+)"$/\1/' || true)"
    if [[ -z "$asset_url" ]]; then
        log "No prebuilt linux-x64 archive in release; falling back to source build..."
        install_from_source
        return 0
    fi
    asset_name="$(basename "$asset_url")"
    log "Downloading $asset_name..."
    curl -fsSL -o "$scratch/$asset_name" "$asset_url"

    local sums_url
    sums_url="$(grep -oE '"browser_download_url":[[:space:]]*"[^"]*SHA256SUMS\.txt"' "$scratch/release.json" | head -n1 | sed -E 's/.*"([^"]+)"$/\1/' || true)"
    if [[ -n "$sums_url" ]] && command -v sha256sum >/dev/null 2>&1; then
        curl -fsSL -o "$scratch/SHA256SUMS.txt" "$sums_url"
        local expected actual
        expected="$(awk -v f="$asset_name" '$2 == f || $2 == "*"f { print tolower($1); exit }' "$scratch/SHA256SUMS.txt")"
        if [[ -n "$expected" ]]; then
            actual="$(sha256sum "$scratch/$asset_name" | awk '{ print tolower($1) }')"
            if [[ "$actual" != "$expected" ]]; then
                echo "[Tedd.DotNetLab] SHA-256 mismatch for $asset_name: expected $expected, got $actual" >&2
                exit 1
            fi
        fi
    fi

    mkdir -p "$scratch/extracted"
    if [[ "$asset_name" == *.tar.gz ]]; then
        tar -xzf "$scratch/$asset_name" -C "$scratch/extracted"
    else
        unzip -q "$scratch/$asset_name" -d "$scratch/extracted"
    fi
    cp -Rf "$scratch/extracted/." "$INSTALL_DIR/"
    return 0
)

install_from_source() (
    if ! "$DOTNET_EXE" --list-sdks 2>/dev/null | grep -qE '^(10|[1-9][1-9]|[2-9][0-9])\.'; then
        echo "[Tedd.DotNetLab] Building from source requires a .NET 10 or newer SDK (including .NET 11/12 preview or RC SDKs)." >&2
        exit 1
    fi

    local script_dir=""
    if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
        script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    fi
    local local_repo=""
    if [[ -n "$script_dir" && -f "$script_dir/../src/Tedd.DotNetLab.Host/Tedd.DotNetLab.Host.csproj" ]]; then
        local_repo="$(cd "$script_dir/.." && pwd)"
    elif [[ -f "./src/Tedd.DotNetLab.Host/Tedd.DotNetLab.Host.csproj" ]]; then
        local_repo="$(pwd)"
    fi

    local scratch
    scratch="$(make_scratch)"
    trap 'rm -rf -- "$scratch"' EXIT

    local source_root="$local_repo"
    if [[ -z "$source_root" ]]; then
        if ! command -v git >/dev/null 2>&1; then
            echo "[Tedd.DotNetLab] git is required when installing from source outside a checkout." >&2
            exit 1
        fi
        source_root="$scratch/repo"
        log "Cloning https://github.com/$REPO.git ..."
        git clone --depth 1 "https://github.com/$REPO.git" "$source_root"
    else
        log "Building from local checkout at $source_root ..."
    fi

    local build_root="$scratch/build"
    local publish_dir="$scratch/publish"
    local host_proj="$source_root/src/Tedd.DotNetLab.Host/Tedd.DotNetLab.Host.csproj"

    log "Restoring and publishing Tedd.DotNetLab.Host..."
    "$DOTNET_EXE" restore "$host_proj" --locked-mode "-p:DotNetLabBuildRoot=$build_root"
    "$DOTNET_EXE" publish "$host_proj" -c Release --no-restore --output "$publish_dir" "-p:DotNetLabBuildRoot=$build_root" -p:UseAppHost=false -p:SelfContained=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false
    cp -Rf "$publish_dir/." "$INSTALL_DIR/"
)

if [[ "$FROM_SOURCE" == "1" || "$FROM_SOURCE" == "true" ]]; then
    install_from_source
else
    install_from_release
fi

HOST_DLL="$INSTALL_DIR/Tedd.DotNetLab.Host.dll"
if [[ ! -f "$HOST_DLL" ]]; then
    echo "[Tedd.DotNetLab] Expected host assembly at $HOST_DLL was not found." >&2
    exit 1
fi

SHIM="$INSTALL_DIR/dotnetlab"
cat > "$SHIM" <<EOF
#!/usr/bin/env bash
export DOTNET_ROLL_FORWARD_TO_PRERELEASE=1
exec "$DOTNET_EXE" --roll-forward Major "$HOST_DLL" "\$@"
EOF
chmod +x "$SHIM"

if [[ "$SKIP_SYMLINK" != "1" && "$SKIP_SYMLINK" != "true" ]]; then
    LOCAL_BIN="$HOME/.local/bin"
    mkdir -p "$LOCAL_BIN"
    ln -sf "$SHIM" "$LOCAL_BIN/dotnetlab"
    log "Linked CLI wrapper at $LOCAL_BIN/dotnetlab"
fi

if [[ "$CLIENT" != "none" ]]; then
    log "Configuring AI client(s): $CLIENT (scope: $SCOPE)..."
    INSTALL_ARGS=("--roll-forward" "Major" "$HOST_DLL" "install" "$CLIENT" "--scope" "$SCOPE" "--dotnet" "$DOTNET_EXE")
    [[ -n "$WORKSPACE" ]] && INSTALL_ARGS+=("--workspace" "$WORKSPACE")
    [[ -n "$PROJECT_DIR" ]] && INSTALL_ARGS+=("--project-dir" "$PROJECT_DIR")
    [[ -n "$CODEX_HOME_OPT" ]] && INSTALL_ARGS+=("--codex-home" "$CODEX_HOME_OPT")
    [[ -n "$CLAUDE_HOME_OPT" ]] && INSTALL_ARGS+=("--claude-home" "$CLAUDE_HOME_OPT")
    [[ -n "$CLAUDE_CONFIG_OPT" ]] && INSTALL_ARGS+=("--claude-config" "$CLAUDE_CONFIG_OPT")
    [[ -n "$CURSOR_HOME_OPT" ]] && INSTALL_ARGS+=("--cursor-home" "$CURSOR_HOME_OPT")
    [[ -n "$ANTIGRAVITY_HOME_OPT" ]] && INSTALL_ARGS+=("--antigravity-home" "$ANTIGRAVITY_HOME_OPT")
    [[ "$ENABLE_NATIVE" == "1" || "$ENABLE_NATIVE" == "true" ]] && INSTALL_ARGS+=("--enable-native-extension")

    DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 "$DOTNET_EXE" "${INSTALL_ARGS[@]}"
fi

log "Installation complete! Restart Codex / Claude Code / Cursor / Antigravity so the new MCP server and 'dotnetlab' skill load."
