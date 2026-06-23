using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid rig from MediaPipe/MLKit landmark pose data.
///
/// Refactored for stability using ideas from:
/// - ganeshsar/UnityPythonMediaPipeAvatar: reference pose (bind) + delta rotation, spine dampening (0.25), Tick smoothing.
/// - BrandonBartram98/MediaPipe-UnitySolver: per-bone dampener and Slerp(rotation, target, lerpAmount).
///
/// Strategy: rotation-only on bones + root scale for proportions. Landmarks map through PoseLandmarkMapping
/// (XY-relative depth multiplier). Bones align to mapped directions root-to-leaf; root height scale uses Y span only.
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    public enum HeadFaceBindingMode
    {
        FaceDrivesHead = 0,
        FaceDrivesNeck = 1,
        LockHeadToNeck = 2,
        FaceDrivesSpine004 = 3
    }

    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Scale and space")]
    [Tooltip("Must match PoseStickFigureRenderer.scale so positions align.")]
    [SerializeField] private float poseScale = 5f;
    [Tooltip("Scale for landmark Z (depth).")]
#pragma warning disable CS0414
    [SerializeField] private float poseDepthScale = 5f;
#pragma warning restore CS0414
    [Tooltip("Invert landmark X so left/right matches 2D (person left = Unity -X when facing +Z).")]
    [SerializeField] private bool invertLandmarkX = true;
    [Tooltip("MLKit: negative Z = toward camera (front). When false, use MLKit Z as-is; enable only if front/back is flipped.")]
    [SerializeField] private bool invertLandmarkZ = false;
    [Tooltip("When true, infer per-side arm/leg Z signs geometrically each frame instead of using the hardcoded debug flags.")]
    [SerializeField] private bool useInferredDepthZ = true;
    [Tooltip("Flatten only Hips (spine) direction to XY to avoid Z-twist on torso.")]
    [SerializeField] private bool flattenDirectionsToXY = true;
    [Tooltip("If rig faces -Z (common), flip limb forward so bicep curl is in front, not behind.")]
    [SerializeField] private bool flipLimbForwardZ = true;
    [Tooltip("Drive neck/head from HEAD_CENTER so skull follows face, not anchored.")]
    [SerializeField] private bool driveHead = true;
    [Tooltip("MLKit z is not 0-1; divide raw z by this so depth stays sensible (e.g. 100).")]
#pragma warning disable CS0414
    [SerializeField] private float zNormalizeScale = 100f;
    [Tooltip("Clamp normalized X,Y to this range so out-of-frame landmarks don't blow up (MediaPipe can return outside 0-1).")]
    [SerializeField] private float xyClampMin = -0.2f;
    [SerializeField] private float xyClampMax = 1.2f;
#pragma warning restore CS0414

    // Runtime access for debug tools (inverts / options that affect pose mapping)
    public bool InvertLandmarkX { get => invertLandmarkX; set => invertLandmarkX = value; }
    public bool InvertLandmarkZ { get => invertLandmarkZ; set => invertLandmarkZ = value; }
    public bool FlattenDirectionsToXY { get => flattenDirectionsToXY; set => flattenDirectionsToXY = value; }
    public bool FlipLimbForwardZ { get => flipLimbForwardZ; set => flipLimbForwardZ = value; }

    /// <summary>Debug: collapse torso to a plane to fix bow-tie / X-shaped hips in 3D (depth mismatch L/R).</summary>
    public PoseLandmarkMapping.TorsoDebugFlattenMode TorsoDebugFlatten { get; set; } =
        PoseLandmarkMapping.TorsoDebugFlattenMode.None;

    public PoseLandmarkMapping.TorsoHipDebugInfo LastTorsoHipDebug { get; private set; }

    // Runtime access for arm/limb tuning
    public float SmoothSpeed { get => smoothSpeed; set => smoothSpeed = Mathf.Max(0.1f, value); }
    public float MaxRotationPerFrame
    {
        get => maxRotationPerFrame;
        set => maxRotationPerFrame = Mathf.Clamp(value, 0f, 360f);
    }
    public float LimbBlend
    {
        get => limbBlend;
        set => limbBlend = Mathf.Clamp(value, 0.2f, 1f);
    }

    public HeadFaceBindingMode HeadFaceBindingModeSetting
    {
        get => headFaceBindingMode;
        set => headFaceBindingMode = value;
    }

    [Header("Stability (reference: ganeshsar, MediaPipe-UnitySolver)")]
    [Tooltip("Lerp speed toward target rotation per second (higher = snappier). ~10–15 stable.")]
    [SerializeField] private float smoothSpeed = 12f;
    [Tooltip("Max rotation change per bone per frame (degrees) to avoid single-frame spikes.")]
    [SerializeField] private float maxRotationPerFrame = 130f;
    [Tooltip("Blend for spine/hips (0.25–0.4). Lower = more stable, less follow.")]
    [Range(0.2f, 0.6f)]
    [SerializeField] private float spineBlend = 0.35f;
    [Tooltip("Blend for limbs (0.6–1). Higher = more responsive.")]
    [Range(0.5f, 1f)]
    [SerializeField] private float limbBlend = 0.75f;
    [Header("Torso kinematics")]
    [Tooltip("Use 4-point torso frame (L/R shoulders + L/R hips) to drive hips/spine/chest/neck as one kinematic chain.")]
    [SerializeField] private bool useTorsoKinematics = true;
    [Tooltip("How strongly torso chain follows the 4-point kinematic solve.")]
    [Range(0f, 1f)]
    [SerializeField] private float torsoKinematicBlend = 0.8f;
    [Tooltip("Translation alignment: blend weight between hip and shoulder endpoints when moving the rig root.")]
    [Range(0f, 1f)]
    [SerializeField] private float torsoTranslationShoulderWeight = 0.25f;
    [Tooltip("How much head turns from face landmarks vs following neck posture.")]
    [Range(0f, 1f)]
    [SerializeField] private float headFaceBlend = 0.55f;

    [Tooltip("Prevents stretched artifacts by choosing whether face aims the head, aims the neck (group), or fully locks head to neck.")]
    [SerializeField] private HeadFaceBindingMode headFaceBindingMode = HeadFaceBindingMode.FaceDrivesNeck;
    [Header("Spine / face anchor (Rigify / custom rigs)")]
    [Tooltip("If set, used as the bone that should carry face + back-of-head (e.g. DEF-spine.004). Required when Unity strips DEF bones from the optimized hierarchy.")]
    [SerializeField] private Transform spineFaceAnchorOverride;

    [Header("Depth")]
    [Tooltip("Minimum hip-relative raw Z span when computing depth multiplier (avoids divide-by-near-zero).")]
    [SerializeField] private float zSpanFloor = 0.03f;
    [Tooltip("Maximum depth multiplier per frame (caps noise when raw Z span is tiny).")]
    [SerializeField] private float zMultiplierCap = 60f;
    [SerializeField] private bool invertDepthAxis;

    [Header("Head")]
    [Tooltip("Scales offset from MID_SHOULDER toward NOSE/EYE/EAR (lower = less head tilt).")]
    [SerializeField] private float headReachScale = 0.65f;
    [Tooltip("Extra dampening on Z component of head offset after radial scale.")]
    [SerializeField] private float headDepthScale = 0.55f;
    [Tooltip("Slerp each frame toward measured head-forward (higher = snappier). Side views need ~0.18–0.28.")]
    [SerializeField] private float headForwardSmoothAlpha = 0.22f;
    [Tooltip("Synthetic neck point along shoulder→nose (0–1). Splits neck vs head rotation so the mesh can match landmarks.")]
    [Range(0.12f, 0.72f)]
    [SerializeField] private float virtualNeckAlongShoulderToNose = 0.38f;

    // (Bone, HierarchyChild for bind direction, FromLandmark, ToLandmark)
    // Root-to-leaf order so parent rotations apply before children.
    private static readonly (HumanBodyBones Bone, HumanBodyBones Child, string From, string To)[] BoneMap =
    {
        (HumanBodyBones.Hips,           HumanBodyBones.Spine,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Spine,          HumanBodyBones.Chest,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Chest,          HumanBodyBones.UpperChest,     "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.UpperChest,     HumanBodyBones.Neck,           "MID_HIP",       "MID_SHOULDER"),
        // Neck takes shoulder→virtual neck; Head takes virtual neck→nose (Head was never rotated before).
        (HumanBodyBones.Neck,           HumanBodyBones.Head,           "MID_SHOULDER",  "NECK_VIRTUAL"),
        (HumanBodyBones.Head,           HumanBodyBones.Jaw,            "NECK_VIRTUAL",   "NOSE"),

        (HumanBodyBones.LeftUpperArm,   HumanBodyBones.LeftLowerArm,   "LEFT_SHOULDER", "LEFT_ELBOW"),
        (HumanBodyBones.LeftLowerArm,   HumanBodyBones.LeftHand,       "LEFT_ELBOW",    "LEFT_WRIST"),

        (HumanBodyBones.RightUpperArm,  HumanBodyBones.RightLowerArm,  "RIGHT_SHOULDER","RIGHT_ELBOW"),
        (HumanBodyBones.RightLowerArm,  HumanBodyBones.RightHand,      "RIGHT_ELBOW",   "RIGHT_WRIST"),

        (HumanBodyBones.LeftUpperLeg,   HumanBodyBones.LeftLowerLeg,   "LEFT_HIP",      "LEFT_KNEE"),
        (HumanBodyBones.LeftLowerLeg,   HumanBodyBones.LeftFoot,       "LEFT_KNEE",     "LEFT_ANKLE"),
        (HumanBodyBones.LeftFoot,       HumanBodyBones.LeftToes,       "LEFT_ANKLE",    "LEFT_FOOT_INDEX"),

        (HumanBodyBones.RightUpperLeg,   HumanBodyBones.RightLowerLeg,  "RIGHT_HIP",     "RIGHT_KNEE"),
        (HumanBodyBones.RightLowerLeg,  HumanBodyBones.RightFoot,      "RIGHT_KNEE",    "RIGHT_ANKLE"),
        (HumanBodyBones.RightFoot,      HumanBodyBones.RightToes,      "RIGHT_ANKLE",   "RIGHT_FOOT_INDEX"),
    };

    private struct RuntimeBone
    {
        public Transform Bone;
        public HumanBodyBones BoneType;
        public string From;
        public string To;
        public Vector3 BindLocalDir;
        public Quaternion BindLocalRot;
        /// <summary>Bind rotation expressed in the model root's local frame.</summary>
        public Quaternion BindModelLocalRot;
        /// <summary>World rotation in bind pose (reference for delta rotation, ganeshsar-style).</summary>
        public Quaternion BindWorldRot;
        /// <summary>World-space direction from bone to child in bind pose.</summary>
        public Vector3 BindWorldDir;
        /// <summary>Blend factor for this bone (spine vs limb).</summary>
        public float Blend;
    }

    private struct TorsoRuntimeBone
    {
        public Transform Bone;
        public Quaternion BindWorldRot;
        public Quaternion BindLocalRot;
        /// <summary>Bind rotation expressed in the model root's local frame.</summary>
        public Quaternion BindModelLocalRot;
        public float Weight;
    }

    private Transform _rootTransform;
    private Transform _hips;
    private Transform _neck;
    private Transform _head;
    private Transform _spine004;
    private Transform _leftUpperArm;
    private Transform _rightUpperArm;
    private Vector3 _bindHipsLocalPos;
    private float _bindHeight;
    private readonly List<RuntimeBone> _bones = new List<RuntimeBone>();
    private readonly Dictionary<string, Vector3> _pos = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, float> _confidence = new Dictionary<string, float>();
    private readonly List<TorsoRuntimeBone> _torsoBones = new List<TorsoRuntimeBone>();
    private bool _ready;
    private bool _loggedHumanoidWarning;

    /// <summary>If true, LEFT_* / RIGHT_* arm landmark positions are swapped before retargeting (mirrored rig vs camera).</summary>
    private bool _debugSwapArmLandmarks;

    /// <summary>If true, negate world Z on arm landmarks after mapping (debug).</summary>
    private bool _debugInvertArmDepthZ;

    /// <summary>If true, negate world Z on head cluster after head straightening (debug).</summary>
    private bool _debugInvertHeadDepthZ;

    /// <summary>If true, negate world Z on leg chain (knee–foot, not hip) so legs match camera depth like arms (debug).</summary>
    private bool _debugInvertLegDepthZ = true;

    private Vector3 _headForwardSmoothed;

    private Vector3 _figureCenter;
    private float _figureHeight = 1f;
    private Vector3 _targetRootPosition;
    private float _targetScale = 1f;
    private Quaternion _bindTorsoFrame = Quaternion.identity;
    private Quaternion _bindHeadFrame = Quaternion.identity;
    private Quaternion _bindHeadWorldRot = Quaternion.identity;
    private Quaternion _bindHeadLocalToNeck = Quaternion.identity;
    private Quaternion _bindNeckLocalToSpine004 = Quaternion.identity;
    private Quaternion _bindHeadLocalToSpine004 = Quaternion.identity;
    private Quaternion _bindNeckLocalToSpine004Local = Quaternion.identity;
    private bool _loggedSpineFaceAnchorMissing;

    [Header("Body direction")]
    [Tooltip("Max degrees per second the root can rotate to follow inferred body direction. 0 = lock root rotation.")]
    [SerializeField] private float bodyYawMaxSpeed = 120f;

    [Tooltip("Optional hint for the recorded camera angle (FRONT, ANGLE_45_LEFT, etc.). Biases body yaw when landmark depth is ambiguous.")]
    [SerializeField] private string recordingAngleHint = "";

        private Vector3 _initialRootPosition;
    private Quaternion _initialRootRotation = Quaternion.identity;
    private Vector3 _initialRootScale = Vector3.one;
private float _currentBodyYaw;

    private bool _isFirstYawFrame = true;

    /// <summary>Raw head facing computed before head-straightening smoothing; used for body yaw.</summary>
    private Vector3 _rawHeadFacingXZ;
    private readonly Dictionary<string, Vector3> _modelLocalPos = new Dictionary<string, Vector3>();

    // Local-space bind frames so retargeting works correctly as the root yaw changes.
    private Quaternion _bindTorsoFrameLocal = Quaternion.identity;
    private Vector3 _bindTorsoUpLocal;
    private Vector3 _bindTorsoRightLocal;
    private Vector3 _bindTorsoForwardLocal;

    private Quaternion _bindHeadFrameLocal = Quaternion.identity;
    private Quaternion _bindHeadLocalRot = Quaternion.identity;
    private Quaternion _bindNeckLocalRot = Quaternion.identity;
    private Quaternion _bindHeadModelLocalRot = Quaternion.identity;
    private Quaternion _bindNeckModelLocalRot = Quaternion.identity;
    private Quaternion _bindHeadLocalToNeckLocal = Quaternion.identity;
        private Quaternion _bindSpine004WorldRot = Quaternion.identity;
private Quaternion _bindHeadLocalToSpine004Local = Quaternion.identity;

    public Vector3 FigureCenter => _figureCenter;
    public float FigureHeight => _figureHeight;
    public Vector3 HipsWorldPosition => _hips != null ? _hips.position : (_rootTransform != null ? _rootTransform.position : Vector3.zero);

    /// <summary>True after a successful rig build (Humanoid avatar + mapped bones).</summary>
    public bool IsDriveable => _ready;

    public void SetDebugSwapArmLandmarks(bool value)
    {
        _debugSwapArmLandmarks = value;
    }

    public void SetDebugInvertArmDepthZ(bool value)
    {
        _debugInvertArmDepthZ = value;
    }

    public void SetDebugInvertHeadDepthZ(bool value)
    {
        _debugInvertHeadDepthZ = value;
    }

    /// <summary>
    /// Sets the recorded camera angle hint (e.g. "ANGLE_45_RIGHT"). The retargeter uses this
    /// to stabilise body yaw when depth cues are ambiguous.
    /// </summary>
    /// <param name="angle">Angle string from Flutter; unknown values are ignored.</param>
    public void SetRecordingAngleHint(string angle)
    {
        recordingAngleHint = (angle ?? "").Trim().ToUpperInvariant();
    }

    public bool DebugInvertLegDepthZ
    {
        get => _debugInvertLegDepthZ;
        set => _debugInvertLegDepthZ = value;
    }

    /// <summary>First active driveable HumanoidPoseDriver in loaded scenes, or null.</summary>
    public static HumanoidPoseDriver FindBestDriveableDriver()
    {
        var drivers = Object.FindObjectsByType<HumanoidPoseDriver>(FindObjectsInactive.Exclude);
        foreach (var d in drivers)
        {
            if (d == null) continue;
            d.TryInitialize();
            if (d.IsDriveable) return d;
        }
        return null;
    }

    private void Awake()
    {
        TryInitialize();
    }

    private void Start()
    {
        // Flutter may send pose before Awake/Start on other objects; retry after full scene init.
        TryInitialize();
    }

    /// <summary>
    /// Builds bone cache when possible. Safe to call every frame; no-op when already ready.
    /// Returns false if the Animator is missing, not Humanoid, or has no mappable bones.
    /// </summary>
    public bool TryInitialize()
    {
        if (_ready) return true;

        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) animator = GetComponentInChildren<Animator>(true);
        if (animator == null) animator = GetComponentInParent<Animator>(true);
        if (animator == null)
        {
            if (!_loggedHumanoidWarning)
            {
                Debug.LogWarning(
                    "[HumanoidPoseDriver] No Animator on this GameObject, its children, or parents. "
                    + "Add an Animator (Humanoid) or assign the Animator field.");
                _loggedHumanoidWarning = true;
            }
            return false;
        }

        if (animator.avatar == null || !animator.avatar.isHuman)
        {
            if (!_loggedHumanoidWarning)
            {
                Debug.LogError(
                    "[HumanoidPoseDriver] Avatar must be **Humanoid** (Rig tab in FBX import settings). "
                    + "Generic rigs cannot use Humanoid retargeting — the cyan stick figure will show instead.");
                _loggedHumanoidWarning = true;
            }
            return false;
        }

        _rootTransform = animator.transform;
        _initialRootPosition = _rootTransform.position;
        _initialRootRotation = _rootTransform.rotation;
        _initialRootScale = _rootTransform.localScale;
        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        _neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        _head = animator.GetBoneTransform(HumanBodyBones.Head);
        ResolveSpineFaceAnchor();
        _leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (_hips != null) _bindHipsLocalPos = _hips.localPosition;

        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        _bindHeight = 0f;
        if (head != null)
        {
            float footY = float.MaxValue;
            if (lFoot != null) footY = Mathf.Min(footY, lFoot.position.y);
            if (rFoot != null) footY = Mathf.Min(footY, rFoot.position.y);
            if (footY < float.MaxValue)
                _bindHeight = head.position.y - footY;
        }
        if (_bindHeight < 0.01f) _bindHeight = 1.8f;

        CacheTorsoChain();
        CacheHeadReference();
        RefreshSpineFaceAnchorBindOffsets();

        _bones.Clear();
        foreach (var m in BoneMap)
        {
            var bone = animator.GetBoneTransform(m.Bone);
            if (bone == null) continue;

            var child = animator.GetBoneTransform(m.Child);
            Vector3 localDir;
            if (child != null)
            {
                Vector3 worldDir = (child.position - bone.position).normalized;
                localDir = bone.InverseTransformDirection(worldDir);
            }
            else
            {
                localDir = bone.childCount > 0
                    ? bone.InverseTransformDirection((bone.GetChild(0).position - bone.position).normalized)
                    : Vector3.up;
            }
            if (localDir.sqrMagnitude < 0.001f) localDir = Vector3.up;

            Vector3 bindWorldDir = bone.TransformDirection(localDir);
            if (bindWorldDir.sqrMagnitude < 0.01f) continue; // degenerate bone, skip
            bindWorldDir.Normalize();
            Quaternion bindWorldRot = bone.rotation;

            // Direction and orientation in the model's root-local frame so we can solve bones after the body yaw is applied.
            Quaternion invRoot = _rootTransform != null ? Quaternion.Inverse(_rootTransform.rotation) : Quaternion.identity;
            Vector3 bindDirModelLocal = invRoot * bindWorldDir;
            Quaternion bindRotModelLocal = invRoot * bindWorldRot;

            bool isHips = m.From == "MID_HIP" && m.To == "MID_SHOULDER";
            bool isNeck = (m.From == "MID_SHOULDER" && m.To == "NECK_VIRTUAL") ||
                          (m.From == "NECK_VIRTUAL" && m.To == "NOSE");
            float blend = (isHips || isNeck) ? spineBlend : limbBlend;

            _bones.Add(new RuntimeBone
            {
                Bone = bone,
                BoneType = m.Bone,
                From = m.From,
                To = m.To,
                BindLocalDir = bindDirModelLocal,
                BindModelLocalRot = bindRotModelLocal,
                BindLocalRot = bone.localRotation,
                BindWorldRot = bindWorldRot,
                BindWorldDir = bindWorldDir,
                Blend = blend,
            });
        }

        _ready = _bones.Count > 0;
        _targetRootPosition = _initialRootPosition;
        _targetScale = _initialRootScale.x;
        if (_ready)
            Debug.Log(
                $"[HumanoidPoseDriver] Ready: {_bones.Count} bones, bindHeight={_bindHeight:F3}, smooth={smoothSpeed}, spineBlend={spineBlend}, limbBlend={limbBlend} on '{gameObject.name}'");
        else if (!_loggedHumanoidWarning)
        {
            Debug.LogError(
                "[HumanoidPoseDriver] No Humanoid bones mapped. Check Avatar Configuration (Mapping) for this model.");
            _loggedHumanoidWarning = true;
        }

        return _ready;
    }

    public void ApplyPose(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (landmarks == null || landmarks.Count == 0) return;
        if (!TryInitialize() || !_ready) return;

        BuildPositionCache(landmarks);
        ComputeFigureBounds();

        if (_hips != null)
            _hips.localPosition = _bindHipsLocalPos;

        // Rotate root so the model faces the inferred body direction.
        float targetYaw = ComputeUnifiedYaw();
        float maxDelta = bodyYawMaxSpeed * Time.deltaTime;
        _currentBodyYaw = Mathf.MoveTowardsAngle(_currentBodyYaw, targetYaw, maxDelta);
        if (_rootTransform != null)
            _rootTransform.rotation = Quaternion.Euler(0f, _currentBodyYaw, 0f);

        // Root scale (smoothed)
        float scale = (_bindHeight > 0.01f && _figureHeight > 0.1f)
            ? Mathf.Clamp(_figureHeight / _bindHeight, 0.85f, 1.15f)
            : 1f;
        _targetScale = scale;
        if (_rootTransform != null)
            _rootTransform.localScale = Vector3.one * Mathf.Lerp(_rootTransform.localScale.x, _targetScale, Time.deltaTime * smoothSpeed);

        // Root translation: when torso kinematics are enabled, the endpoint solve handles it.
        if (!useTorsoKinematics && _hips != null && _pos.TryGetValue("MID_HIP", out Vector3 midHip))
        {
            Vector3 hipsWorld = _hips.position;
            _targetRootPosition = _rootTransform.position + (midHip - hipsWorld);
            float maxDist = 20f;
            _targetRootPosition.x = Mathf.Clamp(_targetRootPosition.x, -maxDist, maxDist);
            _targetRootPosition.y = Mathf.Clamp(_targetRootPosition.y, -maxDist, maxDist);
            _targetRootPosition.z = Mathf.Clamp(_targetRootPosition.z, -maxDist, maxDist);
            _rootTransform.position = Vector3.Lerp(_rootTransform.position, _targetRootPosition, Time.deltaTime * smoothSpeed);
        }

        // Solve in model-local space after root yaw/position are set so bone math is independent of body direction.
        BuildModelLocalPositions();

        if (useTorsoKinematics)
        {
            ApplyTorsoKinematics();
            ApplyTorsoEndpointTranslation();
        }

        ApplyLimbRotationsModelLocal();

        if (driveHead)
            ApplyHeadFromTorsoAndFaceModelLocal();
    }

    /// <summary>
    /// Infers the Y-axis rotation that aligns the model's bind forward with the person's body forward.
    /// Uses a weighted circular mean of three geometry signals — shoulder axis, hip axis, and ear depth
    /// asymmetry — for a single robust estimator. The recording angle hint is used ONLY on the first
    /// frame to break the 180° front/back ambiguity; from frame 2 onward the geometry drives everything.
    /// </summary>
    private float ComputeUnifiedYaw()
    {
        // Signal 1: shoulder axis in XZ plane
        Vector3 shoulderYawSignal = Vector3.zero;
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) &&
            _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
        {
            shoulderYawSignal = new Vector3(rs.z - ls.z, 0f, rs.x - ls.x);
        }

        // Signal 2: hip axis in XZ plane
        Vector3 hipYawSignal = Vector3.zero;
        if (_pos.TryGetValue("LEFT_HIP", out Vector3 lh) &&
            _pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
        {
            hipYawSignal = new Vector3(rh.z - lh.z, 0f, rh.x - lh.x);
        }

        // Signal 3: ear depth asymmetry
        Vector3 earYawSignal = Vector3.zero;
        if (_pos.TryGetValue("LEFT_EAR", out Vector3 le) &&
            _pos.TryGetValue("RIGHT_EAR", out Vector3 re))
        {
            float earZDiff = re.z - le.z;
            earYawSignal = new Vector3(earZDiff, 0f, 1f);
        }

        // Confidence-weighted circular mean
        float w1 = 0.45f, w2 = 0.35f, w3 = 0.20f;
        float sx = 0f, sy = 0f, totalW = 0f;

        if (shoulderYawSignal.sqrMagnitude > 1e-6f)
        {
            float a = Mathf.Atan2(shoulderYawSignal.x, shoulderYawSignal.z);
            sx += w1 * Mathf.Cos(a);
            sy += w1 * Mathf.Sin(a);
            totalW += w1;
        }

        if (hipYawSignal.sqrMagnitude > 1e-6f)
        {
            float a = Mathf.Atan2(hipYawSignal.x, hipYawSignal.z);
            sx += w2 * Mathf.Cos(a);
            sy += w2 * Mathf.Sin(a);
            totalW += w2;
        }

        if (earYawSignal.sqrMagnitude > 1e-6f)
        {
            float a = Mathf.Atan2(earYawSignal.x, earYawSignal.z);
            sx += w3 * Mathf.Cos(a);
            sy += w3 * Mathf.Sin(a);
            totalW += w3;
        }

        if (totalW <= 0f) return _currentBodyYaw;

        float rawYaw = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg;

        // FIRST FRAME ONLY: use hint to break 180° ambiguity, then discard.
        if (_isFirstYawFrame && !string.IsNullOrEmpty(recordingAngleHint))
        {
            float? hintYaw = RecordingAngleToYaw(recordingAngleHint);
            if (hintYaw.HasValue)
            {
                float diff = Mathf.DeltaAngle(rawYaw, hintYaw.Value);
                if (Mathf.Abs(diff) > 90f)
                    rawYaw = Mathf.Repeat(rawYaw + 180f, 360f);
            }
            _isFirstYawFrame = false;
        }

        // Nose-in-front check for additional disambiguation (independent of hint).
        if (_pos.TryGetValue("NOSE", out Vector3 nose) &&
            _pos.TryGetValue("MID_SHOULDER", out Vector3 midShoulder))
        {
            Vector3 noseDir = nose - midShoulder;
            Vector3 forwardXZ = new Vector3(
                Mathf.Sin(rawYaw * Mathf.Deg2Rad), 0f,
                Mathf.Cos(rawYaw * Mathf.Deg2Rad));
            if (noseDir.sqrMagnitude > 1e-10f && Vector3.Dot(forwardXZ, noseDir) < 0f)
                rawYaw = Mathf.Repeat(rawYaw + 180f, 360f);
        }

        return rawYaw;
    }

    /// <summary>
    /// Converts the recorded camera angle string into a yaw angle in Unity world space.
    /// Returns null for unknown angles.
    /// </summary>
    private static float? RecordingAngleToYaw(string angle)
    {
        switch (angle)
        {
            case "FRONT": return 0f;
            case "REAR": return 180f;
            case "SIDE_LEFT": return 90f;
            case "SIDE_RIGHT": return -90f;
            case "ANGLE_45_LEFT": return 45f;
            case "ANGLE_45_RIGHT": return -45f;
            case "DIAGONAL": return -135f;
            default: return null;
        }
    }

    /// <summary>
    /// Re-expresses all mapped landmark positions in the model's local frame so bone solving
    /// is independent of the root yaw.
    /// </summary>
    private void BuildModelLocalPositions()
    {
        _modelLocalPos.Clear();
        if (_rootTransform == null) return;

        Quaternion invRoot = Quaternion.Inverse(_rootTransform.rotation);
        Vector3 rootPos = _rootTransform.position;
        foreach (var kvp in _pos)
            _modelLocalPos[kvp.Key] = invRoot * (kvp.Value - rootPos);
    }

    /// <summary>Call after LoadFrames or Seek to snap toward current pose (optional).</summary>
    public void SnapToPoseNextFrame()
    {
        // Next ApplyPose will use a one-frame higher effective smooth so we catch up; or we could set a flag to skip Slerp once.
        // For now, stability is from smoothing; snap can be added later if needed.
    }

    /// <summary>
    /// Convert raw landmarks to Unity space. Verified against MediaPipe/MLKit:
    /// - X,Y: normalized 0-1 (image space, origin top-left, Y down). We use (0.5-X)*scale so person left = Unity -X; (1-Y-0.5)*scale so image up = Unity +Y.
    /// - Z: not normalized in MLKit; we divide by zNormalizeScale then scale so depth is sensible and humanoid stays in frame.
    /// - Out-of-bounds X,Y (MediaPipe can return outside 0-1 for occluded joints) are clamped so no single vertex blows up.
    /// </summary>
    private void BuildPositionCache(Dictionary<string, LandmarkPoint> landmarks)
    {
        _pos.Clear();
        _confidence.Clear();
        _rawHeadFacingXZ = Vector3.zero;
        double zRef = PoseLandmarkMapping.ComputeZReference(landmarks);
        float zMult = PoseLandmarkMapping.ComputeDepthMultiplier(
            landmarks, poseScale, zRef, zSpanFloor, zMultiplierCap);
        foreach (var kvp in landmarks)
        {
            _pos[kvp.Key] = PoseLandmarkMapping.ToWorldPosition(
                kvp.Value,
                poseScale,
                zMult,
                invertDepthAxis,
                zRef,
                Vector3.zero,
                invertLandmarkX,
                invertLandmarkZ);
            _confidence[kvp.Key] = Mathf.Clamp01((float)kvp.Value.Confidence);
        }

        if (_pos.TryGetValue("LEFT_HIP", out Vector3 lh) && _pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
        {
            _pos["MID_HIP"] = 0.5f * (lh + rh);
            _confidence["MID_HIP"] = 1f;
        }
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
        {
            _pos["MID_SHOULDER"] = 0.5f * (ls + rs);
            _confidence["MID_SHOULDER"] = 1f;
        }
        if (_pos.TryGetValue("MID_SHOULDER", out Vector3 midSh) && _pos.TryGetValue("NOSE", out Vector3 nose))
        {
            _pos["HEAD_CENTER"] = Vector3.Lerp(midSh, nose, 0.5f);
            _confidence["HEAD_CENTER"] = 1f;
        }

        PoseLandmarkMapping.ApplyHeadClusterBlend(_pos, headReachScale, headDepthScale);
        if (_debugSwapArmLandmarks)
            ApplyDebugArmLandmarkSwap();

        if (useInferredDepthZ)
        {
            PoseLandmarkMapping.ApplyInferArmZSigns(_pos);
            PoseLandmarkMapping.ApplyInferLegZSigns(_pos);
        }
        else
        {
            PoseLandmarkMapping.ApplyInvertArmWorldZ(_pos, _debugInvertArmDepthZ);
            PoseLandmarkMapping.ApplyInvertLegWorldZ(_pos, _debugInvertLegDepthZ);
        }

        // Capture raw head facing BEFORE straightening rewrites the head cluster.
        // This is the most reliable hint for body yaw at 45° side angles.
        if (PoseLandmarkMapping.TryComputeHeadFacingXZ(_pos, out Vector3 rawFacing))
        {
            _rawHeadFacingXZ = rawFacing;
        }

        // After shoulders/depth are final so torso "forward" matches retargeting debug.
        PoseLandmarkMapping.ApplyHeadStraightAheadNearShoulder(
            _pos,
            poseScale,
            ref _headForwardSmoothed,
            headForwardSmoothAlpha);
        PoseLandmarkMapping.ApplyInvertHeadWorldZ(_pos, _debugInvertHeadDepthZ);
        if (TorsoDebugFlatten != PoseLandmarkMapping.TorsoDebugFlattenMode.None)
        {
            PoseLandmarkMapping.ApplyTorsoDebugFlatten(_pos, TorsoDebugFlatten);
            RecomputeMidHipShoulderAndHeadCenter();
        }
        ApplyVirtualNeckLandmark();
        if (!PoseLandmarkMapping.TryComputeTorsoHipDebug(_pos, out var th)) th = default;
        LastTorsoHipDebug = th;
    }

    private void RecomputeMidHipShoulderAndHeadCenter()
    {
        if (_pos.TryGetValue("LEFT_HIP", out Vector3 lh) && _pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
        {
            _pos["MID_HIP"] = 0.5f * (lh + rh);
            _confidence["MID_HIP"] = 1f;
        }
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
        {
            _pos["MID_SHOULDER"] = 0.5f * (ls + rs);
            _confidence["MID_SHOULDER"] = 1f;
        }
        if (_pos.TryGetValue("MID_SHOULDER", out Vector3 midSh) && _pos.TryGetValue("NOSE", out Vector3 nose))
        {
            _pos["HEAD_CENTER"] = Vector3.Lerp(midSh, nose, 0.5f);
            _confidence["HEAD_CENTER"] = 1f;
        }
    }

    /// <summary>
    /// Inserts NECK_VIRTUAL between MID_SHOULDER and NOSE so Neck and Head bones each get a rotation
    /// (previously only Neck rotated toward NOSE and Head stayed bind-pose, which skewed the mesh vs landmarks).
    /// </summary>
    private void ApplyVirtualNeckLandmark()
    {
        Vector3 mid;
        if (!_pos.TryGetValue("MID_SHOULDER", out mid))
        {
            if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) &&
                _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
                mid = 0.5f * (ls + rs);
            else
                return;
        }

        if (!_pos.TryGetValue("NOSE", out Vector3 nose))
            return;

        Vector3 delta = nose - mid;
        if (delta.sqrMagnitude < 1e-10f)
        {
            _pos["NECK_VIRTUAL"] = mid + Vector3.up * Mathf.Max(0.01f, poseScale * 0.02f);
            return;
        }

        float t = Mathf.Clamp01(virtualNeckAlongShoulderToNose);
        _pos["NECK_VIRTUAL"] = mid + delta * t;
    }

    private void CacheTorsoChain()
    {
        _torsoBones.Clear();
        if (animator == null) return;

        HumanBodyBones[] candidates =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
        };

        var chain = new List<Transform>();
        foreach (var hb in candidates)
        {
            var t = animator.GetBoneTransform(hb);
            if (t != null) chain.Add(t);
        }

        int n = chain.Count;
        if (n == 0) return;

        Quaternion invRoot = _rootTransform != null ? Quaternion.Inverse(_rootTransform.rotation) : Quaternion.identity;
        for (int i = 0; i < n; i++)
        {
            float w = GetHumanLikeTorsoWeight(chain[i], n == 1 ? i : i / (float)(n - 1));
            _torsoBones.Add(new TorsoRuntimeBone
            {
                Bone = chain[i],
                BindWorldRot = chain[i].rotation,
                BindModelLocalRot = invRoot * chain[i].rotation,
                BindLocalRot = chain[i].localRotation,
                Weight = w,
            });
        }

        if (_rootTransform != null)
        {
            Vector3 bindUp = _rootTransform.up;
            Vector3 bindRight = _rootTransform.right;
            if (TryGetBindTorsoAxes(out Vector3 up, out Vector3 right))
            {
                bindUp = up;
                bindRight = right;
            }

            Vector3 bindForward = Vector3.Cross(bindRight, bindUp).normalized;
            if (bindForward.sqrMagnitude < 1e-6f) bindForward = _rootTransform.forward;
            _bindTorsoFrame = Quaternion.LookRotation(bindForward, bindUp);

            invRoot = Quaternion.Inverse(_rootTransform.rotation);
            _bindTorsoUpLocal = invRoot * bindUp;
            _bindTorsoRightLocal = invRoot * bindRight;
            _bindTorsoForwardLocal = invRoot * bindForward;
            _bindTorsoFrameLocal = Quaternion.LookRotation(_bindTorsoForwardLocal, _bindTorsoUpLocal);
        }
    }

    private bool TryGetBindTorsoAxes(out Vector3 up, out Vector3 right)
    {
        up = Vector3.up;
        right = Vector3.right;
        if (animator == null) return false;

        var lShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var rShoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        var lHip = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        var rHip = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        if (lShoulder == null || rShoulder == null || lHip == null || rHip == null) return false;

        Vector3 midShoulder = 0.5f * (lShoulder.position + rShoulder.position);
        Vector3 midHip = 0.5f * (lHip.position + rHip.position);
        up = (midShoulder - midHip).normalized;
        right = (rShoulder.position - lShoulder.position).normalized;
        return up.sqrMagnitude > 1e-6f && right.sqrMagnitude > 1e-6f;
    }

    private void ApplyTorsoKinematics()
    {
        if (_torsoBones.Count == 0 || _rootTransform == null) return;
        if (!_modelLocalPos.TryGetValue("LEFT_SHOULDER", out Vector3 lShoulder)) return;
        if (!_modelLocalPos.TryGetValue("RIGHT_SHOULDER", out Vector3 rShoulder)) return;
        if (!_modelLocalPos.TryGetValue("LEFT_HIP", out Vector3 lHip)) return;
        if (!_modelLocalPos.TryGetValue("RIGHT_HIP", out Vector3 rHip)) return;

        Vector3 midShoulder = 0.5f * (lShoulder + rShoulder);
        Vector3 midHip = 0.5f * (lHip + rHip);
        Vector3 torsoUpLocal = (midShoulder - midHip);
        Vector3 torsoRightLocal = (rShoulder - lShoulder);
        if (torsoUpLocal.sqrMagnitude < 1e-6f || torsoRightLocal.sqrMagnitude < 1e-6f) return;

        torsoUpLocal.Normalize();
        torsoRightLocal.Normalize();
        Vector3 torsoForwardLocal = Vector3.Cross(torsoRightLocal, torsoUpLocal).normalized;
        if (torsoForwardLocal.sqrMagnitude < 1e-6f) return;

        Quaternion targetTorsoFrameLocal = Quaternion.LookRotation(torsoForwardLocal, torsoUpLocal);
        Quaternion torsoCorrection = targetTorsoFrameLocal * Quaternion.Inverse(_bindTorsoFrameLocal);
        torsoCorrection = Quaternion.Slerp(Quaternion.identity, torsoCorrection, torsoKinematicBlend);

        for (int i = 0; i < _torsoBones.Count; i++)
        {
            var tb = _torsoBones[i];
            if (tb.Bone == null) continue;
            // Face->Spine anchor drives neck/head from a lower spine bone; don't fight it with torso twist on neck.
            if (headFaceBindingMode == HeadFaceBindingMode.FaceDrivesSpine004 && _spine004 != null && _neck != null && tb.Bone == _neck)
                continue;

            Quaternion weightedCorrection = Quaternion.Slerp(Quaternion.identity, torsoCorrection, tb.Weight);
            if (maxRotationPerFrame < 180f)
            {
                float angle = Quaternion.Angle(Quaternion.identity, weightedCorrection);
                if (angle > maxRotationPerFrame && angle > 0.01f)
                    weightedCorrection = Quaternion.Slerp(Quaternion.identity, weightedCorrection, maxRotationPerFrame / angle);
            }

            Quaternion targetLocalRot = weightedCorrection * tb.BindModelLocalRot;
            tb.Bone.rotation = Quaternion.Slerp(tb.Bone.rotation, _rootTransform.rotation * targetLocalRot, Time.deltaTime * smoothSpeed);
        }
    }

    /// <summary>
    /// Rotates arms/legs in model-local space. The root yaw has already been applied,
    /// so no per-bone Z flips are needed.
    /// Confidence-gated (0.55 threshold) on all limbs, with Z-trust blending for
    /// regime-aware smoothing at different camera angles.
    /// </summary>
    private void ApplyLimbRotationsModelLocal()
    {
        float zTrust = PoseLandmarkMapping.ComputeZTrustFactor(_modelLocalPos);

        foreach (var rb in _bones)
        {
            if (rb.Bone == null) continue;
            if (useTorsoKinematics && IsTorsoDrivenBone(rb.Bone)) continue;

            // Head/neck are handled by ApplyHeadFromTorsoAndFaceModelLocal when driveHead is enabled.
            bool isNeck = (rb.From == "MID_SHOULDER" && rb.To == "NECK_VIRTUAL") ||
                          (rb.From == "NECK_VIRTUAL" && rb.To == "NOSE");
            if (isNeck) continue;

            if (!_modelLocalPos.TryGetValue(rb.From, out Vector3 from)) continue;
            if (!_modelLocalPos.TryGetValue(rb.To, out Vector3 to)) continue;

            const float minConfidence = 0.55f;
            if (_confidence.TryGetValue(rb.From, out float cf) && _confidence.TryGetValue(rb.To, out float ct))
            {
                if (cf < minConfidence || ct < minConfidence) continue;
            }

            Vector3 targetDir = to - from;
            if (targetDir.sqrMagnitude < 1e-6f) continue;
            targetDir.Normalize();

            // Avoid unstable 180° flip when bind and target are opposite.
            if (Vector3.Dot(rb.BindLocalDir, targetDir) < -0.999f) continue;

            Quaternion correction = Quaternion.FromToRotation(rb.BindLocalDir, targetDir);
            float effectiveBlend = rb.Blend * Mathf.Max(zTrust, 0.2f);
            correction = Quaternion.Slerp(Quaternion.identity, correction, effectiveBlend);

            // Anatomical joint limits: prevent knees/elbows/etc. from bending too far.
            correction = ClampJointCorrection(rb, correction);

            if (maxRotationPerFrame < 180f)
            {
                float angle = Quaternion.Angle(Quaternion.identity, correction);
                if (angle > maxRotationPerFrame && angle > 0.01f)
                    correction = Quaternion.Slerp(Quaternion.identity, correction, maxRotationPerFrame / angle);
            }

            Quaternion targetLocalRot = correction * rb.BindModelLocalRot;
            Quaternion targetWorldRot = _rootTransform.rotation * targetLocalRot;
            rb.Bone.rotation = Quaternion.Slerp(rb.Bone.rotation, targetWorldRot, Time.deltaTime * smoothSpeed);
        }
    }

    /// <summary>
    /// Clamps a bone correction to anatomically sensible limits based on the bone type.
    /// This is a soft guard against impossible poses (e.g. knee bending forward 180°).
    /// </summary>
    private Quaternion ClampJointCorrection(RuntimeBone rb, Quaternion correction)
    {
        float maxAngle = GetMaxJointAngle(rb);
        if (maxAngle >= 180f) return correction;

        float angle = Quaternion.Angle(Quaternion.identity, correction);
        if (angle <= maxAngle) return correction;
        if (angle < 0.01f) return correction;

        return Quaternion.Slerp(Quaternion.identity, correction, maxAngle / angle);
    }

    /// <summary>
    /// Returns the maximum allowed rotation angle from bind pose for the given bone.
    /// Spine/hip bones are not clamped; limb bones get approximate anatomical limits.
    /// </summary>
    private float GetMaxJointAngle(RuntimeBone rb)
    {
        switch (rb.BoneType)
        {
            // Legs
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.RightUpperLeg:
                return 120f;
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.RightLowerLeg:
                return 150f;
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.RightFoot:
                return 60f;

            // Arms
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.RightUpperArm:
                return 180f;
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.RightLowerArm:
                return 160f;
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightHand:
                return 90f;

            // Spine/head/neck: do not clamp
            default:
                return 180f;
        }
    }

    private bool IsTorsoDrivenBone(Transform bone)
    {
        for (int i = 0; i < _torsoBones.Count; i++)
        {
            if (_torsoBones[i].Bone == bone) return true;
        }
        return false;
    }

    private void CacheHeadReference()
    {
        if (_head == null || _rootTransform == null)
            return;

        _bindHeadWorldRot = _head.rotation;
        _bindHeadLocalRot = _head.localRotation;
        Quaternion invRoot = Quaternion.Inverse(_rootTransform.rotation);
        _bindHeadModelLocalRot = invRoot * _bindHeadWorldRot;
        if (_neck != null)
        {
            _bindHeadLocalToNeck = Quaternion.Inverse(_neck.rotation) * _head.rotation;
            _bindNeckLocalRot = _neck.localRotation;
            _bindNeckModelLocalRot = invRoot * _neck.rotation;
            _bindHeadLocalToNeckLocal = Quaternion.Inverse(_neck.localRotation) * _head.localRotation;
        }
        else
        {
            _bindHeadLocalToNeck = Quaternion.identity;
            _bindHeadLocalToNeckLocal = Quaternion.identity;
            _bindNeckModelLocalRot = Quaternion.identity;
        }

        if (_spine004 != null && _head != null)
        {
            _bindSpine004WorldRot = _spine004.rotation;
            _bindHeadLocalToSpine004Local = Quaternion.Inverse(_spine004.localRotation) * _head.localRotation;
        }

        // Build a stable bind frame for head orientation.
        Vector3 up = _rootTransform.up;
        Vector3 right = _rootTransform.right;

        if (TryGetBindTorsoAxes(out Vector3 torsoUp, out Vector3 torsoRight))
        {
            up = torsoUp;
            right = torsoRight;
        }

        Vector3 forward = Vector3.Cross(right, up).normalized;
        if (forward.sqrMagnitude < 1e-6f)
            forward = _rootTransform.forward;

        _bindHeadFrame = Quaternion.LookRotation(forward, up);

        _bindHeadFrameLocal = Quaternion.LookRotation(invRoot * forward, invRoot * up);
    }

    /// <summary>Find the transform that should carry back-of-head + face (often DEF-spine.004 or humanoid Chest).</summary>
    private void ResolveSpineFaceAnchor()
    {
        _spine004 = null;
        if (animator == null)
            return;

        if (spineFaceAnchorOverride != null)
        {
            _spine004 = spineFaceAnchorOverride;
            return;
        }

        var all = animator.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.name;
            if (string.IsNullOrEmpty(n)) continue;
            string lower = n.ToLowerInvariant();
            if (n == "DEF-spine.004" || lower.Contains("def-spine.004") || lower.Contains("spine.004"))
            {
                _spine004 = t;
                return;
            }
        }

        // Humanoid mapping: upper spine is often the practical "neck base" for mesh weights.
        _spine004 = animator.GetBoneTransform(HumanBodyBones.Chest);
        if (_spine004 == null)
            _spine004 = animator.GetBoneTransform(HumanBodyBones.UpperChest);
        if (_spine004 == null)
            _spine004 = animator.GetBoneTransform(HumanBodyBones.Spine);

        // Can't use head/neck as the anchor (would be degenerate).
        if (_spine004 != null && (_spine004 == _head || _spine004 == _neck))
            _spine004 = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
    }

        /// <summary>
    /// Snaps the rig back to its initial bind pose and clears runtime smoothing state.
    /// Call before loading a new set of recorded landmarks so leftover limb positions
    /// (e.g. legs from the previous form) don't bleed into the next playback.
    /// </summary>
    public void ResetPose()
    {
        if (!_ready) return;

        if (_rootTransform != null)
        {
            _rootTransform.position = _initialRootPosition;
            _rootTransform.rotation = _initialRootRotation;
            _rootTransform.localScale = _initialRootScale;
        }

        if (_hips != null)
            _hips.localPosition = _bindHipsLocalPos;

        foreach (var rb in _bones)
        {
            if (rb.Bone == null) continue;
            rb.Bone.rotation = rb.BindWorldRot;
        }

        if (_spine004 != null)
            _spine004.rotation = _bindSpine004WorldRot;

        _currentBodyYaw = 0f;
        _isFirstYawFrame = true;
        _headForwardSmoothed = Vector3.zero;
        _rawHeadFacingXZ = Vector3.zero;
        _targetRootPosition = _initialRootPosition;
        _targetScale = _initialRootScale.x;
    }

private void RefreshSpineFaceAnchorBindOffsets()
    {
        _bindNeckLocalToSpine004 = Quaternion.identity;
        _bindHeadLocalToSpine004 = Quaternion.identity;
        if (_spine004 == null || _head == null)
            return;

        _bindHeadLocalToSpine004 = Quaternion.Inverse(_spine004.rotation) * _head.rotation;
        _bindHeadLocalToSpine004Local = Quaternion.Inverse(_spine004.localRotation) * _head.localRotation;
        if (_neck != null)
        {
            _bindNeckLocalToSpine004 = Quaternion.Inverse(_spine004.rotation) * _neck.rotation;
            _bindNeckLocalToSpine004Local = Quaternion.Inverse(_spine004.localRotation) * _neck.localRotation;
        }
    }

    private void ApplyHeadFromTorsoAndFaceModelLocal()
    {
        if (_head == null || _rootTransform == null) return;
        if (!_modelLocalPos.TryGetValue("LEFT_EAR", out Vector3 leftEar)) return;
        if (!_modelLocalPos.TryGetValue("RIGHT_EAR", out Vector3 rightEar)) return;
        if (!_modelLocalPos.TryGetValue("NOSE", out Vector3 nose)) return;

        Vector3 earMid = 0.5f * (leftEar + rightEar);
        Vector3 faceForwardLocal = nose - earMid; // back-of-skull -> face direction
        Vector3 headRightLocal = rightEar - leftEar;
        if (faceForwardLocal.sqrMagnitude < 1e-6f || headRightLocal.sqrMagnitude < 1e-6f) return;

        faceForwardLocal.Normalize();
        headRightLocal.Normalize();

        // Build an orthonormal frame in model-local space, keeping up aligned with the model's Y axis.
        Vector3 up = Vector3.up;
        Vector3 right = Vector3.ProjectOnPlane(headRightLocal, up).normalized;
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.Cross(up, faceForwardLocal).normalized;
        if (right.sqrMagnitude < 1e-6f) return;

        Vector3 forward = Vector3.Cross(right, up).normalized;
        if (Vector3.Dot(forward, faceForwardLocal) < 0f) forward = -forward;
        if (forward.sqrMagnitude < 1e-6f) return;

        Quaternion targetHeadFrameLocal = Quaternion.LookRotation(forward, up);
        Quaternion correction = targetHeadFrameLocal * Quaternion.Inverse(_bindHeadFrameLocal);
        correction = Quaternion.Slerp(Quaternion.identity, correction, torsoKinematicBlend);

        if (maxRotationPerFrame < 180f)
        {
            float angle = Quaternion.Angle(Quaternion.identity, correction);
            if (angle > maxRotationPerFrame && angle > 0.01f)
                correction = Quaternion.Slerp(Quaternion.identity, correction, maxRotationPerFrame / angle);
        }

        Quaternion desiredHeadLocalRot = correction * _bindHeadModelLocalRot;
        float dtSmooth = Time.deltaTime * smoothSpeed;

        if (_neck == null)
        {
            _head.rotation = Quaternion.Slerp(_head.rotation, _rootTransform.rotation * desiredHeadLocalRot, dtSmooth);
            return;
        }

        switch (headFaceBindingMode)
        {
            case HeadFaceBindingMode.FaceDrivesHead:
            case HeadFaceBindingMode.FaceDrivesNeck:
            {
                Quaternion desiredNeckLocalRot = desiredHeadLocalRot * Quaternion.Inverse(_bindHeadLocalToNeck);
                desiredNeckLocalRot = Quaternion.Slerp(_bindNeckModelLocalRot, desiredNeckLocalRot, headFaceBlend);

                if (maxRotationPerFrame < 180f)
                {
                    Quaternion neckCorrection = desiredNeckLocalRot * Quaternion.Inverse(_bindNeckModelLocalRot);
                    float angle = Quaternion.Angle(Quaternion.identity, neckCorrection);
                    if (angle > maxRotationPerFrame && angle > 0.01f)
                        neckCorrection = Quaternion.Slerp(Quaternion.identity, neckCorrection, maxRotationPerFrame / angle);
                    desiredNeckLocalRot = neckCorrection * _bindNeckModelLocalRot;
                }

                _neck.rotation = Quaternion.Slerp(_neck.rotation, _rootTransform.rotation * desiredNeckLocalRot, dtSmooth);
                Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                break;
            }

            case HeadFaceBindingMode.LockHeadToNeck:
            {
                Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                break;
            }

            case HeadFaceBindingMode.FaceDrivesSpine004:
            {
                if (_spine004 == null)
                {
                    if (!_loggedSpineFaceAnchorMissing)
                    {
                        _loggedSpineFaceAnchorMissing = true;
                        Debug.LogWarning("[HumanoidPoseDriver] Face->Spine004: no spine face anchor (assign Spine Face Anchor Override on the driver, or check humanoid Chest/Spine mapping). Falling back to neck.");
                    }
                    if (_neck != null)
                    {
                        Quaternion desiredNeckLocalRot = desiredHeadLocalRot * Quaternion.Inverse(_bindHeadLocalToNeck);
                        desiredNeckLocalRot = Quaternion.Slerp(_bindNeckModelLocalRot, desiredNeckLocalRot, headFaceBlend);
                        _neck.rotation = Quaternion.Slerp(_neck.rotation, _rootTransform.rotation * desiredNeckLocalRot, dtSmooth);
                        Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                        _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                    }
                    else
                        _head.rotation = Quaternion.Slerp(_head.rotation, _rootTransform.rotation * desiredHeadLocalRot, dtSmooth);
                    break;
                }

                Quaternion desiredSpine004LocalRot = desiredHeadLocalRot * Quaternion.Inverse(_bindHeadLocalToSpine004Local);
                desiredSpine004LocalRot = Quaternion.Slerp(_spine004.localRotation, desiredSpine004LocalRot, headFaceBlend);

                if (maxRotationPerFrame < 180f)
                {
                    Quaternion delta = desiredSpine004LocalRot * Quaternion.Inverse(_spine004.localRotation);
                    float angle = Quaternion.Angle(Quaternion.identity, delta);
                    if (angle > maxRotationPerFrame && angle > 0.01f)
                    {
                        Quaternion rel = Quaternion.Slerp(Quaternion.identity, delta, maxRotationPerFrame / angle);
                        desiredSpine004LocalRot = rel * _spine004.localRotation;
                    }
                }

                _spine004.rotation = Quaternion.Slerp(_spine004.rotation, _rootTransform.rotation * desiredSpine004LocalRot, dtSmooth);

                if (_neck != null)
                {
                    Quaternion neckTargetLocalRot = _spine004.localRotation * _bindNeckLocalToSpine004Local;
                    _neck.rotation = Quaternion.Slerp(_neck.rotation, _rootTransform.rotation * neckTargetLocalRot, dtSmooth);
                }
                Quaternion headTargetLocalRot = _spine004.localRotation * _bindHeadLocalToSpine004Local;
                _head.rotation = Quaternion.Slerp(_head.rotation, _rootTransform.rotation * headTargetLocalRot, dtSmooth);
                break;
            }
        }
    }

    private void ApplyTorsoEndpointTranslation()
    {
        if (_rootTransform == null) return;
        if (_hips == null) return;
        if (!_pos.TryGetValue("MID_HIP", out Vector3 targetMidHip)) return;
        if (!_pos.TryGetValue("MID_SHOULDER", out Vector3 targetMidShoulder)) return;
        if (_leftUpperArm == null || _rightUpperArm == null) return;

        Vector3 currentMidHip = _hips.position;
        Vector3 currentMidShoulder = 0.5f * (_leftUpperArm.position + _rightUpperArm.position);

        Vector3 hipError = targetMidHip - currentMidHip;
        Vector3 shoulderError = targetMidShoulder - currentMidShoulder;

        float wShoulder = Mathf.Clamp01(torsoTranslationShoulderWeight);
        float wHip = 1f - wShoulder;

        Vector3 endpointError = wHip * hipError + wShoulder * shoulderError;
        _targetRootPosition = _rootTransform.position + endpointError;

        float maxDist = 20f;
        _targetRootPosition.x = Mathf.Clamp(_targetRootPosition.x, -maxDist, maxDist);
        _targetRootPosition.y = Mathf.Clamp(_targetRootPosition.y, -maxDist, maxDist);
        _targetRootPosition.z = Mathf.Clamp(_targetRootPosition.z, -maxDist, maxDist);

        _rootTransform.position = Vector3.Lerp(_rootTransform.position, _targetRootPosition, Time.deltaTime * smoothSpeed);
    }

    private float GetHumanLikeTorsoWeight(Transform bone, float fallbackT)
    {
        if (bone == null) return Mathf.Lerp(0.35f, 1f, fallbackT);
        if (_hips != null && bone == _hips) return 0.35f;
        if (_neck != null && bone == _neck) return 0.55f;

        var spine = animator != null ? animator.GetBoneTransform(HumanBodyBones.Spine) : null;
        var chest = animator != null ? animator.GetBoneTransform(HumanBodyBones.Chest) : null;
        var upperChest = animator != null ? animator.GetBoneTransform(HumanBodyBones.UpperChest) : null;
        if (spine != null && bone == spine) return 0.5f;
        if (chest != null && bone == chest) return 0.7f;
        if (upperChest != null && bone == upperChest) return 0.85f;

        return Mathf.Lerp(0.35f, 1f, fallbackT);
    }

    private static void SwapPos(Dictionary<string, Vector3> pos, string a, string b)
    {
        if (!pos.TryGetValue(a, out Vector3 va) || !pos.TryGetValue(b, out Vector3 vb)) return;
        pos[a] = vb;
        pos[b] = va;
    }

    private void ApplyDebugArmLandmarkSwap()
    {
        SwapPos(_pos, "LEFT_SHOULDER", "RIGHT_SHOULDER");
        SwapPos(_pos, "LEFT_ELBOW", "RIGHT_ELBOW");
        SwapPos(_pos, "LEFT_WRIST", "RIGHT_WRIST");
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            _pos["MID_SHOULDER"] = 0.5f * (ls + rs);
    }

    private void ComputeFigureBounds()
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        float sumX = 0, sumY = 0, sumZ = 0;
        int count = 0;
        foreach (var kvp in _pos)
        {
            if (kvp.Key.StartsWith("MID_") || kvp.Key == "NECK_VIRTUAL") continue;
            sumX += kvp.Value.x;
            sumY += kvp.Value.y;
            sumZ += kvp.Value.z;
            if (kvp.Value.y < minY) minY = kvp.Value.y;
            if (kvp.Value.y > maxY) maxY = kvp.Value.y;
            count++;
        }
        if (count > 0)
        {
            _figureCenter = new Vector3(sumX / count, sumY / count, sumZ / count);
            _figureHeight = Mathf.Max(maxY - minY, 0.5f);
        }
    }
}
