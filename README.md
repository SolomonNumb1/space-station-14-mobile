# Space Station 14 - Mobile (Android)

Android port of [Space Station 14](https://github.com/space-wizards/space-station-14).

> [!WARNING]
> **Disclaimer / unofficial client**
> 
> This android port is NOT  provided or supported by Space Wizards Federation,and at that moment more like proof of work
> 
> * **use at your own risk:** Stability, controls, and performance may vary depending on your device
> * **Do not report mobile specific issues (crashes, low fps, ui bugs) via in game ahelp or to the official Wizard's Den staff**
> * If you encounter bugs or performance problems related to the Android build, please report them directly in the section [issues](https://github.com/SolomonNumb1/space-station-14-mobile/issues) of this repository
NEVERTHELESS , the project will be supported and improved 

## Server Compatibility
- Can only join servers running the exact same build version as this APK (Space Station 14 / RobustToolbox v291)
- At that moment servers with different versions or custom forks are not supported (some is supported) ...because on pc the engines are download from a CDN, whereas on android none of the engines are pre compiled...

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
