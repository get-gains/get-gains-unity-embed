# Flutter + Unity: Repo Setup

This repository contains a Unity project configured to be embedded within a Flutter application. It includes scripts for exporting the Unity project as a library for Android and iOS, and utilities for communication between Unity and Flutter.

The Android app embeds a Unity build. The Unity export (`android/unityLibrary`) is **not** in the repo because it’s large and generated. You add it locally by exporting from the Unity project.

---

## File system (Flutter project)

Expected layout of the **Flutter repo** (`get_gains_app` or your app name) after setup:

```text
get_gains_app/
├── android/
│   ├── app/
│   │   └── build.gradle
│   ├── unityLibrary/          ← Unity export goes here (not in repo)
│   │   ├── build.gradle
│   │   └── src/main/jniLibs/
│   │       ├── arm64-v8a/
│   │       │   └── libmain.so
│   │       └── armeabi-v7a/
│   │           └── libmain.so
│   ├── build.gradle
│   └── local.properties       ← Local only (gitignored): sdk.dir, etc.
├── lib/
├── pubspec.yaml
└── ...
```

- **`android/unityLibrary`** – Exported from this Unity project via flutter_embed_unity. Do not commit; each developer exports or copies it locally.
- **`android/local.properties`** – Local machine paths (SDK, optional NDK); gitignored.

---

## Prerequisites

- Flutter SDK, Android SDK, NDK (e.g. 27.2.12479018 for Unity 6000.0)
- Unity **6000.0** with the [flutter_embed_unity](https://github.com/learntoflutter/flutter_embed_unity) export flow
- Your Unity project set up for Flutter embedding (see this repo’s `docs/flutter-unity.md` for the bridge script)

---

## 1. Clone and open the Flutter repo

```bash
git clone <repo-url>
cd get_gains_app
flutter pub get
```

Do **not** run `flutter run` yet — the Android build will fail until `unityLibrary` is present.

---

## 2. Export Unity into this repo

In **Unity** (this project):

1. Open your Unity 6000.0 project (the one that has the `FlutterUnityBridge` scene/script).
2. Use the **flutter_embed_unity** export (e.g. **File → Build Settings → Export for Flutter** or the plugin’s export option).
3. When the plugin asks for an export path, choose:
   - **`<path-to-this-repo>/get_gains_app/android/unityLibrary`**  
     i.e. the `android/unityLibrary` folder inside the Flutter project.

If the plugin exports to a different folder (e.g. `unityLibrary` on your desktop), **copy that entire folder** into:

```text
get_gains_app/android/unityLibrary
```

So that this path exists:

```text
get_gains_app/android/unityLibrary/build.gradle
get_gains_app/android/unityLibrary/src/main/jniLibs/arm64-v8a/libmain.so
...
```

---

## 3. Android local config (one-time)

From the Flutter repo root:

```bash
cd android
```

- If Android Studio or Flutter hasn’t created `local.properties`, create it with:
  - `sdk.dir=<path-to-android-sdk>`
  - (Optional) any Unity/NDK paths the Unity `build.gradle` expects, if required by the export.)

`local.properties` is gitignored; each developer sets it locally.

---

## 4. Build and run

From the **repo root** (`get_gains_app`):

```bash
flutter run
```

Or open the project in Android Studio and run the app. Use an **ARM64** device or an emulator with **arm64-v8a** (or “Translated ABI: arm64-v8a”); the Unity libs are ARM-only.

---

## Summary

| Step | Action                                                                             |
| ---- | ---------------------------------------------------------------------------------- |
| 1    | Clone repo, `flutter pub get`                                                      |
| 2    | Export Unity into `android/unityLibrary` (or copy the exported folder there)       |
| 3    | Ensure `android/local.properties` has `sdk.dir` (and any required Unity/NDK paths) |
| 4    | `flutter run` on an ARM64 device or emulator                                       |

After you change the Unity scene or scripts, **re-export** to `android/unityLibrary` and rebuild the Flutter app.
