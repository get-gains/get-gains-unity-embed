using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch;

/// <summary>
/// Touch-driven orbit camera using the New Input System (EnhancedTouch).
/// Single-finger drag = orbit. Two-finger pinch = zoom. Camera always LookAt target.
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

    private void OnEnable()
    {
        EnhancedTouchSupport.Enable();
    }

    private void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    private void Awake()
    {
        _cam = GetComponent<Camera>();
    }

    public void SetCamera(Camera cam) { _cam = cam; }

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

    public void UpdateTarget(Vector3 center) { _target = center; }

    private void LateUpdate()
    {
        if (!_initialized || _cam == null) return;

        HandleInput();

        _yaw = Mathf.SmoothDamp(_yaw, _targetYaw, ref _yawVel, smoothTime);
        _pitch = Mathf.SmoothDamp(_pitch, _targetPitch, ref _pitchVel, smoothTime);
        _distance = Mathf.SmoothDamp(_distance, _targetDist, ref _distVel, smoothTime);

        ApplyOrbit();
    }

    private void HandleInput()
    {
        var touches = ETouch.Touch.activeTouches;
        int count = touches.Count;

        if (count == 1)
        {
            var t = touches[0];
            if (t.phase == UnityEngine.InputSystem.TouchPhase.Moved)
            {
                Vector2 delta = t.delta;
                _targetYaw += delta.x * orbitSpeed;
                _targetPitch -= delta.y * orbitSpeed;
                _targetPitch = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
            }
        }
        else if (count >= 2)
        {
            var t0 = touches[0];
            var t1 = touches[1];
            float curDist = Vector2.Distance(t0.screenPosition, t1.screenPosition);

            bool justBegan = t0.phase == UnityEngine.InputSystem.TouchPhase.Began
                          || t1.phase == UnityEngine.InputSystem.TouchPhase.Began;

            if (justBegan)
            {
                _prevPinchDist = curDist;
            }
            else
            {
                float delta = curDist - _prevPinchDist;
                _targetDist -= delta * zoomSpeed;
                _targetDist = Mathf.Clamp(_targetDist, minDistance, maxDistance);
                _prevPinchDist = curDist;
            }
        }

        // Mouse fallback for editor
        if (Mouse.current != null)
        {
            if (Mouse.current.rightButton.isPressed)
            {
                Vector2 mouseDelta = Mouse.current.delta.ReadValue();
                _targetYaw += mouseDelta.x * orbitSpeed * 0.3f;
                _targetPitch -= mouseDelta.y * orbitSpeed * 0.3f;
                _targetPitch = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
            }
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.1f)
            {
                _targetDist -= scroll * 0.01f;
                _targetDist = Mathf.Clamp(_targetDist, minDistance, maxDistance);
            }
        }
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
        _yawVel = _pitchVel = _distVel = 0f;
        if (_cam != null) ApplyOrbit();
    }
}
