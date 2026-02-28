#!/bin/bash
set -e

DIST="../dist/win-arm64"

echo "Building NanoredVPN for Windows ARM64..."

dotnet publish ../src/SingBoxClient.Desktop -c Release -r win-arm64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -o "$DIST"

# Copy runtime files
cp ../runtime/win-arm64/sing-box.exe "$DIST/"

# ── Organize DLLs into subdirectories ─────────────────────────────────
echo "Organizing DLLs..."
mkdir -p "$DIST/libs"
mkdir -p "$DIST/dotnet"

cd "$DIST"
for dll in *.dll; do
    case "$dll" in
        # App assemblies — keep in root
        SingBoxClient.*)
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
echo "dotnet/ ($(ls -1 "$DIST/dotnet/" | wc -l) files)"
echo "libs/   ($(ls -1 "$DIST/libs/" | wc -l) files)"
