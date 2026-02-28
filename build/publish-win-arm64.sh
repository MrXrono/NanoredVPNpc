#!/bin/bash
set -e

RID="win-arm64"
DIST="../dist/$RID"
DOTNET_VERSION="10.0"
DOTNET_INSTALL_DIR="${DOTNET_ROOT:-$HOME/.dotnet}"

# ── Prerequisites check ──────────────────────────────────────────────
echo "=== Checking prerequisites ==="

# 1. .NET SDK
install_dotnet() {
    echo "[!] .NET $DOTNET_VERSION SDK not found. Installing..."
    local installer="/tmp/dotnet-install.sh"
    if command -v curl &>/dev/null; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    elif command -v wget &>/dev/null; then
        wget -qO "$installer" https://dot.net/v1/dotnet-install.sh
    else
        echo "[x] Neither curl nor wget found. Cannot download .NET installer." >&2
        exit 1
    fi
    chmod +x "$installer"
    "$installer" --channel "$DOTNET_VERSION" --install-dir "$DOTNET_INSTALL_DIR"
    rm -f "$installer"
    export PATH="$DOTNET_INSTALL_DIR:$PATH"
    export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
    echo "[✓] .NET SDK installed to $DOTNET_INSTALL_DIR"
}

if command -v dotnet &>/dev/null; then
    SDK_LIST=$(dotnet --list-sdks 2>/dev/null || true)
    if echo "$SDK_LIST" | grep -q "^${DOTNET_VERSION}\."; then
        echo "[✓] .NET $DOTNET_VERSION SDK found"
    else
        echo "[!] dotnet CLI found but no $DOTNET_VERSION SDK installed"
        install_dotnet
    fi
else
    install_dotnet
fi

# 2. tar (for archiving)
if command -v tar &>/dev/null; then
    echo "[✓] tar found"
else
    echo "[!] tar not found. Installing..."
    if command -v apt-get &>/dev/null; then
        sudo apt-get update -qq && sudo apt-get install -y -qq tar
    elif command -v apk &>/dev/null; then
        apk add --no-cache tar
    elif command -v yum &>/dev/null; then
        sudo yum install -y tar
    else
        echo "[x] Cannot install tar — unsupported package manager." >&2
        exit 1
    fi
    echo "[✓] tar installed"
fi

# 3. sing-box runtime binary
SINGBOX_SRC="../runtime/$RID/sing-box.exe"
if [ -f "$SINGBOX_SRC" ]; then
    echo "[✓] sing-box binary found: $SINGBOX_SRC"
else
    echo "[x] sing-box binary not found: $SINGBOX_SRC" >&2
    echo "    Place sing-box.exe in runtime/$RID/ and re-run." >&2
    exit 1
fi

echo "=== All prerequisites satisfied ==="
echo ""

# ── Build ─────────────────────────────────────────────────────────────
echo "Building NanoredVPN for Windows ARM64..."

dotnet publish ../src/SingBoxClient.Desktop -c Release -r $RID \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -o "$DIST"

# Copy runtime files
cp "$SINGBOX_SRC" "$DIST/"

# ── Organize files into subdirectories ─────────────────────────────────
echo "Organizing files..."
mkdir -p "$DIST/Core"
mkdir -p "$DIST/libs"
mkdir -p "$DIST/dotnet"

cd "$DIST"
for dll in *.dll; do
    case "$dll" in
        # Main app assembly — keep in root (loaded by apphost)
        SingBoxClient.Desktop.dll)
            ;;
        # App core library → Core/
        SingBoxClient.Core.dll)
            mv "$dll" Core/
            ;;
        # Native host/runtime — keep in root (loaded before managed code)
        coreclr.dll|hostfxr.dll|hostpolicy.dll|clrjit.dll|clrcompression.dll)
            ;;
        # CoreLib — keep in root (loaded by coreclr before any managed code)
        System.Private.CoreLib.dll)
            ;;
        # Windows CRT — keep in root
        ucrtbase.dll|api-ms-*)
            ;;
        # .NET framework & runtime → dotnet/
        System.*|Microsoft.*|netstandard.dll|mscorlib.dll|WindowsBase.dll)
            mv "$dll" dotnet/
            ;;
        # Debug/diagnostics → dotnet/
        mscordaccore.dll|mscordbi.dll)
            mv "$dll" dotnet/
            ;;
        # Everything else (Avalonia, ReactiveUI, Serilog, SkiaSharp, etc.) → libs/
        *)
            mv "$dll" libs/
            ;;
    esac
done
cd - > /dev/null

echo ""
echo "Build complete: $DIST/"
echo ""
echo "Root files:"
ls -1 "$DIST"/*.exe "$DIST"/*.dll "$DIST"/*.json 2>/dev/null | xargs -I{} basename {}
echo ""
echo "Core/   ($(ls -1 "$DIST/Core/" | wc -l) files)"
echo "dotnet/ ($(ls -1 "$DIST/dotnet/" | wc -l) files)"
echo "libs/   ($(ls -1 "$DIST/libs/" | wc -l) files)"
