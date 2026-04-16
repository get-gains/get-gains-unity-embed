using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders pose as a 3D mannequin figure: capsule limbs + sphere joints with body-part coloring.
/// Flutter sends normalized x,y (0–1) and raw ML Kit Z (depth). X/Y map with scale; Z uses the same
/// PoseLandmarkMapping depth multiplier as HumanoidPoseDriver so the stick figure and humanoid align.
/// </summary>
public class PoseStickFigureRenderer : MonoBehaviour
{
    [Header("Scale and position")]
    [SerializeField] private float scale = 5f;
    [Tooltip("Scale for landmark Z (depth). Match HumanoidPoseDriver.poseDepthScale for alignment.")]
    [SerializeField] private float depthScale = 5f;
    [Tooltip("Invert X to match 2D raw vertices (must match HumanoidPoseDriver.invertLandmarkX).")]
    [SerializeField] private bool invertLandmarkX = true;
    [Tooltip("Invert Z so front (MLKit negative Z) = Unity +Z; must match HumanoidPoseDriver.invertLandmarkZ. Default false to use MLKit Z as-is.")]
    [SerializeField] private bool invertLandmarkZ = false;
    [Tooltip("Z from MLKit is not 0-1; divide by this before scaling (match HumanoidPoseDriver.zNormalizeScale).")]
    [SerializeField] private float zNormalizeScale = 100f;
    [Tooltip("Clamp raw X,Y to this range (match HumanoidPoseDriver.xyClampMin/Max).")]
    [SerializeField] private float xyClampMin = -0.2f;
    [SerializeField] private float xyClampMax = 1.2f;
    [SerializeField] private Vector3 centerOffset = Vector3.zero;

    [Header("Depth")]
    [Tooltip("Match HumanoidPoseDriver.zSpanFloor.")]
    [SerializeField] private float zSpanFloor = 0.03f;
    [Tooltip("Match HumanoidPoseDriver.zMultiplierCap.")]
    [SerializeField] private float zMultiplierCap = 60f;
    [SerializeField] private bool invertDepthAxis;

    [Header("Head")]
    [Tooltip("Match HumanoidPoseDriver.headReachScale.")]
    [SerializeField] private float headReachScale = 0.65f;
    [Tooltip("Match HumanoidPoseDriver.headDepthScale.")]
    [SerializeField] private float headDepthScale = 0.55f;
    [Tooltip("Match HumanoidPoseDriver.headForwardSmoothAlpha.")]
    [SerializeField] private float headForwardSmoothAlpha = 0.22f;

    [Header("Visuals")]
    [SerializeField] private float jointRadius = 0.045f;
    [SerializeField] private float limbRadius = 0.03f;
    [SerializeField] private float torsoRadius = 0.045f;

    private enum BodyPart { Head, Torso, LeftArm, RightArm, LeftLeg, RightLeg, Foot }

    private static readonly (string From, string To, BodyPart Part)[] SkeletonBones =
    {
        ("LEFT_EAR", "LEFT_EYE", BodyPart.Head),
        ("RIGHT_EAR", "RIGHT_EYE", BodyPart.Head),
        ("LEFT_EAR", "NOSE", BodyPart.Head),
        ("RIGHT_EAR", "NOSE", BodyPart.Head),
        ("LEFT_EYE", "NOSE", BodyPart.Head),
        ("RIGHT_EYE", "NOSE", BodyPart.Head),
        ("LEFT_SHOULDER", "RIGHT_SHOULDER", BodyPart.Torso),
        ("LEFT_SHOULDER", "LEFT_HIP", BodyPart.Torso),
        ("RIGHT_SHOULDER", "RIGHT_HIP", BodyPart.Torso),
        ("LEFT_HIP", "RIGHT_HIP", BodyPart.Torso),
        ("LEFT_SHOULDER", "LEFT_ELBOW", BodyPart.LeftArm),
        ("LEFT_ELBOW", "LEFT_WRIST", BodyPart.LeftArm),
        ("RIGHT_SHOULDER", "RIGHT_ELBOW", BodyPart.RightArm),
        ("RIGHT_ELBOW", "RIGHT_WRIST", BodyPart.RightArm),
        ("LEFT_HIP", "LEFT_KNEE", BodyPart.LeftLeg),
        ("LEFT_KNEE", "LEFT_ANKLE", BodyPart.LeftLeg),
        ("RIGHT_HIP", "RIGHT_KNEE", BodyPart.RightLeg),
        ("RIGHT_KNEE", "RIGHT_ANKLE", BodyPart.RightLeg),
        ("LEFT_ANKLE", "LEFT_HEEL", BodyPart.Foot),
        ("LEFT_HEEL", "LEFT_FOOT_INDEX", BodyPart.Foot),
        ("RIGHT_ANKLE", "RIGHT_HEEL", BodyPart.Foot),
        ("RIGHT_HEEL", "RIGHT_FOOT_INDEX", BodyPart.Foot),
    };

    private Shader _shader;
    private Material _jointMaterial;
    private readonly Dictionary<BodyPart, Material> _partMaterials = new Dictionary<BodyPart, Material>();
    private readonly Dictionary<string, Transform> _jointTransforms = new Dictionary<string, Transform>();
    private readonly List<Transform> _boneCapsules = new List<Transform>();
    private readonly List<int> _bonePartIndex = new List<int>();
    private GameObject _jointsRoot;
    private GameObject _bonesRoot;

    private Vector3 _figureCenter;
    private float _figureHeight = 1f;
    private Color _baseColor = Color.cyan;
    private bool _debugInvertArmDepthZ;
    private bool _debugInvertHeadDepthZ;
    private Vector3 _headForwardSmoothed;

    public Vector3 FigureCenter => _figureCenter;
    public float FigureHeight => _figureHeight;

    public void SetDebugInvertArmDepthZ(bool value)
    {
        _debugInvertArmDepthZ = value;
    }

    public void SetDebugInvertHeadDepthZ(bool value)
    {
        _debugInvertHeadDepthZ = value;
    }

    private void Awake()
    {
        _jointsRoot = new GameObject("PoseJoints");
        _jointsRoot.transform.SetParent(transform);
        _bonesRoot = new GameObject("PoseBones");
        _bonesRoot.transform.SetParent(transform);

        _shader = Shader.Find("Sprites/Default");
        if (_shader == null) _shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (_shader == null) _shader = Shader.Find("Unlit/Color");
        if (_shader == null) _shader = Shader.Find("Hidden/InternalColored");
        if (_shader == null)
        {
            Debug.LogError("[PoseStickFigureRenderer] No shader found!");
            return;
        }

        _jointMaterial = MakeMaterial(_baseColor);
        foreach (BodyPart part in System.Enum.GetValues(typeof(BodyPart)))
            _partMaterials[part] = MakeMaterial(_baseColor);
        ApplyBodyPartTints();

        CreateJoints();
        CreateBones();
    }

    private Material MakeMaterial(Color c)
    {
        var mat = new Material(_shader);
        mat.color = c;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        return mat;
    }

    private void SetMatColor(Material mat, Color c)
    {
        if (mat == null) return;
        mat.color = c;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
    }

    private void ApplyBodyPartTints()
    {
        Color.RGBToHSV(_baseColor, out float h, out float s, out float v);
        SetMatColor(_partMaterials[BodyPart.Head],     Color.HSVToRGB(h, s * 0.65f, Mathf.Min(1f, v * 1.2f)));
        SetMatColor(_partMaterials[BodyPart.Torso],    Color.HSVToRGB(h, s, v));
        SetMatColor(_partMaterials[BodyPart.LeftArm],  Color.HSVToRGB(Mathf.Repeat(h + 0.04f, 1f), s, v * 0.9f));
        SetMatColor(_partMaterials[BodyPart.RightArm], Color.HSVToRGB(Mathf.Repeat(h + 0.04f, 1f), s, v * 0.9f));
        SetMatColor(_partMaterials[BodyPart.LeftLeg],  Color.HSVToRGB(Mathf.Repeat(h - 0.04f, 1f), s * 0.9f, v * 0.85f));
        SetMatColor(_partMaterials[BodyPart.RightLeg], Color.HSVToRGB(Mathf.Repeat(h - 0.04f, 1f), s * 0.9f, v * 0.85f));
        SetMatColor(_partMaterials[BodyPart.Foot],     Color.HSVToRGB(Mathf.Repeat(h - 0.06f, 1f), s * 0.8f, v * 0.8f));
        SetMatColor(_jointMaterial, _baseColor);
    }

    private void CreateJoints()
    {
        var names = new HashSet<string>();
        foreach (var (from, to, _) in SkeletonBones) { names.Add(from); names.Add(to); }

        foreach (string name in names)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(_jointsRoot.transform);
            go.transform.localScale = Vector3.one * jointRadius * 2f;
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = _jointMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            var c = go.GetComponent<Collider>();
            if (c != null) c.enabled = false;
            go.SetActive(false);
            _jointTransforms[name] = go.transform;
        }
    }

    private void CreateBones()
    {
        for (int i = 0; i < SkeletonBones.Length; i++)
        {
            var (from, to, part) = SkeletonBones[i];
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"Bone_{from}_{to}";
            go.transform.SetParent(_bonesRoot.transform);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = _partMaterials.ContainsKey(part) ? _partMaterials[part] : _jointMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            var c = go.GetComponent<Collider>();
            if (c != null) c.enabled = false;
            go.SetActive(false);
            _boneCapsules.Add(go.transform);
            _bonePartIndex.Add(i);
        }
    }

    /// <summary>
    /// Update skeleton to match one frame of landmarks using PoseLandmarkMapping (same as HumanoidPoseDriver).
    /// </summary>
    public void UpdateFrame(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (_jointsRoot == null || _bonesRoot == null) return;
        if (landmarks == null || landmarks.Count == 0) { SetVisible(false); return; }

        SetVisible(true);

        var positions = new Dictionary<string, Vector3>();
        float minY = float.MaxValue, maxY = float.MinValue;
        float sumX = 0, sumY = 0, sumZ = 0;
        int count = 0;

        double zRef = PoseLandmarkMapping.ComputeZReference(landmarks);
        float zMult = PoseLandmarkMapping.ComputeDepthMultiplier(
            landmarks, scale, zRef, zSpanFloor, zMultiplierCap);
        foreach (var kvp in landmarks)
        {
            Vector3 pos = PoseLandmarkMapping.ToWorldPosition(
                kvp.Value,
                scale,
                zMult,
                invertDepthAxis,
                zRef,
                centerOffset);
            positions[kvp.Key] = pos;
        }

        PoseLandmarkMapping.ApplyHeadClusterBlend(positions, headReachScale, headDepthScale);
        PoseLandmarkMapping.ApplyInvertArmWorldZ(positions, _debugInvertArmDepthZ);
        PoseLandmarkMapping.ApplyHeadStraightAheadNearShoulder(
            positions,
            scale,
            ref _headForwardSmoothed,
            headForwardSmoothAlpha);
        PoseLandmarkMapping.ApplyInvertHeadWorldZ(positions, _debugInvertHeadDepthZ);

        foreach (var kvp in positions)
        {
            Vector3 pos = kvp.Value;
            sumX += pos.x;
            sumY += pos.y;
            sumZ += pos.z;
            if (pos.y < minY) minY = pos.y;
            if (pos.y > maxY) maxY = pos.y;
            count++;
        }

        if (count > 0)
        {
            _figureCenter = new Vector3(sumX / count, sumY / count, sumZ / count);
            _figureHeight = Mathf.Max(maxY - minY, 0.5f);
        }

        foreach (var kvp in positions)
        {
            if (_jointTransforms.TryGetValue(kvp.Key, out Transform t))
            {
                t.position = transform.TransformPoint(kvp.Value);
                t.gameObject.SetActive(true);
            }
        }

        for (int i = 0; i < _boneCapsules.Count && i < SkeletonBones.Length; i++)
        {
            var (from, to, part) = SkeletonBones[i];
            if (positions.TryGetValue(from, out Vector3 a) && positions.TryGetValue(to, out Vector3 b))
            {
                PlaceCapsule(_boneCapsules[i], transform.TransformPoint(a), transform.TransformPoint(b), part);
                _boneCapsules[i].gameObject.SetActive(true);
            }
            else
            {
                _boneCapsules[i].gameObject.SetActive(false);
            }
        }
    }

    private void PlaceCapsule(Transform capsule, Vector3 start, Vector3 end, BodyPart part)
    {
        Vector3 dir = end - start;
        float dist = dir.magnitude;
        if (dist < 0.001f) { capsule.gameObject.SetActive(false); return; }

        float radius = (part == BodyPart.Torso) ? torsoRadius : limbRadius;

        capsule.position = (start + end) * 0.5f;
        capsule.rotation = Quaternion.FromToRotation(Vector3.up, dir.normalized);
        capsule.localScale = new Vector3(radius * 2f, dist * 0.5f, radius * 2f);
    }

    public void SetColor(Color color)
    {
        _baseColor = color;
        ApplyBodyPartTints();
    }

    public void SetVisible(bool visible)
    {
        if (_jointsRoot != null) _jointsRoot.SetActive(visible);
        if (_bonesRoot != null) _bonesRoot.SetActive(visible);
    }
}
