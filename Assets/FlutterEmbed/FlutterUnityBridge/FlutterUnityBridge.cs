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

    private OrbitCameraController _orbitController;
    private float rotationSpeed;
    private bool sceneLoadedSent;

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

        // Look for a HumanoidPoseDriver anywhere in the scene (e.g. on HumanBasemesh).
        var humanoid = FindAnyObjectByType<HumanoidPoseDriver>();
        if (humanoid != null)
        {
            posePlaybackController.SetHumanoidDriver(humanoid);
            Debug.Log($"[FlutterUnityBridge] Found humanoid rig: {humanoid.gameObject.name}");
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
            if (preferStickFigureOnly)
                posePlaybackController.SetUseStickFigureOnly(true);
            posePlaybackController.LoadFrames(payload.Frames, payload.Fps, payload.Loop);
            Debug.Log($"[FlutterUnityBridge] LoadPoseFrames: {payload.Frames?.Count ?? 0} frames, fps={payload.Fps}, loop={payload.Loop}");
            FrameCameraToFigure();
        }
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
            posePlaybackController.SetSkeletonColor(color);
    }

    public void SetCameraAngle(string message)
    {
        var orbit = EnsureOrbitController();
        if (orbit != null)
            orbit.SetAnglePreset(message);
        else if (poseCamera != null)
            ApplyCameraAngleDirect(message);
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
            default:               offset = new Vector3(0, 0, -distance); break;
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
        return FindAnyObjectByType<HumanoidPoseDriver>();
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
