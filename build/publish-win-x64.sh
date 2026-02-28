#!/bin/bash
set -e

DIST="../dist/win-x64"

echo "Building NanoredVPN for Windows x64..."

dotnet publish ../src/SingBoxClient.Desktop -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -o "$DIST"

# Copy runtime files
cp ../runtime/win-x64/sing-box.exe "$DIST/"

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
