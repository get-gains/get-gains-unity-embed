using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch;

/// <summary>
/// Camera view modes driven by FlutterUnityBridge.SetCameraViewMode.
/// </summary>
public enum CameraViewMode { Workout, Cosmetic }

/// <summary>
/// Touch-driven orbit camera using the New Input System (EnhancedTouch).
/// Single-finger drag = orbit. Two-finger pinch = zoom. Camera always LookAt target.
///
/// View modes:
///   Workout  – full-figure framing (height multiplier 0.70), maxDistance 20 m.
///   Cosmetic – upper-body framing (height multiplier 0.40), maxDistance 6 m,
///              resets to front-facing yaw and slight downward pitch for accessory inspection.
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

    [Header("Pan")]
    [SerializeField] private float panSpeed = 0.003f;
    [SerializeField] private float maxPanRadius = 3f;

    [Header("Damping")]
    [SerializeField] private float smoothTime = 0.08f;

    // ── View-mode constants ──────────────────────────────────────────────────

    private const float WorkoutHeightMultiplier  = 0.70f;
    private const float WorkoutMaxDistance       = 20f;

    private const float CosmeticHeightMultiplier = 0.40f;
    private const float CosmeticMaxDistance      = 6f;
    private const float CosmeticDefaultPitch     = 8f;   // slight downward angle for face
    private const float CosmeticDefaultYaw       = 0f;   // front-facing

    // ── State ────────────────────────────────────────────────────────────────

    private Camera _cam;
    private Vector3 _target;
    private float _yaw;
    private float _pitch = 5f;
    private float _distance = 5f;

    private float _yawVel, _pitchVel, _distVel;
    private float _targetYaw, _targetPitch, _targetDist;

    private float _prevPinchDist;
    private Vector2 _prevPinchCenter;
    private bool _initialized;

    private CameraViewMode _viewMode = CameraViewMode.Workout;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void OnEnable()  { EnhancedTouchSupport.Enable(); }
    private void OnDisable() { EnhancedTouchSupport.Disable(); }

    private void Awake() { _cam = GetComponent<Camera>(); }

    // ── Public API ───────────────────────────────────────────────────────────

    public void SetCamera(Camera cam) { _cam = cam; }

    /// <summary>
    /// Switch view mode. Cosmetic mode reframes for accessory inspection;
    /// Workout mode restores full-figure defaults.
    /// Call before SetTarget so the correct multiplier and distance limits apply.
    /// </summary>
    public void SetViewMode(CameraViewMode mode)
    {
        _viewMode = mode;
        maxDistance = mode == CameraViewMode.Cosmetic ? CosmeticMaxDistance : WorkoutMaxDistance;

        if (mode == CameraViewMode.Cosmetic)
        {
            _yaw         = CosmeticDefaultYaw;
            _targetYaw   = CosmeticDefaultYaw;
            _pitch       = CosmeticDefaultPitch;
            _targetPitch = CosmeticDefaultPitch;
        }

        // Clamp current target distance to the new limit.
        _targetDist = Mathf.Clamp(_targetDist, minDistance, maxDistance);
        if (_initialized) ApplyImmediate();
    }

    /// <summary>
    /// Reframe the camera around <paramref name="center"/> using <paramref name="figureHeight"/>
    /// and the current view mode's height multiplier.
    /// </summary>
    public void SetTarget(Vector3 center, float figureHeight)
    {
        _target = center;
        if (_cam != null && figureHeight > 0.1f)
        {
            float mult = _viewMode == CameraViewMode.Cosmetic
                ? CosmeticHeightMultiplier
                : WorkoutHeightMultiplier;

            float idealDist = (figureHeight * mult) / Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _distance = Mathf.Clamp(idealDist, minDistance, maxDistance);
        }
        _targetDist  = _distance;
        _targetYaw   = _yaw;
        _targetPitch = _pitch;
        _initialized = true;
        ApplyImmediate();
    }

    public void SetAnglePreset(string angle)
    {
        switch ((angle ?? "").Trim().ToUpperInvariant())
        {
            case "FRONT":          _targetYaw =   0f;  _targetPitch = 5f; break;
            case "SIDE_LEFT":      _targetYaw =  90f;  _targetPitch = 5f; break;
            case "SIDE_RIGHT":     _targetYaw = -90f;  _targetPitch = 5f; break;
            case "REAR":           _targetYaw = 180f;  _targetPitch = 5f; break;
            case "ANGLE_45_LEFT":  _targetYaw =  45f;  _targetPitch = 5f; break;
            case "ANGLE_45_RIGHT": _targetYaw = -45f;  _targetPitch = 5f; break;
            default: return;
        }
        _yaw   = _targetYaw;
        _pitch = _targetPitch;
        ApplyImmediate();
    }

    public void UpdateTarget(Vector3 center) { _target = center; }

    // ── Runtime ──────────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (!_initialized || _cam == null) return;

        HandleInput();

        _yaw      = Mathf.SmoothDamp(_yaw,      _targetYaw,   ref _yawVel,   smoothTime);
        _pitch    = Mathf.SmoothDamp(_pitch,    _targetPitch, ref _pitchVel, smoothTime);
        _distance = Mathf.SmoothDamp(_distance, _targetDist,  ref _distVel,  smoothTime);

        ApplyOrbit();
    }

    private void HandleInput()
    {
        var touches = ETouch.Touch.activeTouches;
        int count   = touches.Count;

        if (count == 1)
        {
            var t = touches[0];
            if (t.phase == UnityEngine.InputSystem.TouchPhase.Moved)
            {
                Vector2 delta = t.delta;
                _targetYaw   += delta.x * orbitSpeed;
                _targetPitch -= delta.y * orbitSpeed;
                _targetPitch  = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
            }
        }
        else if (count >= 2)
        {
            var t0 = touches[0];
            var t1 = touches[1];
            float curDist   = Vector2.Distance(t0.screenPosition, t1.screenPosition);
            Vector2 curCenter = (t0.screenPosition + t1.screenPosition) * 0.5f;

            bool justBegan = t0.phase == UnityEngine.InputSystem.TouchPhase.Began
                          || t1.phase == UnityEngine.InputSystem.TouchPhase.Began;

            if (justBegan)
            {
                _prevPinchDist   = curDist;
                _prevPinchCenter = curCenter;
            }
            else
            {
                float distDelta   = curDist - _prevPinchDist;
                Vector2 panDelta  = curCenter - _prevPinchCenter;

                // Classify dominant gesture: if the pinch distance change is larger
                // relative to the center translation → zoom; otherwise → pan.
                if (Mathf.Abs(distDelta) > panDelta.magnitude * 0.7f)
                {
                    _targetDist -= distDelta * zoomSpeed;
                    _targetDist  = Mathf.Clamp(_targetDist, minDistance, maxDistance);
                }
                else if (panDelta.magnitude > 0.5f)
                {
                    PanTarget(panDelta);
                }

                _prevPinchDist   = curDist;
                _prevPinchCenter = curCenter;
            }
        }

        // Mouse fallback for editor
        if (Mouse.current != null)
        {
            if (Mouse.current.rightButton.isPressed)
            {
                Vector2 mouseDelta = Mouse.current.delta.ReadValue();
                _targetYaw   += mouseDelta.x * orbitSpeed * 0.3f;
                _targetPitch -= mouseDelta.y * orbitSpeed * 0.3f;
                _targetPitch  = Mathf.Clamp(_targetPitch, minPitch, maxPitch);
            }
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.1f)
            {
                _targetDist -= scroll * 0.01f;
                _targetDist  = Mathf.Clamp(_targetDist, minDistance, maxDistance);
            }
        }
    }

    private void PanTarget(Vector2 screenDelta)
    {
        if (_cam == null) return;
        float scale = _distance * panSpeed;
        Vector3 right = _cam.transform.right;
        Vector3 up    = _cam.transform.up;
        _target -= right * (screenDelta.x * scale);
        _target -= up    * (screenDelta.y * scale);
        // Clamp so users can't drift the target off into empty space
        _target = Vector3.ClampMagnitude(_target, maxPanRadius);
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
