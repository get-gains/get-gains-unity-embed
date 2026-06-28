using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// On-screen debug menu: HumanoidPoseDriver mapping, orbit camera, and pose playback.
/// Attach to the same GameObject as the humanoid rig, or let PoseDebugAutoInstaller add it.
/// </summary>
public class PoseDebugMenuSimple : MonoBehaviour
{
    [SerializeField] private HumanoidPoseDriver driver;

    [Header("UI")]
    [SerializeField] private bool startVisible = false;
    [Tooltip("Set Invert X on the resolved driver once at startup (typical 2D→3D; matches Pose debug default).")]
    [SerializeField] private bool startWithInvertLandmarkX = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F3;
    [SerializeField] private float buttonHeight = 80f;
    [SerializeField] private float buttonPadding = 0f;

    private bool _visible;
    private bool _hiddenForCosmeticMode;
    private Rect _windowRect = new Rect(20, 20, 380, 520);
    private Vector2 _scroll;
    private PosePlaybackController _playback;
    private OrbitCameraController _orbit;
    private PoseStickFigureRenderer _stick;

    private void Awake()
    {
        ResolveDriver();
        _visible = startVisible;
    }

    private void Start()
    {
        ResolveDriver();
        if (startWithInvertLandmarkX && driver != null)
        {
            driver.InvertLandmarkX = true;
            SyncStickFigureLandmarkInversion();
        }
    }

    public void SetCosmeticMode(bool cosmetic)
    {
        _hiddenForCosmeticMode = cosmetic;
        if (cosmetic) _visible = false;
    }

    private void Update()
    {
        if (WasToggleKeyPressedThisFrame(toggleKey))
            _visible = !_visible;

        if (driver == null || !driver.isActiveAndEnabled)
            ResolveDriver();
        if (_playback == null)
            _playback = Object.FindAnyObjectByType<PosePlaybackController>(FindObjectsInactive.Exclude);
        if (_playback != null) _playback.ResolveHumanoidDriver();
        if (_orbit == null)
            _orbit = Object.FindAnyObjectByType<OrbitCameraController>(FindObjectsInactive.Exclude);
        if (_stick == null)
            _stick = Object.FindAnyObjectByType<PoseStickFigureRenderer>(FindObjectsInactive.Exclude);
        if (driver != null && _stick != null)
        {
            _stick.TorsoDebugFlatten = driver.TorsoDebugFlatten;
            _stick.SetDebugInvertLegDepthZ(driver.DebugInvertLegDepthZ);
        }

        var k = Keyboard.current;
        if (k == null || !_visible || _hiddenForCosmeticMode) return;
        if (_playback == null || _playback.FrameCount <= 0) return;
        if (k.spaceKey.wasPressedThisFrame)
        {
            if (_playback.IsPlaying) _playback.Pause();
            else _playback.Play();
        }
        if (k.pKey.wasPressedThisFrame) _playback.StepPrevFrame();
        if (k.nKey.wasPressedThisFrame) _playback.StepNextFrame();
        if (k.lKey.wasPressedThisFrame) _playback.Loop = !_playback.Loop;
    }

    private void ResolveDriver()
    {
        if (driver != null && driver.isActiveAndEnabled && driver.gameObject.activeInHierarchy)
        {
            driver.TryInitialize();
            if (driver.IsDriveable)
            {
                CachePoseDebugRefs();
                return;
            }
        }

        if (_playback == null)
            _playback = Object.FindAnyObjectByType<PosePlaybackController>(FindObjectsInactive.Exclude);
        if (_playback != null)
        {
            _playback.ResolveHumanoidDriver();
            if (_playback.ActiveHumanoidDriver != null
                && _playback.ActiveHumanoidDriver.isActiveAndEnabled)
            {
                driver = _playback.ActiveHumanoidDriver;
                CachePoseDebugRefs();
                return;
            }
        }

        if (driver == null) driver = GetComponent<HumanoidPoseDriver>();
        if (driver == null) driver = HumanoidPoseDriver.FindBestDriveableDriver();
        CachePoseDebugRefs();
    }

    private void CachePoseDebugRefs()
    {
        if (_playback == null)
            _playback = Object.FindAnyObjectByType<PosePlaybackController>(FindObjectsInactive.Exclude);
        if (_stick == null)
            _stick = Object.FindAnyObjectByType<PoseStickFigureRenderer>(FindObjectsInactive.Exclude);
        if (_orbit == null)
            _orbit = Object.FindAnyObjectByType<OrbitCameraController>(FindObjectsInactive.Exclude);
    }

    private void SetTorsoDebugOnDriverAndStick(PoseLandmarkMapping.TorsoDebugFlattenMode mode)
    {
        if (driver != null) driver.TorsoDebugFlatten = mode;
        if (_stick != null) _stick.TorsoDebugFlatten = mode;
    }

    private void SetDebugInvertLegOnAll(bool value)
    {
        if (driver != null) driver.DebugInvertLegDepthZ = value;
        if (_stick != null) _stick.SetDebugInvertLegDepthZ(value);
        if (_playback != null) _playback.SetDebugInvertLegDepthZ(value);
    }

    private void SyncStickFigureLandmarkInversion()
    {
        if (driver == null) return;
        if (_stick == null)
            _stick = Object.FindAnyObjectByType<PoseStickFigureRenderer>(FindObjectsInactive.Exclude);
        if (_stick != null) _stick.SetLandmarkInversion(driver.InvertLandmarkX, driver.InvertLandmarkZ);
    }

    private static bool WasToggleKeyPressedThisFrame(KeyCode keyCode)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return false;
        if (!TryMapKeyCodeToKey(keyCode, out var key)) return false;
        return keyboard[key].wasPressedThisFrame;
    }

    private static bool TryMapKeyCodeToKey(KeyCode keyCode, out Key key)
    {
        switch (keyCode)
        {
            case KeyCode.F1: key = Key.F1; return true;
            case KeyCode.F2: key = Key.F2; return true;
            case KeyCode.F3: key = Key.F3; return true;
            case KeyCode.F4: key = Key.F4; return true;
            case KeyCode.F5: key = Key.F5; return true;
            case KeyCode.F6: key = Key.F6; return true;
            case KeyCode.F7: key = Key.F7; return true;
            case KeyCode.F8: key = Key.F8; return true;
            case KeyCode.F9: key = Key.F9; return true;
            case KeyCode.F10: key = Key.F10; return true;
            case KeyCode.F11: key = Key.F11; return true;
            case KeyCode.F12: key = Key.F12; return true;
            default: key = default; return false;
        }
    }

private void OnGUI() { }

    private void DrawWindow(int id)
    {
        _scroll = GUILayout.BeginScrollView(_scroll);

        if (_orbit != null)
        {
            GUILayout.Label("Orbit (touch / RMB + drag):");
            bool invOx = GUILayout.Toggle(_orbit.InvertOrbitX, "Invert orbit drag X (screen ↔ yaw)");
            if (invOx != _orbit.InvertOrbitX) _orbit.InvertOrbitX = invOx;
            GUILayout.Space(4);
        }

        GUILayout.Label("Landmark / driver:");
        bool invX = GUILayout.Toggle(driver.InvertLandmarkX, "Invert X (left / right mirror)");
        if (invX != driver.InvertLandmarkX)
        {
            driver.InvertLandmarkX = invX;
            SyncStickFigureLandmarkInversion();
        }

        bool invZ = GUILayout.Toggle(driver.InvertLandmarkZ, "Invert landmark Z (depth)");
        if (invZ != driver.InvertLandmarkZ)
        {
            driver.InvertLandmarkZ = invZ;
            SyncStickFigureLandmarkInversion();
        }

        bool invLegZ = GUILayout.Toggle(driver.DebugInvertLegDepthZ, "Invert leg world Z (knee→foot, default on)");
        if (invLegZ != driver.DebugInvertLegDepthZ) SetDebugInvertLegOnAll(invLegZ);

        bool flatXY = GUILayout.Toggle(driver.FlattenDirectionsToXY, "Flatten hips dir to XY");
        if (flatXY != driver.FlattenDirectionsToXY) driver.FlattenDirectionsToXY = flatXY;

        bool flipLimbZ = GUILayout.Toggle(driver.FlipLimbForwardZ, "Flip limbs Z (front / back)");
        if (flipLimbZ != driver.FlipLimbForwardZ) driver.FlipLimbForwardZ = flipLimbZ;

        GUILayout.Space(6);
        GUILayout.Label("Torso / hips (3D): (Uniform Z = default here)");
        GUILayout.Label(
            "2D: trapezoid. 3D: L/R depth mismatch can make the L-side and R-side torso rails cross in XZ (X / hourglass).");
        int tMode = (int)driver.TorsoDebugFlatten;
        tMode = GUILayout.SelectionGrid(
            tMode,
            new[] { "No flatten (see metrics)", "Uniform Z (perimeter plane)", "Uniform X (rare)" },
            1);
        var mode = (PoseLandmarkMapping.TorsoDebugFlattenMode)
            Mathf.Clamp(tMode, 0, (int)PoseLandmarkMapping.TorsoDebugFlattenMode.UniformX);
        if (mode != driver.TorsoDebugFlatten) SetTorsoDebugOnDriverAndStick(mode);

        var td = driver.LastTorsoHipDebug;
        GUILayout.Label($"L-rail / R-rail cross in XZ: {td.SidesCrossInXz}  (True = bow-tie / twist)");
        GUILayout.Label($"Hip+shoulder Z span: {td.ZSpread:F3}  X span: {td.XSpread:F3}");
        if (_stick != null)
        {
            var ts = _stick.LastTorsoHipDebug;
            GUILayout.Label(
                $"(Cyan stick fig.) cross={ts.SidesCrossInXz}  zSpan={ts.ZSpread:F3}  xSpan={ts.XSpread:F3}");
        }

        GUILayout.Space(6);
        GUILayout.Label("Arm / limb tuning:");

        float smooth = driver.SmoothSpeed;
        GUILayout.Label($"Smooth speed: {smooth:F1}");
        float newSmooth = GUILayout.HorizontalSlider(smooth, 2f, 30f);
        if (Mathf.Abs(newSmooth - smooth) > 0.01f) driver.SmoothSpeed = newSmooth;

        float limbBlend = driver.LimbBlend;
        GUILayout.Label($"Limb blend: {limbBlend:F2}");
        float newLimbBlend = GUILayout.HorizontalSlider(limbBlend, 0.2f, 1f);
        if (Mathf.Abs(newLimbBlend - limbBlend) > 0.001f) driver.LimbBlend = newLimbBlend;

        float maxRot = driver.MaxRotationPerFrame;
        GUILayout.Label($"Max rot / frame: {maxRot:F0}°");
        float newMaxRot = GUILayout.HorizontalSlider(maxRot, 10f, 180f);
        if (Mathf.Abs(newMaxRot - maxRot) > 0.5f) driver.MaxRotationPerFrame = newMaxRot;

        GUILayout.Space(6);
        GUILayout.Label("Head / face binding:");
        var headModes = new[]
        {
            "Face→Head",
            "Face→Neck",
            "Lock head",
            "Face→Spine.004"
        };
        int selected = (int)driver.HeadFaceBindingModeSetting;
        if (selected < 0 || selected > 3) selected = 0;
        selected = GUILayout.SelectionGrid(selected, headModes, 2);
        driver.HeadFaceBindingModeSetting = (HumanoidPoseDriver.HeadFaceBindingMode)selected;

        if (_playback != null && _playback.FrameCount > 0)
        {
            GUILayout.Space(8);
            GUILayout.Label("Playback (Space = play/pause, P = prev, N = next, L = loop):");
            bool loop = _playback.Loop;
            bool newLoop = GUILayout.Toggle(loop, "Loop at end of clip");
            if (newLoop != loop) _playback.Loop = newLoop;

            float spd = _playback.PlaybackSpeed;
            GUILayout.Label($"Speed: {spd:F2}×");
            float newSpd = GUILayout.HorizontalSlider(spd, 0.1f, 3f);
            if (Mathf.Abs(newSpd - spd) > 0.01f) _playback.PlaybackSpeed = newSpd;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_playback.IsPlaying ? "Pause" : "Play"))
            {
                if (_playback.IsPlaying) _playback.Pause();
                else _playback.Play();
            }
            if (GUILayout.Button("Next frame")) _playback.StepNextFrame();
            if (GUILayout.Button("Prev frame")) _playback.StepPrevFrame();
            GUILayout.EndHorizontal();

            int fc = _playback.FrameCount;
            int cur = _playback.CurrentFrameIndex;
            GUILayout.Label($"Frame: {cur} / {fc - 1}");
            float t = fc <= 1 ? 0f : (float)cur / Mathf.Max(1, fc - 1);
            float newT = GUILayout.HorizontalSlider(t, 0f, 1f);
            if (Mathf.Abs(newT - t) > 0.0001f && fc > 0)
                _playback.SeekToFrame(Mathf.Clamp(Mathf.RoundToInt(newT * (fc - 1)), 0, fc - 1));
        }

        GUILayout.Space(6);
        GUILayout.Label("Tips: Invert X (2D→3D). Uniform Z + invert leg Z are on by default. Space / P / N / L when open.");
        GUILayout.EndScrollView();
        GUI.DragWindow(new Rect(0, 0, 420, 24));
    }
}
