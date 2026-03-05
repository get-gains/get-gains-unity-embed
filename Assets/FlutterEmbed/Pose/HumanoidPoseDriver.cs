using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid rig (e.g. HumanBasemesh) from MediaPipe-style landmark positions.
/// Uses bone -> child direction from the bind pose as the reference, then rotates bones
/// so that their direction matches the landmark segment direction (e.g. shoulder→elbow).
/// This is a lightweight, approximate mapping meant to give a believable 3D rig motion
/// without complex IK or full-body retargeting.
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    [Serializable]
    private struct BoneMapping
    {
        public HumanBodyBones Bone;
        public HumanBodyBones ChildBone;
        public string FromLandmark;
        public string ToLandmark;
    }

    [Header("Rig")]
    [SerializeField] private Animator animator;

    [Header("Mappings")]
    [SerializeField] private BoneMapping[] mappings =
    {
        // Arms
        new BoneMapping
        {
            Bone = HumanBodyBones.LeftUpperArm,
            ChildBone = HumanBodyBones.LeftLowerArm,
            FromLandmark = "LEFT_SHOULDER",
            ToLandmark = "LEFT_ELBOW"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.LeftLowerArm,
            ChildBone = HumanBodyBones.LeftHand,
            FromLandmark = "LEFT_ELBOW",
            ToLandmark = "LEFT_WRIST"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.RightUpperArm,
            ChildBone = HumanBodyBones.RightLowerArm,
            FromLandmark = "RIGHT_SHOULDER",
            ToLandmark = "RIGHT_ELBOW"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.RightLowerArm,
            ChildBone = HumanBodyBones.RightHand,
            FromLandmark = "RIGHT_ELBOW",
            ToLandmark = "RIGHT_WRIST"
        },

        // Legs
        new BoneMapping
        {
            Bone = HumanBodyBones.LeftUpperLeg,
            ChildBone = HumanBodyBones.LeftLowerLeg,
            FromLandmark = "LEFT_HIP",
            ToLandmark = "LEFT_KNEE"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.LeftLowerLeg,
            ChildBone = HumanBodyBones.LeftFoot,
            FromLandmark = "LEFT_KNEE",
            ToLandmark = "LEFT_ANKLE"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.RightUpperLeg,
            ChildBone = HumanBodyBones.RightLowerLeg,
            FromLandmark = "RIGHT_HIP",
            ToLandmark = "RIGHT_KNEE"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.RightLowerLeg,
            ChildBone = HumanBodyBones.RightFoot,
            FromLandmark = "RIGHT_KNEE",
            ToLandmark = "RIGHT_ANKLE"
        },

        // Spine / chest / head
        new BoneMapping
        {
            Bone = HumanBodyBones.Spine,
            ChildBone = HumanBodyBones.Chest,
            FromLandmark = "PELVIS_CENTER",
            ToLandmark = "CHEST_CENTER"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.Chest,
            ChildBone = HumanBodyBones.Neck,
            FromLandmark = "CHEST_CENTER",
            ToLandmark = "NECK_CENTER"
        },
        new BoneMapping
        {
            Bone = HumanBodyBones.Neck,
            ChildBone = HumanBodyBones.Head,
            FromLandmark = "NECK_CENTER",
            ToLandmark = "NOSE"
        }
    };

    private struct BindInfo
    {
        public Transform Bone;
        public Vector3 BindDir;   // world-space direction bone->child in bind pose
        public Quaternion BindRot;
    }

    private readonly List<BindInfo> _bindInfos = new List<BindInfo>();
    private readonly Dictionary<string, Vector3> _landmarkCache = new Dictionary<string, Vector3>();

    private Transform _hips;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        if (animator == null)
        {
            Debug.LogWarning("[HumanoidPoseDriver] No Animator assigned.");
            return;
        }

        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);

        _bindInfos.Clear();
        foreach (var mapping in mappings)
        {
            var bone = animator.GetBoneTransform(mapping.Bone);
            var child = animator.GetBoneTransform(mapping.ChildBone);
            if (bone == null || child == null) continue;

            var dir = (child.position - bone.position);
            if (dir.sqrMagnitude < 1e-6f) continue;

            _bindInfos.Add(new BindInfo
            {
                Bone = bone,
                BindDir = dir.normalized,
                BindRot = bone.rotation
            });
        }
    }

    /// <summary>
    /// Apply one pose frame's landmarks to the humanoid rig.
    /// Expects MediaPipe-normalised coordinates (x: 0-1 right, y: 0-1 down, z: depth unused).
    /// </summary>
    public void ApplyPose(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (animator == null || landmarks == null || landmarks.Count == 0) return;

        // Rebuild cache each frame in a lightweight way.
        _landmarkCache.Clear();

        // Precompute some midpoints used by spine/head mappings.
        Vector3 leftHip = GetLandmarkPosition(landmarks, "LEFT_HIP");
        Vector3 rightHip = GetLandmarkPosition(landmarks, "RIGHT_HIP");
        Vector3 leftShoulder = GetLandmarkPosition(landmarks, "LEFT_SHOULDER");
        Vector3 rightShoulder = GetLandmarkPosition(landmarks, "RIGHT_SHOULDER");

        _landmarkCache["LEFT_HIP"] = leftHip;
        _landmarkCache["RIGHT_HIP"] = rightHip;
        _landmarkCache["LEFT_SHOULDER"] = leftShoulder;
        _landmarkCache["RIGHT_SHOULDER"] = rightShoulder;

        _landmarkCache["PELVIS_CENTER"] = 0.5f * (leftHip + rightHip);
        _landmarkCache["CHEST_CENTER"] = 0.5f * (leftShoulder + rightShoulder);

        // Approximate neck as halfway between chest center and head.
        Vector3 nose = GetLandmarkPosition(landmarks, "NOSE");
        _landmarkCache["NOSE"] = nose;
        _landmarkCache["NECK_CENTER"] = 0.5f * (_landmarkCache["CHEST_CENTER"] + nose);

        // Cache remaining landmarks as needed by mappings.
        foreach (var mapping in mappings)
        {
            if (!_landmarkCache.ContainsKey(mapping.FromLandmark))
                _landmarkCache[mapping.FromLandmark] = GetLandmarkPosition(landmarks, mapping.FromLandmark);
            if (!_landmarkCache.ContainsKey(mapping.ToLandmark))
                _landmarkCache[mapping.ToLandmark] = GetLandmarkPosition(landmarks, mapping.ToLandmark);
        }

        // Rotate each bone to match the target landmark direction.
        for (int i = 0; i < mappings.Length && i < _bindInfos.Count; i++)
        {
            var mapping = mappings[i];
            var bind = _bindInfos[i];
            if (bind.Bone == null) continue;

            if (!_landmarkCache.TryGetValue(mapping.FromLandmark, out var a) ||
                !_landmarkCache.TryGetValue(mapping.ToLandmark, out var b))
            {
                continue;
            }

            Vector3 targetDir = (b - a);
            if (targetDir.sqrMagnitude < 1e-6f) continue;

            targetDir.Normalize();

            Quaternion delta = Quaternion.FromToRotation(bind.BindDir, targetDir);
            bind.Bone.rotation = delta * bind.BindRot;
        }

        // Optionally move hips to follow pelvis center in world space (2D plane).
        if (_hips != null)
        {
            Vector3 pelvisCenter;
            if (_landmarkCache.TryGetValue("PELVIS_CENTER", out pelvisCenter))
            {
                // Scale pelvis motion loosely to the rig size.
                float scale = 1.5f;
                Vector3 offset = new Vector3(pelvisCenter.x * scale, pelvisCenter.y * scale, 0f);
                var root = animator.transform;
                root.position = offset;
            }
        }
    }

    private static Vector3 GetLandmarkPosition(Dictionary<string, LandmarkPoint> landmarks, string key)
    {
        if (!landmarks.TryGetValue(key, out var p))
            return Vector3.zero;

        // Same convention as PoseStickFigureRenderer: center around origin, Y-up, ignore depth.
        float x = (float)(p.X - 0.5);
        float y = (float)(1.0 - p.Y - 0.5);
        return new Vector3(x, y, 0f);
    }
}

