using UnityEngine;

/// <summary>
/// Ensures a PoseDebugMenuSimple exists next to any HumanoidPoseDriver in the scene,
/// both in the Unity editor (Play mode) and in builds.
/// No manual setup required beyond having a HumanoidPoseDriver in the scene.
/// </summary>
public static class PoseDebugAutoInstaller
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallDebugMenu()
    {
        var driver = Object.FindAnyObjectByType<HumanoidPoseDriver>();
        if (driver == null) return;

        var menu = driver.GetComponent<PoseDebugMenuSimple>();
        if (menu == null)
        {
            menu = driver.gameObject.AddComponent<PoseDebugMenuSimple>();

            // Force startVisible = true so window shows immediately; user can hide via bottom bar.
            var field = typeof(PoseDebugMenuSimple).GetField("startVisible",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null) field.SetValue(menu, true);
        }
    }
}

