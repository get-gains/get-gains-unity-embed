using UnityEngine;

/// <summary>
/// Touch-driven orbit camera that orbits around a target point (the pose figure center).
/// Single-finger drag rotates (orbit). Two-finger pinch zooms (dolly).
/// Attach to the same GameObject as the Camera, or assign the camera via SetCamera().
/// </summary>
public class OrbitCameraController : MonoBehaviour
{
    [Header("Orbit")]
    [SerializeField] private float orbitSpeed = 0.25f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 80f;

    [Header("Zoom")]
    [SerializeField] private float zoomSpeed = 0.015f;
    [SerializeField] private float minDistance = 0.8f;
    [SerializeField] private float maxDistance = 20f;

    [Header("Damping")]
    [SerializeField] private float smoothTime = 0.08f;

    private Camera _cam;
    private Vector3 _target;
    private float _yaw;
    private float _pitch = 5f;
    private float _distance = 5f;

    private float _yawVel, _pitchVel, _distVel;
    private float _targetYaw, _targetPitch, _targetDist;

    private float _prevPinchDist;
    private bool _initialized;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
    }

    /// <summary>Assign external camera if this script is not on the Camera GameObject.</summary>
    public void SetCamera(Camera cam)
    {
        _cam = cam;
    }

    /// <summary>
    /// Anchor the orbit around [center] and set initial distance so the figure fills the view.
    /// Called by FlutterUnityBridge after frames are loaded.
    /// </summary>
    public void SetTarget(Vector3 center, float figureHeight)
    {
        _target = center;
        if (_cam != null && figureHeight > 0.1f)
        {
            float idealDist = (figureHeight * 0.7f) / Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _distance = Mathf.Clamp(idealDist, minDistance, maxDistance);
        }
        _targetDist = _distance;
        _targetYaw = _yaw;
        _targetPitch = _pitch;
        _initialized = true;
        ApplyImmediate();
    }

    /// <summary>
    /// Set the orbital yaw/pitch from a named camera angle preset.
    /// Resets user rotation to the chosen angle.
    /// </summary>
    public void SetAnglePreset(string angle)
    {
        switch ((angle ?? "").Trim().ToUpperInvariant())
        {
            case "FRONT":          _targetYaw = 0f;    _targetPitch = 5f;  break;
            case "SIDE_LEFT":      _targetYaw = 90f;   _targetPitch = 5f;  break;
            case "SIDE_RIGHT":     _targetYaw = -90f;  _targetPitch = 5f;  break;
            case "REAR":           _targetYaw = 180f;  _targetPitch = 5f;  break;
            case "ANGLE_45_LEFT":  _targetYaw = 45f;   _targetPitch = 5f;  break;
            case "ANGLE_45_RIGHT": _targetYaw = -45f;  _targetPitch = 5f;  break;
            default: return;
        }
        _yaw = _targetYaw;
        _pitch = _targetPitch;
        ApplyImmediate();
    }

    /// <summary>Update the orbit target each frame (figure may move during playback).</summary>
    public void UpdateTarget(Vector3 center)
    {
        _target = center;
    }

    private void LateUpdate()
    {
        if (!_initialized || _cam == null) return;

        HandleTouchInput();

        _yaw = Mathf.SmoothDamp(_yaw, _targetYaw, ref _yawVel, smoothTime);
        _pitch = Mathf.SmoothDamp(_pitch, _targetPitch, ref _pitchVel, smoothTime);
        _distance = Mathf.SmoothDamp(_distance, _targetDist, ref _distVel, smoothTime);

        ApplyOrbit();
    }

    private void HandleTouchInput()
    {
        int touchCount = Input.touchCount;

        if (touchCount == 1)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Moved)
            {
                _targetYaw += t.deltaPosition.x * orbitSpeed;
                _targetPitch -= t.deltaPosition.y * orbitSpeed;
                _targetPitch = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
            }
        }
        else if (touchCount >= 2)
        {
            Touch t0 = Input.GetTouch(0);
            Touch t1 = Input.GetTouch(1);
            float curDist = Vector2.Distance(t0.position, t1.position);

            if (t0.phase == TouchPhase.Began || t1.phase == TouchPhase.Began)
            {
                _prevPinchDist = curDist;
            }
            else if (t0.phase == TouchPhase.Moved || t1.phase == TouchPhase.Moved)
            {
                float delta = curDist - _prevPinchDist;
                _targetDist -= delta * zoomSpeed;
                _targetDist = Mathf.Clamp(_targetDist, minDistance, maxDistance);
                _prevPinchDist = curDist;
            }
        }

#if UNITY_EDITOR
        // Mouse fallback for editor testing: right-drag to orbit, scroll to zoom
        if (Input.GetMouseButton(1))
        {
            _targetYaw += Input.GetAxis("Mouse X") * orbitSpeed * 8f;
            _targetPitch -= Input.GetAxis("Mouse Y") * orbitSpeed * 8f;
            _targetPitch = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
        }
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            _targetDist -= scroll * 3f;
            _targetDist = Mathf.Clamp(_targetDist, minDistance, maxDistance);
        }
#endif
    }

    private void ApplyOrbit()
    {
        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 offset = rotation * new Vector3(0f, 0f, -_distance);
        _cam.transform.position = _target + offset;
        _cam.transform.LookAt(_target);
    }

    private void ApplyImmediate()
    {
        _yawVel = 0f;
        _pitchVel = 0f;
        _distVel = 0f;
        if (_cam != null) ApplyOrbit();
    }
}
