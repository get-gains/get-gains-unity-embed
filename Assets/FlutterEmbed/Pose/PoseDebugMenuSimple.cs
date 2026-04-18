using UnityEngine;

/// <summary>
/// Very small on-screen debug menu to tweak HumanoidPoseDriver inversion/space options at runtime.
/// Attach this to the same GameObject as the humanoid rig (the one with HumanoidPoseDriver),
/// or assign the driver reference in the Inspector.
/// </summary>
public class PoseDebugMenuSimple : MonoBehaviour
{
    [SerializeField] private HumanoidPoseDriver driver;

    [Header("UI")]
    [SerializeField] private bool startVisible = false;
    [SerializeField] private KeyCode toggleKey = KeyCode.F3;
    // Full-width bar at bottom so it's impossible to miss on phone.
    [SerializeField] private float buttonHeight = 80f;
    [SerializeField] private float buttonPadding = 0f;

    private bool _visible;
    private bool _hiddenForCosmeticMode;
    private Rect _windowRect = new Rect(20, 20, 320, 260);

    private void Awake()
    {
        if (driver == null)
            driver = GetComponent<HumanoidPoseDriver>();
        _visible = startVisible;
    }

    /// <summary>
    /// Hide the entire debug UI in Cosmetic view mode so it doesn't intercept touches
    /// or confuse users inspecting accessories.
    /// </summary>
    public void SetCosmeticMode(bool cosmetic)
    {
        _hiddenForCosmeticMode = cosmetic;
        if (cosmetic) _visible = false; // also collapse the window if open
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            _visible = !_visible;

        if (driver == null)
            driver = FindAnyObjectByType<HumanoidPoseDriver>();
    }

    private void OnGUI()
    {
        if (_hiddenForCosmeticMode) return;

        // Full-width bar along the bottom, very visible on device.
        float x = buttonPadding;
        float y = Screen.height - buttonPadding - buttonHeight;
        Rect buttonRect = new Rect(x, y, Screen.width - 2f * buttonPadding, buttonHeight);
        GUIStyle style = new GUIStyle(GUI.skin.button)
        {
            fontSize = 28,
            alignment = TextAnchor.MiddleCenter
        };
        string label = _visible ? "HIDE POSE DEBUG" : "SHOW POSE DEBUG";
        if (GUI.Button(buttonRect, label, style))
            _visible = !_visible;

        if (!_visible || driver == null) return;

        _windowRect = GUILayout.Window(0, _windowRect, DrawWindow, "Pose Debug");
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label("HumanoidPoseDriver toggles:");
        GUILayout.Space(4);

        // Space / inversion
        bool invX = GUILayout.Toggle(driver.InvertLandmarkX, "Invert Landmark X (left/right)");
        if (invX != driver.InvertLandmarkX) driver.InvertLandmarkX = invX;

        bool invZ = GUILayout.Toggle(driver.InvertLandmarkZ, "Invert Landmark Z (depth front/back)");
        if (invZ != driver.InvertLandmarkZ) driver.InvertLandmarkZ = invZ;

        bool flatXY = GUILayout.Toggle(driver.FlattenDirectionsToXY, "Flatten hips dir to XY");
        if (flatXY != driver.FlattenDirectionsToXY) driver.FlattenDirectionsToXY = flatXY;

        bool flipLimbZ = GUILayout.Toggle(driver.FlipLimbForwardZ, "Flip limbs Z (front/back)");
        if (flipLimbZ != driver.FlipLimbForwardZ) driver.FlipLimbForwardZ = flipLimbZ;

        GUILayout.Space(6);
        GUILayout.Label("Arm / limb tuning:");

        // Smooth speed slider
        float smooth = driver.SmoothSpeed;
        GUILayout.Label($"Smooth speed: {smooth:F1}");
        float newSmooth = GUILayout.HorizontalSlider(smooth, 2f, 30f);
        if (Mathf.Abs(newSmooth - smooth) > 0.01f) driver.SmoothSpeed = newSmooth;

        // Limb blend slider (how strongly limbs follow pose)
        float limbBlend = driver.LimbBlend;
        GUILayout.Label($"Limb blend: {limbBlend:F2}");
        float newLimbBlend = GUILayout.HorizontalSlider(limbBlend, 0.2f, 1f);
        if (Mathf.Abs(newLimbBlend - limbBlend) > 0.001f) driver.LimbBlend = newLimbBlend;

        // Max rotation per frame (stiff vs snappy)
        float maxRot = driver.MaxRotationPerFrame;
        GUILayout.Label($"Max rot / frame: {maxRot:F0}°");
        float newMaxRot = GUILayout.HorizontalSlider(maxRot, 10f, 180f);
        if (Mathf.Abs(newMaxRot - maxRot) > 0.5f) driver.MaxRotationPerFrame = newMaxRot;

        GUILayout.Space(6);
        GUILayout.Label("Head/Face binding:");
        string[] headModes =
        {
            "Face->Neck+Head",
            "Lock Head->Neck",
            "Face->Spine004"
        };

        int selected = 0;
        if (driver.HeadFaceBindingModeSetting == HumanoidPoseDriver.HeadFaceBindingMode.LockHeadToNeck) selected = 1;
        if (driver.HeadFaceBindingModeSetting == HumanoidPoseDriver.HeadFaceBindingMode.FaceDrivesSpine004) selected = 2;

        selected = GUILayout.SelectionGrid(selected, headModes, 3);
        driver.HeadFaceBindingModeSetting = selected == 0
            ? HumanoidPoseDriver.HeadFaceBindingMode.FaceDrivesNeck
            : selected == 1
                ? HumanoidPoseDriver.HeadFaceBindingMode.LockHeadToNeck
                : HumanoidPoseDriver.HeadFaceBindingMode.FaceDrivesSpine004;

        GUILayout.Space(6);
        GUILayout.Label("Tips:");
        GUILayout.Label("- If arms lag or feel rubbery → increase Smooth speed or Limb blend.");
        GUILayout.Label("- Default Max rot / frame is 130°. Lower it if arms snap or jitter.");
        GUILayout.Label("- If arms swing behind body → tweak Flip limbs Z / Invert Z.");

        GUI.DragWindow(new Rect(0, 0, 400, 24));
    }
}

