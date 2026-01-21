# Unity Embed for Flutter (TBA)

This repository contains a Unity project configured to be embedded within a Flutter application. It includes scripts for exporting the Unity project as a library for Android and iOS, and utilities for communication between Unity and Flutter.

**Status: To Be Announced (TBA)**

## File Structure

The project follows a standard Unity project structure with specific additions for Flutter integration:

```
get-gains-unity-embed/
├── Assets/
│   ├── FlutterEmbed/              # Core integration files
│   │   ├── Editor/                # Editor scripts for exporting the project
│   │   │   ├── ProjectExporter.cs # Logic to export to Android/iOS
│   │   │   └── ...
│   │   └── SendToFlutter/         # Run-time scripts for communication
│   │       └── SendToFlutter.cs   # Static class to send messages to Flutter
│   ├── Scenes/                    # Unity Scenes
│   │   └── SampleScene.unity      # Example scene aimed for embedding
│   └── Settings/                  # Rendering and build settings
├── Library/                       # Unity generated library files
├── Packages/                      # Unity Package Manager manifest
└── ProjectSettings/               # Project configuration (Build, Player, etc.)
```

### Key Components

- **`Assets/FlutterEmbed/Editor/`**: Contains C# scripts that add menu items and logic to export this Unity project into a format that can be consumed by the Flutter Unity widget.
- **`Assets/FlutterEmbed/SendToFlutter/SendToFlutter.cs`**: A utility script providing a `Send(string data)` method. This handles the platform-specific interop (JNI for Android, P/Invoke for iOS) to pass data back to the hosting Flutter app.

## Usage for a Flutter Project

To use this Unity project within a Flutter application:

1.  **Unity Setup**:
    - Open this project in the supported Unity version (check `ProjectSettings/ProjectVersion.txt`).
    - Ensure the build target is set appropriately (Android or iOS).

2.  **Exporting**:
    - Use the custom menu items under **Flutter Embed** to export the project (e.g., `Flutter Embed > Export project to flutter app (Android)`).
    - The export process generates the necessary `.aar` (Android) or framework (iOS) files.

3.  **Flutter Integration**:
    - In your Flutter project, ensure you use a compatible Unity widget package (e.g., `flutter_unity_widget` or similar).
    - Place the exported Unity build files into the appropriate native directories of your Flutter project (`android/unityLibrary`, `ios/UnityLibrary`, etc.).

4.  **Communication**:
    - **Unity to Flutter**: call `SendToFlutter.Send("your_message")` from your Unity scripts.
    - **Flutter to Unity**: Use the Flutter widget's controller to send messages to Unity GameObjects.

## Roadmap (TBA)

- [ ] finalize export pipeline definition.
- [ ] document specific message protocols.
- [ ] add example flutter project usage.
