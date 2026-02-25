#!/bin/bash
set -euo pipefail

if [ $# -lt 2 ]; then
    echo "Usage: $0 <version> <nuget-api-key>"
    exit 1
fi

VERSION="$1"
API_KEY="$2"
CONFIGURATION="Release"
OUTPUT_DIR="$(mktemp -d)"

PROJECTS=(
    src/NWayland.Generator/NWayland.Generator.csproj
    src/NWayland/NWayland.csproj
    src/NWayland.Protocols.Wlr/NWayland.Protocols.Wlr.csproj
    src/NWayland.Protocols.Plasma/NWayland.Protocols.Plasma.csproj
)

echo "Building version $VERSION..."
for proj in "${PROJECTS[@]}"; do
    dotnet build "$proj" -c "$CONFIGURATION" /p:Version="$VERSION"
done

echo ""
echo "Packing version $VERSION..."
for proj in "${PROJECTS[@]}"; do
    dotnet pack "$proj" -c "$CONFIGURATION" -o "$OUTPUT_DIR" /p:Version="$VERSION" --no-build
done

echo ""
echo "Pushing packages to nuget.org..."
for nupkg in "$OUTPUT_DIR"/*.nupkg; do
    echo "  Pushing $(basename "$nupkg")"
    dotnet nuget push "$nupkg" --api-key "$API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
done

rm -rf "$OUTPUT_DIR"
echo "Done."
