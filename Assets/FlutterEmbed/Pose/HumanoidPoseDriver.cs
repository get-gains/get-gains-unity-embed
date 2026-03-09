using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid rig from MediaPipe 33-landmark pose data.
/// Moves the root transform (not individual bones) to follow the pelvis center.
/// Applies only ROTATIONS to bones so the skinned mesh stays intact.
/// Processes from root to leaf so parent rotations cascade correctly.
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Scale")]
    [Tooltip("Scale factor applied to landmark positions to match rig size.")]
    [SerializeField] private float poseScale = 5f;

    private Transform _rootTransform;
    private Transform _hips;
    private Vector3 _bindHipsLocalPos;

    private struct BoneChain
    {
        public Transform Bone;
        public string FromLandmark;
        public string ToLandmark;
        public Vector3 BindLocalDir;
        public Quaternion BindLocalRot;
    }

    private readonly List<BoneChain> _chains = new List<BoneChain>();
    private readonly Dictionary<string, Vector3> _posCache = new Dictionary<string, Vector3>();

    // Ordered root-to-leaf so parent rotations apply before children.
    private static readonly (HumanBodyBones Bone, HumanBodyBones Child, string From, string To)[] Segments =
    {
        // Spine first (root-most)
        (HumanBodyBones.Spine, HumanBodyBones.Chest, "MID_HIP", "MID_SHOULDER"),
        (HumanBodyBones.Neck,  HumanBodyBones.Head,  "MID_SHOULDER", "NOSE"),

        // Arms
        (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  "LEFT_SHOULDER", "LEFT_ELBOW"),
        (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      "LEFT_ELBOW",    "LEFT_WRIST"),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, "RIGHT_SHOULDER","RIGHT_ELBOW"),
        (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     "RIGHT_ELBOW",   "RIGHT_WRIST"),

        // Legs
        (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  "LEFT_HIP",      "LEFT_KNEE"),
        (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      "LEFT_KNEE",     "LEFT_ANKLE"),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, "RIGHT_HIP",     "RIGHT_KNEE"),
        (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     "RIGHT_KNEE",    "RIGHT_ANKLE"),
    };

    private bool _ready;

    private void Start()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (animator == null)
        {
            Debug.LogWarning("[HumanoidPoseDriver] No Animator found.");
            return;
        }

        _rootTransform = animator.transform;
        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);

        if (_hips != null)
            _bindHipsLocalPos = _hips.localPosition;

        _chains.Clear();
        foreach (var seg in Segments)
        {
            var bone = animator.GetBoneTransform(seg.Bone);
            var child = animator.GetBoneTransform(seg.Child);
            if (bone == null || child == null)
            {
                Debug.LogWarning($"[HumanoidPoseDriver] Missing bone: {seg.Bone} or {seg.Child}");
                continue;
            }

            Vector3 worldDir = (child.position - bone.position).normalized;
            Vector3 localDir = bone.InverseTransformDirection(worldDir);

            _chains.Add(new BoneChain
            {
                Bone = bone,
                FromLandmark = seg.From,
                ToLandmark = seg.To,
                BindLocalDir = localDir,
                BindLocalRot = bone.localRotation
            });
        }

        _ready = _chains.Count > 0;
        Debug.Log($"[HumanoidPoseDriver] Ready with {_chains.Count} bone chains.");
    }

    public void ApplyPose(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (!_ready || landmarks == null || landmarks.Count == 0) return;

        BuildPositionCache(landmarks);

        // Reset all bones to bind pose first so rotations don't accumulate.
        foreach (var chain in _chains)
        {
            if (chain.Bone != null)
                chain.Bone.localRotation = chain.BindLocalRot;
        }
        if (_hips != null)
            _hips.localPosition = _bindHipsLocalPos;

        // Move the ROOT TRANSFORM (not hips bone) to follow the pelvis center.
        if (_rootTransform != null && _posCache.TryGetValue("MID_HIP", out Vector3 pelvis))
        {
            _rootTransform.position = pelvis;
        }

        // Apply rotations from root to leaf. Each bone is rotated so its
        // bind-pose child direction aligns with the landmark direction.
        foreach (var chain in _chains)
        {
            if (chain.Bone == null) continue;
            if (!_posCache.TryGetValue(chain.FromLandmark, out Vector3 from)) continue;
            if (!_posCache.TryGetValue(chain.ToLandmark, out Vector3 to)) continue;

            Vector3 targetDir = (to - from);
            if (targetDir.sqrMagnitude < 1e-6f) continue;
            targetDir.Normalize();

            // Current world direction of this bone's child (after parent rotations).
            Vector3 currentDir = chain.Bone.TransformDirection(chain.BindLocalDir);
            Quaternion correction = Quaternion.FromToRotation(currentDir, targetDir);
            chain.Bone.rotation = correction * chain.Bone.rotation;
        }
    }

    private void BuildPositionCache(Dictionary<string, LandmarkPoint> landmarks)
    {
        _posCache.Clear();

        foreach (var kvp in landmarks)
        {
            float x = (float)(kvp.Value.X - 0.5) * poseScale;
            float y = (float)(1.0 - kvp.Value.Y - 0.5) * poseScale;
            _posCache[kvp.Key] = new Vector3(x, y, 0f);
        }

        if (_posCache.TryGetValue("LEFT_HIP", out Vector3 lh) && _posCache.TryGetValue("RIGHT_HIP", out Vector3 rh))
            _posCache["MID_HIP"] = 0.5f * (lh + rh);

        if (_posCache.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _posCache.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            _posCache["MID_SHOULDER"] = 0.5f * (ls + rs);
    }
}
