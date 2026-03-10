# Get Gains Unity Embed - Feature Index

> **Purpose**: Navigation hub for all Unity embed features and documentation used by the Flutter Get Gains app.

---

## Overview

**Get Gains Unity Embed** is a Unity 6000.0 project embedded inside the Flutter Get Gains app via `flutter_embed_unity_6000_0_android`.  
It renders interactive 3D content and exposes a bidirectional messaging bridge between Flutter and Unity.

### Technology Stack

| Technology            | Version   | Purpose                                |
| --------------------- | --------- | -------------------------------------- |
| **Unity**             | 6000.0    | 3D engine / scene runtime              |
| **C#**                | 10+       | Gameplay & bridge scripting            |
| **flutter_embed_unity** | 6000_0  | Embedding Unity view into Flutter app  |

### Architecture Approach

- **Single-bridge pattern**: one `FlutterUnityBridge` GameObject as the messaging hub
- **String-based contract** for Flutter ↔ Unity messages
- **Scene-driven**: Unity is responsible only for visuals + contract, business logic lives in Flutter/Server

---

## Documentation Structure

| Document                                 | Purpose                                          |
| ---------------------------------------- | ------------------------------------------------ |
| `FEATURE_INDEX.md` (this file)           | Navigation hub for Unity embed docs              |
| [flutter-unity.md](flutter-unity.md)     | Flutter ↔ Unity bridge contract and checklist    |

---

## Feature Categories

### Flutter ↔ Unity Messaging Bridge

| Feature                       | Description                                                        | Status   | Documentation                                  |
| ----------------------------- | ------------------------------------------------------------------ | -------- | ---------------------------------------------- |
| Bridge GameObject            | Single `FlutterUnityBridge` GameObject in active scene            | ✅ Planned / Defined | [flutter-unity.md](flutter-unity.md) |
| Incoming Messages (Flutter)  | `SetRotationSpeed`, `OnMessageFromFlutter`, `OnJsonFromFlutter`   | ✅ Planned / Defined | [flutter-unity.md](flutter-unity.md) |
| Outgoing Messages (Unity)    | `scene_loaded` + arbitrary event strings back to Flutter          | ✅ Planned / Defined | [flutter-unity.md](flutter-unity.md) |
| Scene Lifecycle Integration  | Send `scene_loaded` once when the scene is ready                  | ✅ Planned / Defined | [flutter-unity.md](flutter-unity.md) |

**Primary Responsibilities (Unity side):**

- Maintain a persistent `FlutterUnityBridge` GameObject in the main scene
- Implement public `void` methods that take a single `string` parameter, called from Flutter
- Use the plugin API (e.g. `SendToFlutter.Send(string message)`) to notify Flutter of scene readiness and other events

---

## Quick Start Guide

### How to Navigate Unity Docs

1. **New to the Unity embed?** Start with [flutter-unity.md](flutter-unity.md) for the exact contract.
2. **Implementing the bridge?** Follow the summary checklist in `flutter-unity.md`.

### Common Workflow: Implementing the Bridge

1. Create or reuse a GameObject named `FlutterUnityBridge` in your main scene.
2. Attach a script (e.g. `FlutterUnityBridge.cs`) implementing:
   - `public void SetRotationSpeed(string message)`
   - `public void OnMessageFromFlutter(string message)`
   - (Optional) `public void OnJsonFromFlutter(string message)`
3. On scene ready, send **exactly** `scene_loaded` once to Flutter.
4. Use the plugin’s send API to propagate other events back to Flutter.

---

## Documentation Conventions

### Documentation Hierarchy

```
FEATURE_INDEX.md   → Navigation hub (this file)
flutter-unity.md   → Detailed bridge contract and checklist
```

**Create new Unity docs when:**

- You introduce a new embedded Unity scene or major interaction mode
- You add non-trivial messaging contracts between Flutter and Unity

---

_Last updated: March 5, 2026_

