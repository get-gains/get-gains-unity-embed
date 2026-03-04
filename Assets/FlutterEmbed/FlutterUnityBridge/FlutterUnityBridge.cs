using UnityEngine;

/// <summary>
/// Bridge script for Flutter ↔ Unity messaging. The Flutter app calls these methods
/// on the GameObject named "FlutterUnityBridge". Sends "scene_loaded" to Flutter when ready.
/// Implements pose/skeleton: LoadPoseFrames, PlayPose, PausePose, SeekPoseFrame, SetSkeletonColor, SetCameraAngle.
/// </summary>
public class FlutterUnityBridge : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Optional. If null, a child cube is created for rotation.")]
    private Transform rotatableTarget;

    [SerializeField]
    [Tooltip("Optional. If null, pose playback is created at runtime (stick figure).")]
    private PosePlaybackController posePlaybackController;

    [SerializeField]
    [Tooltip("Optional. Camera to orbit for SetCameraAngle (FRONT, SIDE_LEFT, etc.).")]
    private Camera poseCamera;

    private float rotationSpeed;
    private bool sceneLoadedSent;
    private string _lastCameraAngle = "FRONT";

    private void Awake()
    {
        if (poseCamera == null)
            poseCamera = Camera.main;
        SendSceneLoadedOnce();
    }

    /// <summary>Create pose playback + stick figure on first use so initial scene load stays light.</summary>
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

    /// <summary>
    /// Orbit camera around the figure center. Supported angles:
    /// FRONT, SIDE_LEFT, SIDE_RIGHT, REAR, ANGLE_45_LEFT, ANGLE_45_RIGHT.
    /// </summary>
    public void SetCameraAngle(string message)
    {
        if (poseCamera == null) return;
        _lastCameraAngle = (message ?? "").Trim().ToUpperInvariant();
        ApplyCameraAngle();
    }

    private void ApplyCameraAngle()
    {
        if (poseCamera == null) return;

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
        switch (_lastCameraAngle)
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

    /// <summary>Auto-frame camera to fit the loaded figure.</summary>
    private void FrameCameraToFigure()
    {
        ApplyCameraAngle();
    }

    private PoseStickFigureRenderer GetFigureRenderer()
    {
        if (posePlaybackController == null) return null;
        return posePlaybackController.GetComponentInChildren<PoseStickFigureRenderer>();
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
