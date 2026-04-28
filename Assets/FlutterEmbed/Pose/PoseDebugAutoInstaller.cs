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

        // Start with stable defaults: X inverted for 2D→3D, Z off unless a prefab set it.
        driver.InvertLandmarkX = true;
        driver.InvertLandmarkZ = false;
        driver.TorsoDebugFlatten = PoseLandmarkMapping.TorsoDebugFlattenMode.UniformZ;
        driver.DebugInvertLegDepthZ = true;
        var fig = Object.FindAnyObjectByType<PoseStickFigureRenderer>(FindObjectsInactive.Exclude);
        if (fig != null)
        {
            fig.SetLandmarkInversion(driver.InvertLandmarkX, driver.InvertLandmarkZ);
            fig.TorsoDebugFlatten = driver.TorsoDebugFlatten;
            fig.SetDebugInvertLegDepthZ(driver.DebugInvertLegDepthZ);
        }

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

