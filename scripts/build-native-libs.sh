#!/usr/bin/env bash
set -euo pipefail

# Find NDK
if [ -z "${ANDROID_NDK_HOME:-}" ]; then
  if [ -n "${ANDROID_HOME:-}" ] && [ -d "$ANDROID_HOME/ndk" ]; then
    ANDROID_NDK_HOME=$(find "$ANDROID_HOME/ndk" -maxdepth 1 -mindepth 1 | sort -V | tail -n 1)
  elif [ -n "${ANDROID_NDK_ROOT:-}" ]; then
    ANDROID_NDK_HOME="$ANDROID_NDK_ROOT"
  else
    echo "ERROR: ANDROID_NDK_HOME is not set and could not be detected."
    exit 1
  fi
fi

echo "=== Using Android NDK: $ANDROID_NDK_HOME ==="

TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
ABI="arm64-v8a"
MIN_API="android-26"
NUM_JOBS=$(nproc 2>/dev/null || echo 4)
BUILD_DIR="$(pwd)/.build-natives"
OUT_DIR="$(pwd)/Content.Android/lib/$ABI"

mkdir -p "$BUILD_DIR"
mkdir -p "$OUT_DIR"

# 1. Build FreeType
echo "=== Building FreeType ==="
if [ ! -f "$OUT_DIR/libfreetype.so" ] || [ ! -f "$OUT_DIR/libfreetype6.so" ]; then
  FT_SRC="$BUILD_DIR/freetype"
  if [ ! -d "$FT_SRC" ]; then
    git clone --depth 1 --branch VER-2-13-3 https://gitlab.freedesktop.org/freetype/freetype.git "$FT_SRC"
  fi
  cmake -B "$FT_SRC/build" -S "$FT_SRC" \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI="$ABI" \
    -DANDROID_PLATFORM="$MIN_API" \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DFT_DISABLE_ZLIB=TRUE \
    -DFT_DISABLE_BZIP2=TRUE \
    -DFT_DISABLE_PNG=TRUE \
    -DFT_DISABLE_HARFBUZZ=TRUE \
    -DFT_DISABLE_BROTLI=TRUE
  cmake --build "$FT_SRC/build" --config Release -j"$NUM_JOBS"
  find "$FT_SRC/build" -name "libfreetype.so" -exec cp {} "$OUT_DIR/libfreetype.so" \;
  cp "$OUT_DIR/libfreetype.so" "$OUT_DIR/libfreetype6.so"
  echo "FreeType built successfully"
else
  echo "FreeType already built"
fi

# 2. Build Zstd
echo "=== Building Zstd ==="
if [ ! -f "$OUT_DIR/libzstd.so" ]; then
  ZSTD_SRC="$BUILD_DIR/zstd"
  if [ ! -d "$ZSTD_SRC" ]; then
    git clone --depth 1 --branch v1.5.6 https://github.com/facebook/zstd.git "$ZSTD_SRC"
  fi
  cmake -B "$ZSTD_SRC/build/cmake/build" -S "$ZSTD_SRC/build/cmake" \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI="$ABI" \
    -DANDROID_PLATFORM="$MIN_API" \
    -DCMAKE_BUILD_TYPE=Release \
    -DZSTD_BUILD_SHARED=ON \
    -DZSTD_BUILD_STATIC=OFF \
    -DZSTD_BUILD_PROGRAMS=OFF \
    -DZSTD_BUILD_TESTS=OFF
  cmake --build "$ZSTD_SRC/build/cmake/build" --config Release --target libzstd_shared -j"$NUM_JOBS"
  find "$ZSTD_SRC/build/cmake/build" -name "libzstd.so" -exec cp {} "$OUT_DIR/libzstd.so" \;
  echo "Zstd built successfully"
else
  echo "Zstd already built"
fi

# 3. Build OpenAL Soft
echo "=== Building OpenAL Soft ==="
if [ ! -f "$OUT_DIR/libopenal.so" ]; then
  OPENAL_SRC="$BUILD_DIR/openal"
  if [ ! -d "$OPENAL_SRC" ]; then
    git clone --depth 1 --branch 1.23.1 https://github.com/kcat/openal-soft.git "$OPENAL_SRC"
  fi
  cmake -B "$OPENAL_SRC/build" -S "$OPENAL_SRC" \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI="$ABI" \
    -DANDROID_PLATFORM="$MIN_API" \
    -DCMAKE_BUILD_TYPE=Release \
    -DLIBTYPE=SHARED \
    -DALSOFT_EXAMPLES=OFF \
    -DALSOFT_UTILS=OFF \
    -DALSOFT_TESTS=OFF
  cmake --build "$OPENAL_SRC/build" --config Release -j"$NUM_JOBS"
  find "$OPENAL_SRC/build" -name "libopenal.so" -exec cp {} "$OUT_DIR/libopenal.so" \;
  echo "OpenAL Soft built successfully"
else
  echo "OpenAL Soft already built"
fi

# 4. Fetch SDL3 (official C# Android build by ppy/SDL3-CS)
echo "=== Fetching SDL3 for .NET C# ==="
if [ ! -f "$OUT_DIR/libSDL3.so" ]; then
  curl -sSL "https://github.com/ppy/SDL3-CS/raw/master/native/android/arm64-v8a/libSDL3.so" -o "$OUT_DIR/libSDL3.so"
  echo "SDL3 fetched successfully"
else
  echo "SDL3 already built"
fi

# 5. Build Libsodium
echo "=== Building Libsodium ==="
if [ ! -f "$OUT_DIR/libsodium.so" ]; then
  SODIUM_SRC="$BUILD_DIR/libsodium"
  if [ ! -d "$SODIUM_SRC" ]; then
    git clone --depth 1 --branch 1.0.20-RELEASE https://github.com/jedisct1/libsodium.git "$SODIUM_SRC"
  fi
  (
    cd "$SODIUM_SRC"
    ./autogen.sh
    export ANDROID_NDK_HOME="$ANDROID_NDK_HOME"
    export NDK_PLATFORM="$MIN_API"
    export LIBSODIUM_FULL_BUILD=1
    ./dist-build/android-armv8-a.sh
  )
  find "$SODIUM_SRC" -name "libsodium.so" -exec cp {} "$OUT_DIR/libsodium.so" \;
  echo "Libsodium built successfully"
else
  echo "Libsodium already built"
fi

echo "=== All native libraries successfully built! ==="
ls -lh "$OUT_DIR"
