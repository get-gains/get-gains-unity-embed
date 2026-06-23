using System.Collections.Generic;
using FlutterEmbed;
using UnityEngine;

/// <summary>
/// Bridge script for Flutter-Unity messaging. Sends "scene_loaded" to Flutter when ready.
/// Implements pose: LoadPoseFrames, PlayPose, PausePose, SeekPoseFrame, SetSkeletonColor, SetCameraAngle.
/// Implements cosmetics: LoadEquippedCosmetics, PreviewCosmetic, ClearPreview (delegates to CosmeticManager).
/// Camera is managed by OrbitCameraController (touch orbit + pinch zoom via New Input System).
/// If a HumanoidPoseDriver exists in the scene, it drives the humanoid rig and hides the stick figure.
/// </summary>
public class FlutterUnityBridge : MonoBehaviour
{
    [SerializeField] private Transform rotatableTarget;
    [SerializeField] private PosePlaybackController posePlaybackController;
    [SerializeField] private Camera poseCamera;
    [SerializeField] private CosmeticManager cosmeticManager;
    [SerializeField] private PoseComparisonController poseComparisonController;

    private OrbitCameraController _orbitController;
    private string _lastRecordingAngleHint = "";
    private float rotationSpeed;
    private bool sceneLoadedSent;
    private CameraViewMode _currentViewMode = CameraViewMode.Workout;

    private void Awake()
    {
        if (poseCamera == null)
            poseCamera = Camera.main;
        SendSceneLoadedOnce();
    }

    private void EnsurePosePlayback()
    {
        if (posePlaybackController != null) return;

        var poseGo = new GameObject("PosePlayback");
        poseGo.transform.SetParent(transform);
        poseGo.transform.localPosition = Vector3.zero;

        var rendererGo = new GameObject("PoseStickFigure");
        rendererGo.transform.SetParent(poseGo.transform);
        rendererGo.transform.localPosition = Vector3.zero;
        var renderer = rendererGo.AddComponent<PoseStickFigureRenderer>();

        posePlaybackController = poseGo.AddComponent<PosePlaybackController>();
        posePlaybackController.SetRenderer(renderer);

        var humanoid = HumanoidPoseDriver.FindBestDriveableDriver();
        if (humanoid != null)
        {
            posePlaybackController.SetHumanoidDriver(humanoid);
            Debug.Log($"[FlutterUnityBridge] Found humanoid rig: {humanoid.gameObject.name}");

            // Ensure a PoseDebugMenuSimple exists so debug controls are always available in the embedded scene.
            var debugMenu = humanoid.GetComponent<PoseDebugMenuSimple>();
            if (debugMenu == null)
            {
                debugMenu = humanoid.gameObject.AddComponent<PoseDebugMenuSimple>();
                // Debug menu hidden by default; inference handles depth signs automatically.
                var startVisibleField = typeof(PoseDebugMenuSimple).GetField("startVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (startVisibleField != null) startVisibleField.SetValue(debugMenu, false);
                var driverField = typeof(PoseDebugMenuSimple).GetField("driver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (driverField != null) driverField.SetValue(debugMenu, humanoid);
            }
        }
        if (preferStickFigureOnly && posePlaybackController != null)
            posePlaybackController.SetUseStickFigureOnly(true);
    }

    [Tooltip("When true, always show stick figure instead of humanoid (reliable fallback if humanoid deforms).")]
    [SerializeField] private bool preferStickFigureOnly = false;

    private OrbitCameraController EnsureOrbitController()
    {
        if (_orbitController != null) return _orbitController;
        if (poseCamera == null) return null;

        _orbitController = poseCamera.GetComponent<OrbitCameraController>();
        if (_orbitController == null)
            _orbitController = poseCamera.gameObject.AddComponent<OrbitCameraController>();
        _orbitController.SetCamera(poseCamera);
        return _orbitController;
    }

    private PoseComparisonController EnsureComparisonController()
    {
        if (poseComparisonController != null) return poseComparisonController;
        poseComparisonController = GetComponent<PoseComparisonController>();
        if (poseComparisonController == null)
            poseComparisonController = gameObject.AddComponent<PoseComparisonController>();
        return poseComparisonController;
    }

    private void Start()
    {
        SendSceneLoadedOnce();
    }

    private bool _firstUpdate = true;
    private void Update()
    {
        if (_firstUpdate)
        {
            _firstUpdate = false;
            SendSceneLoadedOnce();
        }
        if (rotatableTarget != null && rotatableTarget.gameObject.activeInHierarchy && rotationSpeed != 0f)
            rotatableTarget.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);

        UpdateOrbitTarget();
    }

    private void UpdateOrbitTarget()
    {
        if (_orbitController == null) return;

        // In Cosmetic mode the target is set once by FrameCameraCosmetic and must
        // not be overwritten every frame — doing so locks the camera to the hips
        // and prevents vertical pan (the user's drag snaps back immediately).
        if (_currentViewMode == CameraViewMode.Cosmetic) return;

        // Prefer humanoid driver for tracking when present (stick figure is hidden).
        var humanoid = GetHumanoidDriver();
        if (humanoid != null && humanoid.FigureHeight > 0.1f)
        {
            _orbitController.UpdateTarget(humanoid.HipsWorldPosition);
            return;
        }

        var renderer = GetFigureRenderer();
        if (renderer != null && renderer.FigureHeight > 0.1f)
            _orbitController.UpdateTarget(renderer.FigureCenter);
    }

    public void SetRotationSpeed(string message)
    {
        if (float.TryParse(message, out float speed))
            rotationSpeed = speed;
    }

    public void OnMessageFromFlutter(string message)
    {
        Debug.Log($"[FlutterUnityBridge] OnMessageFromFlutter: {message}");
    }

    public void OnJsonFromFlutter(string message)
    {
        Debug.Log($"[FlutterUnityBridge] OnJsonFromFlutter: {message}");
    }

    // ── Pose / Skeleton ──

    public void LoadPoseFrames(string message)
    {
        if (!PoseDataParser.TryParse(message, out PosePayload payload))
        {
            Debug.LogWarning("[FlutterUnityBridge] LoadPoseFrames: failed to parse JSON");
            return;
        }
        EnsurePosePlayback();
        if (posePlaybackController != null)
        {
            posePlaybackController.ResolveHumanoidDriver();
            if (preferStickFigureOnly)
                posePlaybackController.SetUseStickFigureOnly(true);

            // Reset the rig to bind pose before applying a new recording so limbs
            // that aren't present in the new landmarks (e.g. legs) don't stay frozen
            // in the previous form's position.
            var humanoid = posePlaybackController.ActiveHumanoidDriver;
            humanoid?.ResetPose();

            posePlaybackController.LoadFrames(payload.Frames, payload.Fps, payload.Loop);

            // Forward the recorded camera angle to the retargeter for stable 45° yaw.
            if (!string.IsNullOrEmpty(_lastRecordingAngleHint))
            {
                posePlaybackController.ResolveHumanoidDriver();
                posePlaybackController.ActiveHumanoidDriver?.SetRecordingAngleHint(_lastRecordingAngleHint);
            }

            Debug.Log($"[FlutterUnityBridge] LoadPoseFrames: {payload.Frames?.Count ?? 0} frames, fps={payload.Fps}, loop={payload.Loop}");
            FrameCameraToFigure();
        }
    }

    /// <summary>
    /// Flutter → Unity: snap the humanoid back to its initial bind pose.
    /// Useful as a manual reset switch between recordings.
    /// </summary>
    public void ResetPose(string message)
    {
        EnsurePosePlayback();
        posePlaybackController?.ResolveHumanoidDriver();
        var humanoid = posePlaybackController?.ActiveHumanoidDriver;
        humanoid?.ResetPose();
        Debug.Log("[FlutterUnityBridge] ResetPose");
    }

    public void PlayPose(string message)
    {
        if (posePlaybackController != null) posePlaybackController.Play();
    }

    public void PausePose(string message)
    {
        if (posePlaybackController != null) posePlaybackController.Pause();
    }

    public void SeekPoseFrame(string message)
    {
        if (posePlaybackController != null && int.TryParse(message?.Trim(), out int index))
            posePlaybackController.SeekToFrame(index);
    }

    public void SetSkeletonColor(string message)
    {
        if (posePlaybackController != null && ColorUtility.TryParseHtmlString(message?.Trim(), out Color color))
        {
            posePlaybackController.SetSkeletonColor(color);
            // Tint the active humanoid model as well so the avatar matches the skeleton color.
            var driver = posePlaybackController.ActiveHumanoidDriver;
            if (driver != null)
            {
                var applier = driver.GetComponent<AvatarMaterialApplier>();
                if (applier != null) applier.SetTint(color);
            }
        }
    }

    /// <summary>
    /// Flutter → Unity: switch camera view mode.
    /// Message: "WORKOUT" | "COSMETIC"
    /// Workout  — full-figure framing, wide orbit limits (default).
    /// Cosmetic — upper-body close-up, front-facing, tight zoom limit for accessory inspection.
    /// </summary>
    public void SetCameraViewMode(string message)
    {
        string raw = (message ?? "WORKOUT").Trim().ToUpperInvariant();
        _currentViewMode = raw == "COSMETIC" ? CameraViewMode.Cosmetic : CameraViewMode.Workout;

        var orbit = EnsureOrbitController();
        if (orbit != null)
            orbit.SetViewMode(_currentViewMode);

        // Reduce frame rate in Cosmetic mode (wardrobe is static inspection, not live pose).
        Application.targetFrameRate = _currentViewMode == CameraViewMode.Cosmetic ? 30 : 60;

        // Show / hide pose-debug overlay so it can't block touch gestures in wardrobe.
        var debugMenu = FindAnyObjectByType<PoseDebugMenuSimple>();
        if (debugMenu != null)
            debugMenu.SetCosmeticMode(_currentViewMode == CameraViewMode.Cosmetic);

        Debug.Log($"[FlutterUnityBridge] SetCameraViewMode: {_currentViewMode}, targetFrameRate={Application.targetFrameRate}");
    }

    /// <summary>
    /// Flutter → Unity: re-apply Cosmetic framing (restores close-up after user pans away).
    /// Message: ignored.
    /// </summary>
    public void ResetCosmeticView(string _)
    {
        if (_currentViewMode == CameraViewMode.Cosmetic)
            FrameCameraCosmetic();
        Debug.Log("[FlutterUnityBridge] ResetCosmeticView");
    }

    public void SetCameraAngle(string message)
    {
        var orbit = EnsureOrbitController();
        if (orbit != null)
            orbit.SetAnglePreset(message);
        else if (poseCamera != null)
            ApplyCameraAngleDirect(message);
    }

    /// <summary>
    /// Debug / tuning for pose retargeting. Message: JSON from Flutter, e.g.
    /// {"swapArmLandmarks":true,"forceShowStickFigure":true,"invertArmDepthZ":false,"invertHeadDepthZ":true,"invertLegDepthZ":true}
    /// </summary>
    public void SetPoseDebugOptions(string message)
    {
        EnsurePosePlayback();
        if (posePlaybackController == null) return;

        var json = string.IsNullOrWhiteSpace(message) ? "{}" : message.Trim();
        var opts = JsonUtility.FromJson<PoseDebugOptionsJson>(json);
        posePlaybackController.ResolveHumanoidDriver();
        var humanoid = posePlaybackController.ActiveHumanoidDriver;
        if (humanoid != null)
            humanoid.SetDebugSwapArmLandmarks(opts.swapArmLandmarks);
        posePlaybackController.SetDebugForceStickFigure(opts.forceShowStickFigure);
        posePlaybackController.SetDebugInvertArmDepthZ(opts.invertArmDepthZ);
        posePlaybackController.SetDebugInvertHeadDepthZ(opts.invertHeadDepthZ);
        posePlaybackController.SetDebugInvertLegDepthZ(opts.invertLegDepthZ);
        Debug.Log(
            $"[FlutterUnityBridge] SetPoseDebugOptions swapArmLandmarks={opts.swapArmLandmarks} forceShowStickFigure={opts.forceShowStickFigure} invertArmDepthZ={opts.invertArmDepthZ} invertHeadDepthZ={opts.invertHeadDepthZ} invertLegDepthZ={opts.invertLegDepthZ}");
    }

    /// <summary>
    /// Flutter → Unity: tell the pose retargeter the recorded camera angle
    /// (FRONT, ANGLE_45_LEFT, ANGLE_45_RIGHT, etc.) so body yaw stays stable.
    /// </summary>
    public void SetRecordingAngleHint(string message)
    {
        _lastRecordingAngleHint = (message ?? "").Trim().ToUpperInvariant();

        EnsurePosePlayback();
        if (posePlaybackController != null)
        {
            posePlaybackController.ResolveHumanoidDriver();
            var humanoid = posePlaybackController.ActiveHumanoidDriver;
            humanoid?.SetRecordingAngleHint(_lastRecordingAngleHint);
        }

        var comparison = EnsureComparisonController();
        comparison?.SetRecordingAngleHint(_lastRecordingAngleHint);

        Debug.Log($"[FlutterUnityBridge] SetRecordingAngleHint: {_lastRecordingAngleHint}");
    }

    /// <summary>
    /// Flutter → Unity: enter side-by-side comparison mode.
    /// Message: JSON with { referenceFrames: [...], clientFrames: [...], fps: 15, loop: true }
    /// Loads coach reference into the left (green) model and client into the right (orange) model.
    /// </summary>
    public void EnterComparisonMode(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Debug.LogWarning("[FlutterUnityBridge] EnterComparisonMode: empty payload");
            return;
        }

        if (!PoseComparisonPayload.TryParse(message, out PoseComparisonPayloadData payload))
        {
            Debug.LogWarning("[FlutterUnityBridge] EnterComparisonMode: failed to parse JSON");
            return;
        }

        EnsurePosePlayback();
        if (posePlaybackController != null)
        {
            posePlaybackController.Pause();
            var renderer = posePlaybackController.GetComponentInChildren<PoseStickFigureRenderer>();
            if (renderer != null) renderer.SetVisible(false);
        }

        var comparison = EnsureComparisonController();
        comparison?.EnterComparisonMode(
            payload.ReferenceFrames,
            payload.ClientFrames,
            payload.Fps,
            poseCamera,
            EnsureOrbitController());

        Debug.Log($"[FlutterUnityBridge] EnterComparisonMode: ref={payload.ReferenceFrames?.Count ?? 0}, client={payload.ClientFrames?.Count ?? 0}");
        SendToFlutter.Send("comparison_entered");
    }

    /// <summary>
    /// Flutter → Unity: exit side-by-side comparison mode and return to single-avatar playback.
    /// </summary>
    public void ExitComparisonMode(string message)
    {
        var comparison = EnsureComparisonController();
        comparison?.ExitComparisonMode();

        if (posePlaybackController != null)
        {
            var renderer = posePlaybackController.GetComponentInChildren<PoseStickFigureRenderer>();
            if (renderer != null) renderer.SetVisible(true);
            posePlaybackController.Play();
        }

        FrameCameraToFigure();
        Debug.Log("[FlutterUnityBridge] ExitComparisonMode");
    }

    /// <summary>
    /// Cosmetic framing: shift orbit target up to the upper-body/chest area and apply
    /// Cosmetic view-mode distance/angle so headwear and accessories fill the viewport.
    /// Safe to call even before pose frames are loaded — uses humanoid rig pose or
    /// falls back to a default height when the rig has not been driven yet.
    /// </summary>
    private void FrameCameraCosmetic()
    {
        Vector3 hipsCenter = Vector3.zero;
        float   figHeight  = 1.8f; // sensible default if rig not yet driven

        var humanoid = GetHumanoidDriver();
        if (humanoid != null && humanoid.FigureHeight > 0.1f)
        {
            hipsCenter = humanoid.HipsWorldPosition;
            figHeight  = humanoid.FigureHeight;
        }
        else
        {
            var renderer = GetFigureRenderer();
            if (renderer != null && renderer.FigureHeight > 0.1f)
            {
                hipsCenter = renderer.FigureCenter;
                figHeight  = renderer.FigureHeight;
            }
        }

        // Bias the look-at point toward the upper chest so the face and head
        // accessories are centred in the viewport at cosmetic close-up distance.
        Vector3 cosmeticCenter = hipsCenter + Vector3.up * (figHeight * 0.35f);

        var orbit = EnsureOrbitController();
        if (orbit == null) return;

        orbit.SetViewMode(CameraViewMode.Cosmetic);
        orbit.SetTarget(cosmeticCenter, figHeight);
        Debug.Log($"[FlutterUnityBridge] FrameCameraCosmetic: center={cosmeticCenter}, height={figHeight:F2}");
    }

    private void FrameCameraToFigure()
    {
        Vector3 center = Vector3.zero;
        float height = 0f;

        var humanoid = GetHumanoidDriver();
        if (humanoid != null && humanoid.FigureHeight > 0.1f)
        {
            center = humanoid.HipsWorldPosition;
            height = humanoid.FigureHeight;
        }
        else
        {
            var renderer = GetFigureRenderer();
            if (renderer != null && renderer.FigureHeight > 0.1f)
            {
                center = renderer.FigureCenter;
                height = renderer.FigureHeight;
            }
        }

        if (height < 0.1f) return;
        var orbit = EnsureOrbitController();
        if (orbit != null)
            orbit.SetTarget(center, height);
    }

    private void ApplyCameraAngleDirect(string message)
    {
        string angle = (message ?? "").Trim().ToUpperInvariant();
        Vector3 target = Vector3.zero;
        float figHeight = 3f;

        var renderer = GetFigureRenderer();
        if (renderer != null && renderer.FigureHeight > 0.1f)
        {
            target = renderer.FigureCenter;
            figHeight = renderer.FigureHeight;
        }

        float distance = (figHeight * 0.7f) / Mathf.Tan(poseCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        distance = Mathf.Max(distance, 2f);

        Vector3 offset;
        switch (angle)
        {
            case "FRONT":          offset = new Vector3(0, 0, -distance); break;
            case "SIDE_LEFT":      offset = new Vector3(-distance, 0, 0); break;
            case "SIDE_RIGHT":     offset = new Vector3(distance, 0, 0); break;
            case "REAR":           offset = new Vector3(0, 0, distance); break;
            case "ANGLE_45_LEFT":  offset = Quaternion.Euler(0, 45, 0) * new Vector3(0, 0, -distance); break;
            case "ANGLE_45_RIGHT": offset = Quaternion.Euler(0, -45, 0) * new Vector3(0, 0, -distance); break;
            case "DIAGONAL":
            default:               offset = Quaternion.Euler(15, -135, 0) * new Vector3(0, 0, -distance); break;
        }

        poseCamera.transform.position = target + offset;
        poseCamera.transform.LookAt(target);
    }

    private PoseStickFigureRenderer GetFigureRenderer()
    {
        if (posePlaybackController == null) return null;
        return posePlaybackController.GetComponentInChildren<PoseStickFigureRenderer>();
    }

    private HumanoidPoseDriver GetHumanoidDriver()
    {
        if (posePlaybackController == null || preferStickFigureOnly) return null;
        posePlaybackController.ResolveHumanoidDriver();
        return posePlaybackController.ActiveHumanoidDriver;
    }

    // ── Cosmetics ──

    private CosmeticManager EnsureCosmeticManager()
    {
        if (cosmeticManager != null) return cosmeticManager;

        cosmeticManager = FindAnyObjectByType<CosmeticManager>();
        if (cosmeticManager != null)
        {
            Debug.Log($"[FlutterUnityBridge] Found CosmeticManager on: {cosmeticManager.gameObject.name}");
        }
        else
        {
            Debug.LogWarning("[FlutterUnityBridge] No CosmeticManager found in scene. Cosmetic commands will be ignored.");
        }
        return cosmeticManager;
    }

    public void LoadEquippedCosmetics(string message)
    {
        Debug.Log($"[FlutterUnityBridge] LoadEquippedCosmetics: viewMode={_currentViewMode}, payload={message}");
        var mgr = EnsureCosmeticManager();
        if (mgr != null)
        {
            mgr.LoadCosmetics(message);
        }
        else
        {
            Debug.LogWarning("[FlutterUnityBridge] LoadEquippedCosmetics: no CosmeticManager available.");
            SendToFlutter.Send("cosmetics_loaded");
        }

        // Reframe camera when in Cosmetic mode so accessories are visible
        // without requiring the user to pinch-zoom.
        if (_currentViewMode == CameraViewMode.Cosmetic)
            FrameCameraCosmetic();
    }

    public void PreviewCosmetic(string message)
    {
        var mgr = EnsureCosmeticManager();
        if (mgr != null)
        {
            mgr.PreviewCosmetic(message);
        }
        else
        {
            Debug.LogWarning("[FlutterUnityBridge] PreviewCosmetic: no CosmeticManager available.");
        }
    }

    public void ClearPreview(string message)
    {
        var mgr = EnsureCosmeticManager();
        if (mgr != null)
        {
            mgr.ClearPreview();
        }
        else
        {
            Debug.LogWarning("[FlutterUnityBridge] ClearPreview: no CosmeticManager available.");
            SendToFlutter.Send("cosmetics_loaded");
        }
    }

    public void SendToFlutterMessage(string message)
    {
        SendToFlutter.Send(message);
    }

    private void SendSceneLoadedOnce()
    {
        if (sceneLoadedSent) return;
        sceneLoadedSent = true;
        SendToFlutter.Send("scene_loaded");
        StartCoroutine(SendSceneLoadedRetryOnce());
    }

    private System.Collections.IEnumerator SendSceneLoadedRetryOnce()
    {
        yield return null;
        SendToFlutter.Send("scene_loaded");
    }
}

[System.Serializable]
public class PoseDebugOptionsJson
{
    public bool swapArmLandmarks;
    public bool forceShowStickFigure;
    public bool invertArmDepthZ;
    public bool invertHeadDepthZ;
    public bool invertLegDepthZ;
}

/// <summary>
/// Lightweight parser for the EnterComparisonMode JSON payload.
/// Reuses the existing PoseDataParser frame format for both reference and client arrays.
/// </summary>
public static class PoseComparisonPayload
{
    [System.Serializable]
    private class Envelope
    {
        public string referenceFrames;
        public string clientFrames;
        public int fps;
        public bool loop;
    }

    public static bool TryParse(string json, out PoseComparisonPayloadData payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var env = JsonUtility.FromJson<Envelope>(json);
            if (env == null) return false;

            int fps = env.fps > 0 ? env.fps : 15;

            var reference = new List<PoseFrame>();
            var client = new List<PoseFrame>();

            if (!string.IsNullOrWhiteSpace(env.referenceFrames))
            {
                var refPayload = $"{{\"frames\":{env.referenceFrames},\"fps\":{fps},\"loop\":{env.loop.ToString().ToLowerInvariant()}}}";
                if (PoseDataParser.TryParse(refPayload, out PosePayload rp))
                    reference = rp.Frames;
            }

            if (!string.IsNullOrWhiteSpace(env.clientFrames))
            {
                var clientPayload = $"{{\"frames\":{env.clientFrames},\"fps\":{fps},\"loop\":{env.loop.ToString().ToLowerInvariant()}}}";
                if (PoseDataParser.TryParse(clientPayload, out PosePayload cp))
                    client = cp.Frames;
            }

            payload = new PoseComparisonPayloadData
            {
                ReferenceFrames = reference,
                ClientFrames = client,
                Fps = fps,
                Loop = env.loop,
            };
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[PoseComparisonPayload] Parse error: {e.Message}");
            return false;
        }
    }
}

public class PoseComparisonPayloadData
{
    public List<PoseFrame> ReferenceFrames;
    public List<PoseFrame> ClientFrames;
    public int Fps;
    public bool Loop;
}
