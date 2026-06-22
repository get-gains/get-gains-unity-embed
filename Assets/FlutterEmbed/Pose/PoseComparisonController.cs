using System.Collections.Generic;
using UnityEngine;
using FlutterEmbed;

/// <summary>
/// Manages side-by-side 3D form comparison in the same Unity scene.
/// Activates a second mannequin, loads reference frames into the coach (left)
/// and client frames into the right model, tints them green/orange, and
/// reframes the camera so both figures are visible.
/// </summary>
public class PoseComparisonController : MonoBehaviour
{
    [Header("Rigs")]
    [SerializeField] private GameObject coachMannequin;
    [SerializeField] private GameObject clientMannequin;

    [Header("Colors")]
    [SerializeField] private Color coachColor = new Color(0f, 0.78f, 0.34f);   // green
    [SerializeField] private Color clientColor = new Color(1f, 0.55f, 0f);    // orange

    [Header("Layout")]
    [SerializeField] private float coachX = -1.2f;
    [SerializeField] private float clientX = 1.2f;
    [SerializeField] private float figureSpacing = 2.4f;

    private PosePlaybackController _coachPlayback;
    private PosePlaybackController _clientPlayback;
    private HumanoidPoseDriver _coachDriver;
    private HumanoidPoseDriver _clientDriver;
    private AvatarMaterialApplier _coachMaterial;
    private AvatarMaterialApplier _clientMaterial;
    private bool _isInComparisonMode;

    public bool IsInComparisonMode => _isInComparisonMode;

    /// <summary>
    /// Enter side-by-side comparison mode.
    /// </summary>
    /// <param name="referenceFrames">Coach reference frames.</param>
    /// <param name="clientFrames">Client recorded frames.</param>
    /// <param name="fps">Playback frames per second.</param>
    /// <param name="camera">Camera to reframe.</param>
    /// <param name="orbit">Orbit controller for framing.</param>
    public void EnterComparisonMode(
        List<PoseFrame> referenceFrames,
        List<PoseFrame> clientFrames,
        int fps,
        Camera camera,
        OrbitCameraController orbit)
    {
        if (coachMannequin == null || clientMannequin == null)
        {
            Debug.LogWarning("[PoseComparisonController] Coach or client mannequin not assigned.");
            return;
        }

        // Activate the client first so its Animator/HumanoidPoseDriver can initialize.
        clientMannequin.SetActive(true);

        // Position models side by side.
        coachMannequin.transform.position = new Vector3(coachX, 0f, 0f);
        clientMannequin.transform.position = new Vector3(clientX, 0f, 0f);

        ResolveDrivers();

        // Apply colors.
        ApplyTint(coachMannequin, coachColor);
        ApplyTint(clientMannequin, clientColor);

        EnsurePlaybacks();

        _coachPlayback.SetSkeletonColor(coachColor);
        _clientPlayback.SetSkeletonColor(clientColor);

        _coachPlayback.LoadFrames(referenceFrames, fps, true);
        _clientPlayback.LoadFrames(clientFrames, fps, true);

        Debug.Log($"[PoseComparisonController] Loaded frames: ref={referenceFrames?.Count ?? 0}, client={clientFrames?.Count ?? 0}. Playing...");

        _coachPlayback.Play();
        _clientPlayback.Play();

        Debug.Log($"[PoseComparisonController] Play states: coach={_coachPlayback.IsPlaying}, client={_clientPlayback.IsPlaying}");

        _isInComparisonMode = true;

        FrameBothFigures(camera, orbit);

        Debug.Log($"[PoseComparisonController] Entered comparison mode: ref={referenceFrames?.Count ?? 0}, client={clientFrames?.Count ?? 0}");
    }

    /// <summary>
    /// Exit comparison mode: hide the client mannequin, pause both playbacks,
    /// reset the coach mannequin to the origin and remove its tint so normal
    /// form viewing shows the original avatar color.
    /// </summary>
    public void ExitComparisonMode()
    {
        _isInComparisonMode = false;
        if (_coachPlayback != null) _coachPlayback.Pause();
        if (_clientPlayback != null) _clientPlayback.Pause();

        if (clientMannequin != null) clientMannequin.SetActive(false);

        if (coachMannequin != null)
        {
            coachMannequin.transform.position = Vector3.zero;
            // Reset coach tint to the default orange used for normal form viewing.
            ApplyTint(coachMannequin, clientColor);
        }

        Debug.Log("[PoseComparisonController] Exited comparison mode.");
    }

    /// <summary>
    /// Set the recording angle hint on both drivers so 45° recordings align correctly.
    /// </summary>
    public void SetRecordingAngleHint(string angle)
    {
        if (_coachDriver != null) _coachDriver.SetRecordingAngleHint(angle);
        if (_clientDriver != null) _clientDriver.SetRecordingAngleHint(angle);
    }

    private void ResolveDrivers()
    {
        if (_coachDriver == null && coachMannequin != null)
            _coachDriver = coachMannequin.GetComponent<HumanoidPoseDriver>();
        if (_clientDriver == null && clientMannequin != null)
            _clientDriver = clientMannequin.GetComponent<HumanoidPoseDriver>();

        if (_coachDriver != null) _coachDriver.TryInitialize();
        if (_clientDriver != null) _clientDriver.TryInitialize();
    }

    private void EnsurePlaybacks()
    {
        if (_coachPlayback == null)
        {
            _coachPlayback = CreatePlaybackForDriver(_coachDriver, "PosePlayback_Coach");
        }
        if (_clientPlayback == null)
        {
            _clientPlayback = CreatePlaybackForDriver(_clientDriver, "PosePlayback_Client");
        }
    }

    private PosePlaybackController CreatePlaybackForDriver(HumanoidPoseDriver driver, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.zero;

        var rendererGo = new GameObject("PoseStickFigure");
        rendererGo.transform.SetParent(go.transform);
        rendererGo.transform.localPosition = Vector3.zero;
        var renderer = rendererGo.AddComponent<PoseStickFigureRenderer>();

        var playback = go.AddComponent<PosePlaybackController>();
        playback.SetRenderer(renderer);
        if (driver != null)
            playback.SetHumanoidDriver(driver);
        return playback;
    }

    private void ApplyTint(GameObject mannequin, Color color)
    {
        if (mannequin == null) return;
        var applier = mannequin.GetComponent<AvatarMaterialApplier>();
        if (applier != null)
        {
            applier.SetTint(color);
            return;
        }

        // Fallback: tint every renderer directly if no applier is present.
        foreach (var r in mannequin.GetComponentsInChildren<Renderer>(true))
        {
            // Skip cosmetic items so hats/glasses keep their own look.
            if (IsCosmeticRenderer(r.transform)) continue;
            if (r.sharedMaterial != null)
            {
                var instance = new Material(r.sharedMaterial);
                instance.color = color;
                if (instance.HasProperty("_BaseColor")) instance.SetColor("_BaseColor", color);
                if (instance.HasProperty("_Color")) instance.SetColor("_Color", color);
                r.sharedMaterial = instance;
            }
        }
    }

    private static bool IsCosmeticRenderer(Transform t)
    {
        while (t != null)
        {
            if (t.GetComponent<CosmeticAttachment>() != null) return true;
            t = t.parent;
        }
        return false;
    }

    private void FrameBothFigures(Camera camera, OrbitCameraController orbit)
    {
        if (camera == null) return;

        float coachH = _coachDriver != null ? _coachDriver.FigureHeight : 1.8f;
        float clientH = _clientDriver != null ? _clientDriver.FigureHeight : 1.8f;
        float maxHeight = Mathf.Max(coachH, clientH, 1f);

        Vector3 center = new Vector3((coachX + clientX) * 0.5f, maxHeight * 0.45f, 0f);

        if (orbit != null)
        {
            orbit.SetViewMode(CameraViewMode.Workout);
            orbit.SetTarget(center, maxHeight);
            orbit.SetAnglePreset("FRONT");
        }
        else
        {
            float distance = Mathf.Max(figureSpacing * 1.2f, (maxHeight * 0.9f) / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            camera.transform.position = center + new Vector3(0f, maxHeight * 0.1f, -distance);
            camera.transform.LookAt(center);
        }
    }
}
