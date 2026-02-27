#!/bin/bash
set -e

echo "Building NanoredVPN for Windows ARM64..."

dotnet publish ../src/SingBoxClient.Desktop -c Release -r win-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../dist/win-arm64

# Copy runtime files
cp ../runtime/win-arm64/sing-box.exe ../dist/win-arm64/

echo "Build complete: dist/win-arm64/"
echo "Files:"
ls -lh ../dist/win-arm64/
