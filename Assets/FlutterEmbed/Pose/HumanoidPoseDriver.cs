using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid rig from MediaPipe 33-landmark pose data.
///
/// Strategy: ROTATION-ONLY on bones + SCALE the root to match landmark proportions.
/// - Bones keep their natural hierarchy and lengths (mesh stays intact).
/// - Root transform is scaled so the model's height matches the landmark figure height.
/// - Root is positioned so hips land at MID_HIP.
/// - Each bone is rotated so its natural child direction aligns with the landmark direction.
/// - Processed root-to-leaf so parent rotations cascade correctly.
/// - Root height scale uses Y span only; Z affects joint positions for 3D bone directions, not overall scale.
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Scale")]
    [Tooltip("Must match PoseStickFigureRenderer.scale so positions align.")]
    [SerializeField] private float poseScale = 5f;

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
        // Spine chain
        (HumanBodyBones.Hips,           HumanBodyBones.Spine,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Spine,          HumanBodyBones.Chest,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Chest,          HumanBodyBones.UpperChest,     "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.UpperChest,     HumanBodyBones.Neck,           "MID_HIP",       "MID_SHOULDER"),
        // Neck takes shoulder→virtual neck; Head takes virtual neck→nose (Head was never rotated before).
        (HumanBodyBones.Neck,           HumanBodyBones.Head,           "MID_SHOULDER",  "NECK_VIRTUAL"),
        (HumanBodyBones.Head,           HumanBodyBones.Jaw,            "NECK_VIRTUAL",   "NOSE"),

        // Left arm
        (HumanBodyBones.LeftShoulder,   HumanBodyBones.LeftUpperArm,   "MID_SHOULDER",  "LEFT_SHOULDER"),
        (HumanBodyBones.LeftUpperArm,   HumanBodyBones.LeftLowerArm,   "LEFT_SHOULDER", "LEFT_ELBOW"),
        (HumanBodyBones.LeftLowerArm,   HumanBodyBones.LeftHand,       "LEFT_ELBOW",    "LEFT_WRIST"),

        // Right arm
        (HumanBodyBones.RightShoulder,  HumanBodyBones.RightUpperArm,  "MID_SHOULDER",  "RIGHT_SHOULDER"),
        (HumanBodyBones.RightUpperArm,  HumanBodyBones.RightLowerArm,  "RIGHT_SHOULDER","RIGHT_ELBOW"),
        (HumanBodyBones.RightLowerArm,  HumanBodyBones.RightHand,      "RIGHT_ELBOW",   "RIGHT_WRIST"),

        // Left leg
        (HumanBodyBones.LeftUpperLeg,   HumanBodyBones.LeftLowerLeg,   "LEFT_HIP",      "LEFT_KNEE"),
        (HumanBodyBones.LeftLowerLeg,   HumanBodyBones.LeftFoot,       "LEFT_KNEE",     "LEFT_ANKLE"),
        (HumanBodyBones.LeftFoot,       HumanBodyBones.LeftToes,       "LEFT_ANKLE",    "LEFT_FOOT_INDEX"),

        // Right leg
        (HumanBodyBones.RightUpperLeg,  HumanBodyBones.RightLowerLeg,  "RIGHT_HIP",     "RIGHT_KNEE"),
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
    }

    private Transform _rootTransform;
    private Transform _hips;
    private Vector3 _bindHipsLocalPos;
    private float _bindHeight;
    private readonly List<RuntimeBone> _bones = new List<RuntimeBone>();
    private readonly Dictionary<string, Vector3> _pos = new Dictionary<string, Vector3>();
    private bool _ready;
    private bool _loggedHumanoidWarning;

    /// <summary>If true, LEFT_* / RIGHT_* arm landmark positions are swapped before retargeting (mirrored rig vs camera).</summary>
    private bool _debugSwapArmLandmarks;

    /// <summary>If true, negate world Z on arm landmarks after mapping (debug).</summary>
    private bool _debugInvertArmDepthZ;

    /// <summary>If true, negate world Z on head cluster after head straightening (debug).</summary>
    private bool _debugInvertHeadDepthZ;

    private Vector3 _headForwardSmoothed;

    private Vector3 _figureCenter;
    private float _figureHeight = 1f;
    public Vector3 FigureCenter => _figureCenter;
    public float FigureHeight => _figureHeight;

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
        if (_hips != null) _bindHipsLocalPos = _hips.localPosition;

        // Measure bind-pose height: lowest foot to head
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

        _bones.Clear();
        foreach (var m in BoneMap)
        {
            var bone = animator.GetBoneTransform(m.Bone);
            if (bone == null) continue; // UpperChest, LeftToes, etc. may be absent

            // Compute bind-pose child direction from actual hierarchy child
            var child = animator.GetBoneTransform(m.Child);
            Vector3 localDir;
            if (child != null)
            {
                Vector3 worldDir = (child.position - bone.position).normalized;
                localDir = bone.InverseTransformDirection(worldDir);
            }
            else
            {
                // Fallback: use first transform child or local up
                localDir = bone.childCount > 0
                    ? bone.InverseTransformDirection((bone.GetChild(0).position - bone.position).normalized)
                    : Vector3.up;
            }
            if (localDir.sqrMagnitude < 0.001f) localDir = Vector3.up;

            _bones.Add(new RuntimeBone
            {
                Bone = bone,
                From = m.From,
                To = m.To,
                BindLocalDir = localDir,
                BindLocalRot = bone.localRotation,
            });
        }

        _ready = _bones.Count > 0;
        if (_ready)
            Debug.Log($"[HumanoidPoseDriver] Ready: {_bones.Count} bones, bindHeight={_bindHeight:F3} on '{gameObject.name}'");
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

        // 1. Reset all bones to bind pose so rotations don't accumulate.
        foreach (var rb in _bones)
            if (rb.Bone != null) rb.Bone.localRotation = rb.BindLocalRot;
        if (_hips != null)
            _hips.localPosition = _bindHipsLocalPos;

        // 2. Scale root so model height matches landmark figure height.
        float scale = (_bindHeight > 0.01f && _figureHeight > 0.1f)
            ? _figureHeight / _bindHeight
            : 1f;
        _rootTransform.localScale = Vector3.one * scale;

        // 3. Position root so the hips bone lands at MID_HIP.
        if (_hips != null && _pos.TryGetValue("MID_HIP", out Vector3 midHip))
        {
            Vector3 hipsWorld = _hips.position; // after scale, before repositioning
            _rootTransform.position += (midHip - hipsWorld);
        }

        // 4. Rotate each bone root-to-leaf so its child direction matches landmark direction.
        foreach (var rb in _bones)
        {
            if (rb.Bone == null) continue;
            if (!_pos.TryGetValue(rb.From, out Vector3 from)) continue;
            if (!_pos.TryGetValue(rb.To, out Vector3 to)) continue;

            Vector3 targetDir = to - from;
            if (targetDir.sqrMagnitude < 1e-6f) continue;
            targetDir.Normalize();

            // Current direction this bone's child points in world space
            Vector3 currentDir = rb.Bone.TransformDirection(rb.BindLocalDir);
            if (currentDir.sqrMagnitude < 1e-6f) continue;

            Quaternion correction = Quaternion.FromToRotation(currentDir, targetDir);
            rb.Bone.rotation = correction * rb.Bone.rotation;
        }
    }

    private void BuildPositionCache(Dictionary<string, LandmarkPoint> landmarks)
    {
        _pos.Clear();
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
                Vector3.zero);
        }

        // Synthetic midpoints
        if (_pos.TryGetValue("LEFT_HIP", out Vector3 lh) && _pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
            _pos["MID_HIP"] = 0.5f * (lh + rh);
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            _pos["MID_SHOULDER"] = 0.5f * (ls + rs);

        PoseLandmarkMapping.ApplyHeadClusterBlend(_pos, headReachScale, headDepthScale);
        if (_debugSwapArmLandmarks)
            ApplyDebugArmLandmarkSwap();
        PoseLandmarkMapping.ApplyInvertArmWorldZ(_pos, _debugInvertArmDepthZ);
        // After shoulders/depth are final so torso "forward" matches retargeting debug.
        PoseLandmarkMapping.ApplyHeadStraightAheadNearShoulder(
            _pos,
            poseScale,
            ref _headForwardSmoothed,
            headForwardSmoothAlpha);
        PoseLandmarkMapping.ApplyInvertHeadWorldZ(_pos, _debugInvertHeadDepthZ);
        ApplyVirtualNeckLandmark();
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
