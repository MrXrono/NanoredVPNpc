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

# ── Organize DLLs into libs/ ──────────────────────────────────────────
echo "Organizing DLLs..."
mkdir -p "$DIST/libs"

cd "$DIST"
for dll in *.dll; do
    case "$dll" in
        # App assemblies — keep in root
        SingBoxClient.*)
            ;;
        # .NET runtime & framework — keep in root
        System.*|Microsoft.*|netstandard.dll|mscorlib.dll|WindowsBase.dll)
            ;;
        # Native host/runtime — keep in root
        coreclr.dll|hostfxr.dll|hostpolicy.dll|clrjit.dll|clrcompression.dll)
            ;;
        # Debug/diagnostics — keep in root
        mscordaccore.dll|mscordbi.dll)
            ;;
        # Windows CRT — keep in root
        ucrtbase.dll|api-ms-*)
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
echo "libs/ ($(ls -1 "$DIST/libs/" | wc -l) files):"
ls -1 "$DIST/libs/" | head -10
echo "..."
