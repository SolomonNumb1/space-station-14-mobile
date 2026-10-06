#!/usr/bin/env bash
set -euo pipefail

# Find Android NDK
if [ -z "${ANDROID_NDK_HOME:-}" ]; then
    if [ -n "${ANDROID_HOME:-}" ] && [ -d "${ANDROID_HOME}/ndk" ]; then
        ANDROID_NDK_HOME=$(ls -d "${ANDROID_HOME}/ndk"/* 2>/dev/null | sort -V | tail -n 1)
    else
        echo "Error: ANDROID_NDK_HOME or ANDROID_HOME/ndk not found!"
        exit 1
    fi
fi

echo "=== Using Android NDK: $ANDROID_NDK_HOME ==="
TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
TARGET_DIR="$(pwd)/Content.Android/lib/arm64-v8a"
mkdir -p "$TARGET_DIR"

BUILD_DIR="$(pwd)/native-build"
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
cd "$BUILD_DIR"

NPROC=$(nproc 2>/dev/null || echo 4)

# 1. Build SDL3 (matching ppy/SDL3-CS commit with SDL_ANDROID_JAR=OFF)
echo "=== Building SDL3 ==="
mkdir -p sdl3 && cd sdl3
git init -q
git remote add origin https://github.com/libsdl-org/SDL.git
git fetch --depth 1 origin f0e99e7c7f9aa90d5ce2e3b8a69f72c23faf257e
git checkout -q FETCH_HEAD
cmake -B build \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=21 \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DSDL_SHARED=ON \
    -DSDL_STATIC=OFF \
    -DSDL_ANDROID_JAR=OFF \
    -DSDL_TEST_LIBRARY=OFF
cmake --build build -j"$NPROC"
SDL_LIB=$(find build -name "libSDL3.so" | head -n 1)
cp "$SDL_LIB" "$TARGET_DIR/libSDL3.so"
cd ..

# 2. Build FreeType (official VER-2-13-3 without external shared dependencies)
echo "=== Building FreeType ==="
git clone --depth 1 --branch VER-2-13-3 https://github.com/freetype/freetype.git freetype
cmake -B build-ft -S freetype \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=21 \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DFT_DISABLE_BZIP2=TRUE \
    -DFT_DISABLE_PNG=TRUE \
    -DFT_DISABLE_HARFBUZZ=TRUE \
    -DFT_DISABLE_BROTLI=TRUE \
    -DFT_REQUIRE_ZLIB=FALSE
cmake --build build-ft -j"$NPROC"
FT_LIB=$(find build-ft -name "libfreetype.so" | head -n 1)
cp "$FT_LIB" "$TARGET_DIR/libfreetype.so"
cp "$FT_LIB" "$TARGET_DIR/libfreetype6.so"

# 3. Build OpenAL Soft (official 1.24.3)
echo "=== Building OpenAL Soft ==="
git clone --depth 1 --branch 1.24.3 https://github.com/kcat/openal-soft.git openal-soft
cmake -B build-al -S openal-soft \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=21 \
    -DCMAKE_BUILD_TYPE=Release \
    -DLIBTYPE=SHARED \
    -DALSOFT_EXAMPLES=OFF \
    -DALSOFT_TESTS=OFF \
    -DALSOFT_UTILS=OFF \
    -DALSOFT_EMBED_HRTF_DATA=YES
cmake --build build-al -j"$NPROC"
AL_LIB=$(find build-al -name "libopenal.so" | head -n 1)
cp "$AL_LIB" "$TARGET_DIR/libopenal.so"

# 4. Build Zstd (official v1.5.7)
echo "=== Building Zstd ==="
git clone --depth 1 --branch v1.5.7 https://github.com/facebook/zstd.git zstd
cmake -B build-zstd -S zstd/build/cmake \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=21 \
    -DCMAKE_BUILD_TYPE=Release \
    -DZSTD_BUILD_PROGRAMS=OFF \
    -DZSTD_BUILD_STATIC=OFF \
    -DZSTD_BUILD_SHARED=ON
cmake --build build-zstd -j"$NPROC"
ZSTD_LIB=$(find build-zstd -name "libzstd.so" | head -n 1)
cp "$ZSTD_LIB" "$TARGET_DIR/libzstd.so"

# 5. Build libsodium (official 1.0.20-RELEASE)
echo "=== Building libsodium ==="
git clone --depth 1 --branch 1.0.20-RELEASE https://github.com/jedisct1/libsodium.git libsodium
cd libsodium
./autogen.sh
export ANDROID_NDK_HOME="$ANDROID_NDK_HOME"
export NDK_PLATFORM=android-21
./dist-build/android-armv8-a.sh
SODIUM_LIB=$(find libsodium-android* -name "libsodium.so" | head -n 1)
cp "$SODIUM_LIB" "$TARGET_DIR/libsodium.so"
cd ..

cd ..
rm -rf "$BUILD_DIR"

echo "=== All 6 native libraries successfully built from source ==="
ls -lh "$TARGET_DIR"
