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
        var driver = HumanoidPoseDriver.FindBestDriveableDriver();
        if (driver == null) return;

        // Start with stable defaults: X inverted for 2D→3D.
        // Z-sign inference replaces the old hardcoded invert flags (Fixes 2, 5).
        driver.InvertLandmarkX = true;
        driver.InvertLandmarkZ = false;
        driver.TorsoDebugFlatten = PoseLandmarkMapping.TorsoDebugFlattenMode.None;
        var fig = Object.FindAnyObjectByType<PoseStickFigureRenderer>(FindObjectsInactive.Exclude);
        if (fig != null)
        {
            fig.SetLandmarkInversion(driver.InvertLandmarkX, driver.InvertLandmarkZ);
            fig.TorsoDebugFlatten = driver.TorsoDebugFlatten;
        }

        var menu = driver.GetComponent<PoseDebugMenuSimple>();
        if (menu == null)
        {
            menu = driver.gameObject.AddComponent<PoseDebugMenuSimple>();

            // Debug menu hidden by default; inference handles depth signs automatically.
            var field = typeof(PoseDebugMenuSimple).GetField("startVisible",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null) field.SetValue(menu, false);
        }
    }
}

