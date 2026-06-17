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
    [Tooltip("Flatten only Hips (spine) direction to XY to avoid Z-twist on torso.")]
    [SerializeField] private bool flattenDirectionsToXY = true;
    [Tooltip("If rig faces -Z (common), flip limb forward so bicep curl is in front, not behind.")]
    [SerializeField] private bool flipLimbForwardZ = true;
    [Tooltip("Drive neck/head from HEAD_CENTER so skull follows face, not anchored.")]
    [SerializeField] private bool driveHead = true;
    [Tooltip("Flip head/neck Z if face points backwards relative to camera.")]
    [SerializeField] private bool invertHeadZ = false;
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
        PoseLandmarkMapping.TorsoDebugFlattenMode.UniformZ;

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
        public string From;
        public string To;
        public Vector3 BindLocalDir;
        public Quaternion BindLocalRot;
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
    private bool _loggedSpineFaceAnchorMissing;

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
                    + "Generic rigs cannot use HumanBodyBones retargeting — the cyan stick figure will show instead.");
                _loggedHumanoidWarning = true;
            }
            return false;
        }

        _rootTransform = animator.transform;
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

            bool isHips = m.From == "MID_HIP" && m.To == "MID_SHOULDER";
            bool isNeck = (m.From == "MID_SHOULDER" && m.To == "NECK_VIRTUAL") ||
                          (m.From == "NECK_VIRTUAL" && m.To == "NOSE");
            float blend = (isHips || isNeck) ? spineBlend : limbBlend;

            _bones.Add(new RuntimeBone
            {
                Bone = bone,
                From = m.From,
                To = m.To,
                BindLocalDir = localDir,
                BindLocalRot = bone.localRotation,
                BindWorldRot = bindWorldRot,
                BindWorldDir = bindWorldDir,
                Blend = blend,
            });
        }

        _ready = _bones.Count > 0;
        _targetRootPosition = _rootTransform.position;
        _targetScale = _rootTransform.localScale.x;
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

        // Root scale and position (smoothed)
        float scale = (_bindHeight > 0.01f && _figureHeight > 0.1f)
            ? Mathf.Clamp(_figureHeight / _bindHeight, 0.85f, 1.15f)
            : 1f;
        _targetScale = scale;
        _rootTransform.localScale = Vector3.one * Mathf.Lerp(_rootTransform.localScale.x, _targetScale, Time.deltaTime * smoothSpeed);

        // When torso kinematics are enabled, root translation is handled by the kinematic solve
        // to better satisfy both hip and shoulder endpoints.
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

        if (useTorsoKinematics)
            ApplyTorsoKinematics();

        // Per-bone: target rotation from bind reference (ganeshsar-style), then smooth (MediaPipe-style Slerp)
        bool isArmBone(string from, string to) =>
            (from.Contains("SHOULDER") && to.Contains("ELBOW")) || (from.Contains("ELBOW") && to.Contains("WRIST"));
        bool isLimbBone(string from, string to) =>
            isArmBone(from, to) ||
            (from.Contains("HIP") && to.Contains("KNEE")) || (from.Contains("KNEE") && (to.Contains("ANKLE") || to.Contains("FOOT")));

        foreach (var rb in _bones)
        {
            if (rb.Bone == null) continue;
            if (useTorsoKinematics && IsTorsoDrivenBone(rb.Bone)) continue;
            if (!_pos.TryGetValue(rb.From, out Vector3 from)) continue;
            if (!_pos.TryGetValue(rb.To, out Vector3 to)) continue;

            bool isNeck = (rb.From == "MID_SHOULDER" && rb.To == "NECK_VIRTUAL") ||
                          (rb.From == "NECK_VIRTUAL" && rb.To == "NOSE");
            if (isNeck && !driveHead) continue; // allow disabling head driving if it misbehaves

            bool isArm = isArmBone(rb.From, rb.To);
            if (isArm && _confidence.TryGetValue(rb.From, out float cf) && _confidence.TryGetValue(rb.To, out float ct))
            {
                if (cf < 0.4f || ct < 0.4f) continue;
            }

            bool isHips = rb.From == "MID_HIP" && rb.To == "MID_SHOULDER";
            bool flatten = flattenDirectionsToXY && isHips;

            Vector3 targetDir = to - from;
            if (flatten) targetDir.z = 0f;

            // Flip limbs forward/back in Z for arms/legs only; neck uses its own invertHeadZ flag.
            if (flipLimbForwardZ && isLimbBone(rb.From, rb.To) && !isNeck)
                targetDir.z = -targetDir.z;

            if (invertHeadZ && isNeck)
                targetDir.z = -targetDir.z;

            if (targetDir.sqrMagnitude < 1e-6f) continue; // zero-length segment, skip
            targetDir.Normalize();

            // Pose space: +Z = person toward camera (front). Transform to world so rig forward matches (arms in front, not behind).
            Vector3 targetDirWorld = _rootTransform != null ? _rootTransform.TransformDirection(targetDir) : targetDir;
            targetDirWorld.Normalize();

            // Avoid unstable 180° flip when bind and target are opposite (humanoid sanity)
            if (Vector3.Dot(rb.BindWorldDir, targetDirWorld) < -0.999f) continue;

            // Delta from bind reference (ganeshsar: deltaRotTracked * initialRotation)
            Quaternion correction = Quaternion.FromToRotation(rb.BindWorldDir, targetDirWorld);
            correction = Quaternion.Slerp(Quaternion.identity, correction, rb.Blend);

            if (maxRotationPerFrame < 180f)
            {
                float angle = Quaternion.Angle(Quaternion.identity, correction);
                if (angle > maxRotationPerFrame && angle > 0.01f)
                    correction = Quaternion.Slerp(Quaternion.identity, correction, maxRotationPerFrame / angle);
            }

            Quaternion targetWorldRot = correction * rb.BindWorldRot;
            rb.Bone.rotation = Quaternion.Slerp(rb.Bone.rotation, targetWorldRot, Time.deltaTime * smoothSpeed);
        }
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
        PoseLandmarkMapping.ApplyInvertArmWorldZ(_pos, _debugInvertArmDepthZ);
        PoseLandmarkMapping.ApplyInvertLegWorldZ(_pos, _debugInvertLegDepthZ);
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

        for (int i = 0; i < n; i++)
        {
            float w = GetHumanLikeTorsoWeight(chain[i], n == 1 ? i : i / (float)(n - 1));
            _torsoBones.Add(new TorsoRuntimeBone
            {
                Bone = chain[i],
                BindWorldRot = chain[i].rotation,
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
        if (!_pos.TryGetValue("LEFT_SHOULDER", out Vector3 lShoulder)) return;
        if (!_pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rShoulder)) return;
        if (!_pos.TryGetValue("LEFT_HIP", out Vector3 lHip)) return;
        if (!_pos.TryGetValue("RIGHT_HIP", out Vector3 rHip)) return;

        Vector3 midShoulder = 0.5f * (lShoulder + rShoulder);
        Vector3 midHip = 0.5f * (lHip + rHip);
        Vector3 torsoUpLocal = (midShoulder - midHip);
        Vector3 torsoRightLocal = (rShoulder - lShoulder);
        if (torsoUpLocal.sqrMagnitude < 1e-6f || torsoRightLocal.sqrMagnitude < 1e-6f) return;

        torsoUpLocal.Normalize();
        torsoRightLocal.Normalize();
        Vector3 torsoForwardLocal = Vector3.Cross(torsoRightLocal, torsoUpLocal).normalized;
        if (torsoForwardLocal.sqrMagnitude < 1e-6f) return;

        Vector3 torsoUpWorld = _rootTransform.TransformDirection(torsoUpLocal).normalized;
        Vector3 torsoForwardWorld = _rootTransform.TransformDirection(torsoForwardLocal).normalized;
        Quaternion targetTorsoFrame = Quaternion.LookRotation(torsoForwardWorld, torsoUpWorld);
        Quaternion torsoCorrection = targetTorsoFrame * Quaternion.Inverse(_bindTorsoFrame);
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

            Quaternion targetWorldRot = weightedCorrection * tb.BindWorldRot;
            tb.Bone.rotation = Quaternion.Slerp(tb.Bone.rotation, targetWorldRot, Time.deltaTime * smoothSpeed);
        }

        ApplyTorsoEndpointTranslation();

        // Use torso frame + face landmarks to rotate the back of the skull toward the face.
        if (driveHead)
            ApplyHeadFromTorsoAndFace(torsoUpWorld);
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
        if (_neck != null)
            _bindHeadLocalToNeck = Quaternion.Inverse(_neck.rotation) * _head.rotation;

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

    private void RefreshSpineFaceAnchorBindOffsets()
    {
        _bindNeckLocalToSpine004 = Quaternion.identity;
        _bindHeadLocalToSpine004 = Quaternion.identity;
        if (_spine004 == null || _head == null)
            return;

        _bindHeadLocalToSpine004 = Quaternion.Inverse(_spine004.rotation) * _head.rotation;
        if (_neck != null)
            _bindNeckLocalToSpine004 = Quaternion.Inverse(_spine004.rotation) * _neck.rotation;
    }

    private void ApplyHeadFromTorsoAndFace(Vector3 torsoUpWorld)
    {
        if (_head == null || _rootTransform == null)
            return;
        if (!_pos.TryGetValue("LEFT_EAR", out Vector3 leftEar))
            return;
        if (!_pos.TryGetValue("RIGHT_EAR", out Vector3 rightEar))
            return;
        if (!_pos.TryGetValue("NOSE", out Vector3 nose))
            return;

        Vector3 earMid = 0.5f * (leftEar + rightEar);
        Vector3 faceForwardLocal = nose - earMid; // back-of-skull -> face direction
        Vector3 headRightLocal = rightEar - leftEar;
        if (faceForwardLocal.sqrMagnitude < 1e-6f || headRightLocal.sqrMagnitude < 1e-6f)
            return;

        Vector3 faceForwardWorld = _rootTransform.TransformDirection(faceForwardLocal.normalized);
        Vector3 headRightWorld = _rootTransform.TransformDirection(headRightLocal.normalized);

        // Rebuild orthonormal frame while anchoring up to torso so head follows spine kinematics.
        Vector3 up = torsoUpWorld.sqrMagnitude > 1e-6f ? torsoUpWorld.normalized : _rootTransform.up;
        Vector3 right = Vector3.ProjectOnPlane(headRightWorld, up).normalized;
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.Cross(up, faceForwardWorld).normalized;
        if (right.sqrMagnitude < 1e-6f)
            return;

        Vector3 forward = Vector3.Cross(right, up).normalized;
        if (Vector3.Dot(forward, faceForwardWorld) < 0f)
            forward = -forward;
        if (forward.sqrMagnitude < 1e-6f)
            return;

        Quaternion targetHeadFrame = Quaternion.LookRotation(forward, up);
        Quaternion correction = targetHeadFrame * Quaternion.Inverse(_bindHeadFrame);
        correction = Quaternion.Slerp(Quaternion.identity, correction, torsoKinematicBlend);

        if (maxRotationPerFrame < 180f)
        {
            float angle = Quaternion.Angle(Quaternion.identity, correction);
            if (angle > maxRotationPerFrame && angle > 0.01f)
                correction = Quaternion.Slerp(Quaternion.identity, correction, maxRotationPerFrame / angle);
        }

        // "Face direction" results in a desired HEAD world rotation.
        Quaternion desiredHeadWorldRot = correction * _bindHeadWorldRot;

        // If we can't find neck, fall back to face->head behavior.
        if (_neck == null)
        {
            _head.rotation = Quaternion.Slerp(_head.rotation, desiredHeadWorldRot, Time.deltaTime * smoothSpeed);
            return;
        }

        float dtSmooth = Time.deltaTime * smoothSpeed;

        switch (headFaceBindingMode)
        {
            case HeadFaceBindingMode.FaceDrivesHead:
            {
                // Group face + neck together: face aims neck, then head is locked to neck.
                Quaternion desiredNeckWorldRot = desiredHeadWorldRot * Quaternion.Inverse(_bindHeadLocalToNeck);

                // Interpret headFaceBlend as "how much neck follows the face".
                desiredNeckWorldRot = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, headFaceBlend);

                if (maxRotationPerFrame < 180f)
                {
                    Quaternion neckCorrection = desiredNeckWorldRot * Quaternion.Inverse(_neck.rotation);
                    float angle = Quaternion.Angle(Quaternion.identity, neckCorrection);
                    if (angle > maxRotationPerFrame && angle > 0.01f)
                        neckCorrection = Quaternion.Slerp(Quaternion.identity, neckCorrection, maxRotationPerFrame / angle);
                    desiredNeckWorldRot = neckCorrection * _neck.rotation;
                }

                _neck.rotation = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, dtSmooth);

                // Lock head to neck (prevents stretched/stacked head motion).
                Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                break;
            }

            case HeadFaceBindingMode.FaceDrivesNeck:
            {
                // Find neck rotation such that: neck * (headLocalToNeck bind) ~= desiredHeadWorldRot.
                Quaternion desiredNeckWorldRot = desiredHeadWorldRot * Quaternion.Inverse(_bindHeadLocalToNeck);

                // Interpret headFaceBlend as "how much neck follows the face".
                desiredNeckWorldRot = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, headFaceBlend);

                if (maxRotationPerFrame < 180f)
                {
                    Quaternion neckCorrection = desiredNeckWorldRot * Quaternion.Inverse(_neck.rotation);
                    float angle = Quaternion.Angle(Quaternion.identity, neckCorrection);
                    if (angle > maxRotationPerFrame && angle > 0.01f)
                        neckCorrection = Quaternion.Slerp(Quaternion.identity, neckCorrection, maxRotationPerFrame / angle);
                    desiredNeckWorldRot = neckCorrection * _neck.rotation;
                }

                _neck.rotation = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, dtSmooth);

                // Lock head to neck (so head mesh doesn't "stretch" independently).
                Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                break;
            }

            case HeadFaceBindingMode.LockHeadToNeck:
            {
                // Ignore face aim for head and just lock to current neck posture.
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
                    // Fallback: if we can't find the anchor bone, behave like Face->Neck.
                    if (_neck != null)
                    {
                        Quaternion desiredNeckWorldRot = desiredHeadWorldRot * Quaternion.Inverse(_bindHeadLocalToNeck);
                        desiredNeckWorldRot = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, headFaceBlend);
                        _neck.rotation = Quaternion.Slerp(_neck.rotation, desiredNeckWorldRot, dtSmooth);
                        Quaternion lockedHeadWorldRot = _neck.rotation * _bindHeadLocalToNeck;
                        _head.rotation = Quaternion.Slerp(_head.rotation, lockedHeadWorldRot, dtSmooth);
                    }
                    else
                        _head.rotation = Quaternion.Slerp(_head.rotation, desiredHeadWorldRot, dtSmooth);
                    break;
                }

                // Aim the spine anchor so the head matches desiredHeadWorldRot (same blend as neck modes).
                Quaternion desiredSpine004WorldRot = desiredHeadWorldRot * Quaternion.Inverse(_bindHeadLocalToSpine004);
                desiredSpine004WorldRot = Quaternion.Slerp(_spine004.rotation, desiredSpine004WorldRot, headFaceBlend);

                if (maxRotationPerFrame < 180f)
                {
                    Quaternion delta = desiredSpine004WorldRot * Quaternion.Inverse(_spine004.rotation);
                    float angle = Quaternion.Angle(Quaternion.identity, delta);
                    if (angle > maxRotationPerFrame && angle > 0.01f)
                    {
                        Quaternion rel = Quaternion.Slerp(Quaternion.identity, delta, maxRotationPerFrame / angle);
                        desiredSpine004WorldRot = rel * _spine004.rotation;
                    }
                }

                _spine004.rotation = Quaternion.Slerp(_spine004.rotation, desiredSpine004WorldRot, dtSmooth);

                // Glue neck then head to anchor (parent chain order) so face verts stay with back-of-head verts.
                if (_neck != null)
                {
                    Quaternion neckTargetWorldRot = _spine004.rotation * _bindNeckLocalToSpine004;
                    _neck.rotation = Quaternion.Slerp(_neck.rotation, neckTargetWorldRot, dtSmooth);
                }
                Quaternion headTargetWorldRot = _spine004.rotation * _bindHeadLocalToSpine004;
                _head.rotation = Quaternion.Slerp(_head.rotation, headTargetWorldRot, dtSmooth);
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
