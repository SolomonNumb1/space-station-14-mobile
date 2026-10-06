# Space Station 14 - Mobile (Android)

Android port of [Space Station 14](https://github.com/space-wizards/space-station-14).

## Server Compatibility
- Can only join servers running the exact same build version as this APK (Space Station 14 / RobustToolbox v291)
- Servers with different versions or custom forks are not supported

## Controls
- Virtual Thumbstick: Movement
- ACT (Z): Use item in hand
- DROP (Q): Drop item
- SWAP (X): Swap hands
- PULL (Ctrl): Pull objects
- EXAM (Shift): Examine
- INV: Inventory
- KBD: Keyboard
- Pinch to zoom

## Requirements
- Android 8.0+
- ARM64
- OpenGL ES 3.0+

## Architecture
- `Content.Android/` - Android app
- `space-station-14/` - Upstream Space Station 14 (Git submodule)
- `RobustToolbox/` - RobustToolbox engine (Git submodule)
- `patches/` - Client fixes

## Building
Built via GitHub Actions (`.github/workflows/android-release.yml`).
