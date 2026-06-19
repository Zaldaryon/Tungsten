#!/bin/bash
set -e

ZIG="${ZIG:-zig}"
DIR="$(cd "$(dirname "$0")" && pwd)"
SRC="$DIR/tungsten_noise_simplex.c $DIR/noise_column.c"
OUT="$DIR/../native"

# CRITICAL FLAGS:
#   -ffp-contract=off : prevents FMA contraction (C# never uses FMA for scalar)
#   -fwrapv           : signed integer overflow wraps (matches C# unchecked behavior)
CFLAGS="-O3 -shared -fPIC -flto -DNDEBUG -ffp-contract=off -fwrapv -I$DIR"
CFLAGS_NOLTO="-O3 -shared -fPIC -DNDEBUG -ffp-contract=off -fwrapv -I$DIR"

mkdir -p "$OUT/win-x64" "$OUT/linux-x64" "$OUT/linux-arm64" "$OUT/osx-arm64"

echo "Building tungsten_noise for all platforms..."

$ZIG cc $CFLAGS -target x86_64-windows-gnu   -o "$OUT/win-x64/tungsten_noise.dll"         $SRC && echo "  ✓ win-x64"
$ZIG cc $CFLAGS -target x86_64-linux-gnu     -o "$OUT/linux-x64/libtungsten_noise.so"      $SRC && echo "  ✓ linux-x64"
$ZIG cc $CFLAGS -target aarch64-linux-gnu    -o "$OUT/linux-arm64/libtungsten_noise.so"    $SRC && echo "  ✓ linux-arm64"
$ZIG cc $CFLAGS_NOLTO -target aarch64-macos  -o "$OUT/osx-arm64/libtungsten_noise.dylib"   $SRC && echo "  ✓ osx-arm64"

echo ""
echo "Done. Output:"
find "$OUT" -type f -name "*tungsten_noise*" -exec ls -lh {} \;
