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
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Scale")]
    [Tooltip("Must match PoseStickFigureRenderer.scale so positions align.")]
    [SerializeField] private float poseScale = 5f;

    // (Bone, HierarchyChild for bind direction, FromLandmark, ToLandmark)
    // Root-to-leaf order so parent rotations apply before children.
    private static readonly (HumanBodyBones Bone, HumanBodyBones Child, string From, string To)[] BoneMap =
    {
        // Spine chain
        (HumanBodyBones.Hips,           HumanBodyBones.Spine,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Spine,          HumanBodyBones.Chest,          "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Chest,          HumanBodyBones.UpperChest,     "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.UpperChest,     HumanBodyBones.Neck,           "MID_HIP",       "MID_SHOULDER"),
        (HumanBodyBones.Neck,           HumanBodyBones.Head,           "MID_SHOULDER",  "NOSE"),

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

    private Vector3 _figureCenter;
    private float _figureHeight = 1f;
    public Vector3 FigureCenter => _figureCenter;
    public float FigureHeight => _figureHeight;

    private void Start()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) { Debug.LogWarning("[HumanoidPoseDriver] No Animator."); return; }

        _rootTransform = animator.transform;
        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (_hips != null) _bindHipsLocalPos = _hips.localPosition;

        // Measure bind-pose height: lowest foot to head
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
        Debug.Log($"[HumanoidPoseDriver] Ready: {_bones.Count} bones, bindHeight={_bindHeight:F3}");
    }

    public void ApplyPose(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (!_ready || landmarks == null || landmarks.Count == 0) return;

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
        foreach (var kvp in landmarks)
        {
            float x = (float)(kvp.Value.X - 0.5) * poseScale;
            float y = (float)(1.0 - kvp.Value.Y - 0.5) * poseScale;
            _pos[kvp.Key] = new Vector3(x, y, 0f);
        }

        // Synthetic midpoints
        if (_pos.TryGetValue("LEFT_HIP", out Vector3 lh) && _pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
            _pos["MID_HIP"] = 0.5f * (lh + rh);
        if (_pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) && _pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            _pos["MID_SHOULDER"] = 0.5f * (ls + rs);
    }

    private void ComputeFigureBounds()
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        float sumX = 0, sumY = 0;
        int count = 0;

        foreach (var kvp in _pos)
        {
            if (kvp.Key.StartsWith("MID_")) continue;
            sumX += kvp.Value.x;
            sumY += kvp.Value.y;
            if (kvp.Value.y < minY) minY = kvp.Value.y;
            if (kvp.Value.y > maxY) maxY = kvp.Value.y;
            count++;
        }

        if (count > 0)
        {
            _figureCenter = new Vector3(sumX / count, sumY / count, 0f);
            _figureHeight = Mathf.Max(maxY - minY, 0.5f);
        }
    }
}
