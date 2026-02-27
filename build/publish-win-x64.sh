#!/bin/bash
set -e

echo "Building NanoredVPN for Windows x64..."

dotnet publish ../src/SingBoxClient.Desktop -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../dist/win-x64

# Copy runtime files
cp ../runtime/win-x64/sing-box.exe ../dist/win-x64/

echo "Build complete: dist/win-x64/"
echo "Files:"
ls -lh ../dist/win-x64/
