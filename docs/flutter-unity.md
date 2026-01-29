## Context

This Unity project is embedded inside a Flutter app using **flutter_embed_unity** (Unity 6000.0 / flutter_embed_unity_6000_0_android). The Flutter app shows the Unity view as a widget and can send/receive string messages.

## Your Task

Implement bidirectional messaging so that:

1. **Flutter → Unity**: Flutter sends messages to a **single** GameObject in the active scene. That GameObject must have a C# script with **public void MethodName(string message)** methods that Flutter can call by name.
2. **Unity → Flutter**: Unity sends messages to Flutter using the plugin’s API (e.g. `SendToFlutter.Send(string message)` or equivalent for your Unity/plugin version).

## Contract (must match Flutter app)

Use these exact names so the Flutter app can call your methods:

| Item                           | Value                                                                                                                                                 |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| **GameObject name** (in scene) | `FlutterUnityBridge`                                                                                                                                  |
| **Script**                     | Attach a script that implements the methods below. Name the script e.g. `FlutterUnityBridge.cs`.                                                      |
| **Method: rotation speed**     | `public void SetRotationSpeed(string message)` — `message` is a number string (e.g. `"50"`). Use it to set a rotation speed (e.g. on a cube or logo). |
| **Method: custom text**        | `public void OnMessageFromFlutter(string message)` — `message` is arbitrary text from Flutter. Log it and/or react in the scene.                      |
| **Method: JSON** (optional)    | `public void OnJsonFromFlutter(string message)` — `message` is a JSON string. Parse and use as needed.                                                |

## Unity → Flutter: when to send

1. **Scene ready**  
   As soon as your scene is fully loaded and the bridge is ready to receive calls, send this exact string to Flutter once:  
   `scene_loaded`  
   Flutter uses this to show the Unity view and enable the “Send to Unity” controls.

2. **Other events**  
   Whenever you want to notify Flutter (e.g. button click in Unity, game event), call the plugin’s send API with a string, e.g. `SendToFlutter.Send("your_message")`. Flutter will display these in a list.

## Technical notes

- The Flutter app calls:  
  `SendToUnity("FlutterUnityBridge", "MethodName", "message")`  
  So the GameObject must be named **FlutterUnityBridge** and the script must have **public void** methods that take a **single string**.
- Use the correct namespace/API for **SendToFlutter** (or equivalent) for Unity 6000.0 with flutter_embed_unity. If the plugin provides a prefab or scene with a bridge, you can extend or replace it as long as the GameObject name and method names above are kept.
- Keep the main scene simple: one persistent GameObject `FlutterUnityBridge` with the bridge script, plus any visuals you need (e.g. a rotating cube for `SetRotationSpeed`).

## Summary checklist

- [ ] Create or use a GameObject named **FlutterUnityBridge** in the main scene.
- [ ] Add a script with:
  - [ ] `public void SetRotationSpeed(string message)`
  - [ ] `public void OnMessageFromFlutter(string message)`
  - [ ] (Optional) `public void OnJsonFromFlutter(string message)`
- [ ] On scene ready, send **exactly** `scene_loaded` to Flutter once.
- [ ] Use the plugin’s API to send other strings to Flutter when needed (e.g. `SendToFlutter.Send(...)`).

After implementation, the Flutter app will be able to send rotation values and custom text to Unity, and Unity will signal when it’s ready and can send events back to Flutter.
