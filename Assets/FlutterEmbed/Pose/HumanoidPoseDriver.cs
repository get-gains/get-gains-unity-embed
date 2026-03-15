using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid rig from MediaPipe/MLKit landmark pose data.
///
/// Refactored for stability using ideas from:
/// - ganeshsar/UnityPythonMediaPipeAvatar: reference pose (bind) + delta rotation, spine dampening (0.25), Tick smoothing.
/// - BrandonBartram98/MediaPipe-UnitySolver: per-bone dampener and Slerp(rotation, target, lerpAmount).
///
/// Strategy:
/// 1. At Start(), store each bone's bind-pose world rotation and bind direction (reference).
/// 2. Each frame: compute target direction from landmarks; rotation = FromToRotation(bindDir, targetDir) * bindWorldRot.
/// 3. Apply per-bone blend (spine/hips softer) and temporal Slerp so motion is stable, not jittery.
/// 4. No per-frame reset to bind — we always target from the same reference, avoiding drift and nonsense poses.
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Scale and space")]
    [Tooltip("Must match PoseStickFigureRenderer.scale so positions align.")]
    [SerializeField] private float poseScale = 5f;
    [Tooltip("Scale for landmark Z (depth).")]
    [SerializeField] private float poseDepthScale = 5f;
    [Tooltip("Invert landmark X so left/right matches 2D (person left = Unity -X when facing +Z).")]
    [SerializeField] private bool invertLandmarkX = true;
    [Tooltip("MLKit: negative Z = toward camera (front). Invert so front maps to Unity +Z (in front of character).")]
    [SerializeField] private bool invertLandmarkZ = true;
    [Tooltip("Flatten only Hips (spine) direction to XY to avoid Z-twist on torso.")]
    [SerializeField] private bool flattenDirectionsToXY = true;
    [Tooltip("If rig faces -Z (common), flip limb forward so bicep curl is in front, not behind.")]
    [SerializeField] private bool flipLimbForwardZ = true;
    [Tooltip("MLKit z is not 0-1; divide raw z by this so depth stays sensible (e.g. 100).")]
    [SerializeField] private float zNormalizeScale = 100f;
    [Tooltip("Clamp normalized X,Y to this range so out-of-frame landmarks don't blow up (MediaPipe can return outside 0-1).")]
    [SerializeField] private float xyClampMin = -0.2f;
    [SerializeField] private float xyClampMax = 1.2f;

    [Header("Stability (reference: ganeshsar, MediaPipe-UnitySolver)")]
    [Tooltip("Lerp speed toward target rotation per second (higher = snappier). ~10–15 stable.")]
    [SerializeField] private float smoothSpeed = 12f;
    [Tooltip("Max rotation change per bone per frame (degrees) to avoid single-frame spikes.")]
    [SerializeField] private float maxRotationPerFrame = 90f;
    [Tooltip("Blend for spine/hips (0.25–0.4). Lower = more stable, less follow.")]
    [Range(0.2f, 0.6f)]
    [SerializeField] private float spineBlend = 0.35f;
    [Tooltip("Blend for limbs (0.6–1). Higher = more responsive.")]
    [Range(0.5f, 1f)]
    [SerializeField] private float limbBlend = 0.75f;

    private static readonly (HumanBodyBones Bone, HumanBodyBones Child, string From, string To)[] BoneMap =
    {
        (HumanBodyBones.Hips,           HumanBodyBones.Spine,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Neck,           HumanBodyBones.Head,           "MID_SHOULDER",  "HEAD_CENTER"),

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

    private Transform _rootTransform;
    private Transform _hips;
    private Vector3 _bindHipsLocalPos;
    private float _bindHeight;
    private readonly List<RuntimeBone> _bones = new List<RuntimeBone>();
    private readonly Dictionary<string, Vector3> _pos = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, float> _confidence = new Dictionary<string, float>();
    private bool _ready;

    private Vector3 _figureCenter;
    private float _figureHeight = 1f;
    private Vector3 _targetRootPosition;
    private float _targetScale = 1f;

    public Vector3 FigureCenter => _figureCenter;
    public float FigureHeight => _figureHeight;
    public Vector3 HipsWorldPosition => _hips != null ? _hips.position : (_rootTransform != null ? _rootTransform.position : Vector3.zero);

    private void Start()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) { Debug.LogWarning("[HumanoidPoseDriver] No Animator."); return; }

        _rootTransform = animator.transform;
        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (_hips != null) _bindHipsLocalPos = _hips.localPosition;

        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        if (head != null)
        {
            float footY = float.MaxValue;
            if (lFoot != null) footY = Mathf.Min(footY, lFoot.position.y);
            if (rFoot != null) footY = Mathf.Min(footY, rFoot.position.y);
            if (footY < float.MaxValue)
                _bindHeight = head.position.y - footY;
        }
        if (_bindHeight < 0.01f) _bindHeight = 1.8f;

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
            bool isNeck = m.From == "MID_SHOULDER" && m.To == "HEAD_CENTER";
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
        Debug.Log($"[HumanoidPoseDriver] Ready: {_bones.Count} bones, bindHeight={_bindHeight:F3}, smooth={smoothSpeed}, spineBlend={spineBlend}, limbBlend={limbBlend}");
    }

    public void ApplyPose(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (!_ready || landmarks == null || landmarks.Count == 0) return;

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

        if (_hips != null && _pos.TryGetValue("MID_HIP", out Vector3 midHip))
        {
            Vector3 hipsWorld = _hips.position;
            _targetRootPosition = _rootTransform.position + (midHip - hipsWorld);
            float maxDist = 20f;
            _targetRootPosition.x = Mathf.Clamp(_targetRootPosition.x, -maxDist, maxDist);
            _targetRootPosition.y = Mathf.Clamp(_targetRootPosition.y, -maxDist, maxDist);
            _targetRootPosition.z = Mathf.Clamp(_targetRootPosition.z, -maxDist, maxDist);
            _rootTransform.position = Vector3.Lerp(_rootTransform.position, _targetRootPosition, Time.deltaTime * smoothSpeed);
        }

        // Per-bone: target rotation from bind reference (ganeshsar-style), then smooth (MediaPipe-style Slerp)
        bool isArmBone(string from, string to) =>
            (from.Contains("SHOULDER") && to.Contains("ELBOW")) || (from.Contains("ELBOW") && to.Contains("WRIST"));
        bool isLimbBone(string from, string to) =>
            isArmBone(from, to) ||
            (from.Contains("HIP") && to.Contains("KNEE")) || (from.Contains("KNEE") && (to.Contains("ANKLE") || to.Contains("FOOT")));

        foreach (var rb in _bones)
        {
            if (rb.Bone == null) continue;
            if (!_pos.TryGetValue(rb.From, out Vector3 from)) continue;
            if (!_pos.TryGetValue(rb.To, out Vector3 to)) continue;

            if (rb.From == "MID_SHOULDER" && rb.To == "HEAD_CENTER") continue; // neck: skip to avoid face/skull disconnect

            bool isArm = isArmBone(rb.From, rb.To);
            if (isArm && _confidence.TryGetValue(rb.From, out float cf) && _confidence.TryGetValue(rb.To, out float ct))
            {
                if (cf < 0.4f || ct < 0.4f) continue;
            }

            bool isHips = rb.From == "MID_HIP" && rb.To == "MID_SHOULDER";
            bool flatten = flattenDirectionsToXY && isHips;

            Vector3 targetDir = to - from;
            if (flatten) targetDir.z = 0f;
            if (flipLimbForwardZ && isLimbBone(rb.From, rb.To)) targetDir.z = -targetDir.z; // rig often faces -Z; pose +Z (front) → -Z = limbs in front
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
        float zScale = Mathf.Max(0.001f, zNormalizeScale);

        foreach (var kvp in landmarks)
        {
            float rawX = (float)kvp.Value.X;
            float rawY = (float)kvp.Value.Y;
            float rawZ = (float)kvp.Value.Z;

            if (!IsValidFloat(rawX)) rawX = 0.5f;
            if (!IsValidFloat(rawY)) rawY = 0.5f;
            if (!IsValidFloat(rawZ)) rawZ = 0f;
            rawX = Mathf.Clamp(rawX, xyClampMin, xyClampMax);
            rawY = Mathf.Clamp(rawY, xyClampMin, xyClampMax);
            rawZ = Mathf.Clamp(rawZ / zScale, -1f, 1f);

            float x = (invertLandmarkX ? (0.5f - rawX) : (rawX - 0.5f)) * poseScale;
            float y = (0.5f - rawY) * poseScale;
            float z = (invertLandmarkZ ? -rawZ : rawZ) * poseDepthScale;

            _pos[kvp.Key] = new Vector3(x, y, z);
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
    }

    private static bool IsValidFloat(float f)
    {
        return !float.IsNaN(f) && !float.IsInfinity(f);
    }

    private void ComputeFigureBounds()
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        float sumX = 0, sumY = 0, sumZ = 0;
        int count = 0;
        foreach (var kvp in _pos)
        {
            if (kvp.Key.StartsWith("MID_")) continue;
            sumX += kvp.Value.x; sumY += kvp.Value.y; sumZ += kvp.Value.z;
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
