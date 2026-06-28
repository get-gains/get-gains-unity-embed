using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Loads up to three equipped cosmetic prefabs (by assetRef) and handles shop preview.
/// JSON: <c>{"cosmetics":[{"assetRef":"beanie_get_gains","category":"…"}]}</c> — category is ignored.
/// </summary>
public class CosmeticManager : MonoBehaviour
{
    public const int MaxEquippedCosmetics = 3;

    private const string ResourcesPrefabRoot = "FlutterEmbed/Cosmetics/CosmeticAssets";

    [Header("Optional — if unset, anchors are created/found under the humanoid Head bone")]
    [SerializeField] private Transform headAnchor;
    [SerializeField] private Transform facewearAnchor;
    [SerializeField] private Transform hatTopAnchor;

    private readonly List<GameObject> _equippedRoots = new();
    private readonly List<string> _lastEquippedRefs = new();

    [Serializable]
    private class CosmeticsEnvelope
    {
        public CosmeticEntry[] cosmetics;
    }

    [Serializable]
    private class CosmeticEntry
    {
        public string assetRef;
        public string category;
    }

    [Serializable]
    private class PreviewEnvelope
    {
        public string assetRef;
        public string category;
        public bool showOnly;
    }

    private void Awake()
    {
        EnsureAnchors();
    }

    private void EnsureAnchors()
    {
        if (headAnchor != null && facewearAnchor != null && hatTopAnchor != null) return;

        var driver = HumanoidPoseDriver.FindBestDriveableDriver();
        Transform headBone = null;
        if (driver != null)
        {
            var anim = driver.GetComponent<Animator>();
            if (anim != null) headBone = anim.GetBoneTransform(HumanBodyBones.Head);
        }

        if (headBone == null)
        {
            Debug.LogWarning("[CosmeticManager] No humanoid Head bone — cosmetics cannot attach.");
            return;
        }

        if (headAnchor == null) headAnchor = FindOrCreateAnchor(headBone, "CosmeticAnchor_Head", Vector3.zero, Quaternion.identity);
        if (facewearAnchor == null) facewearAnchor = FindOrCreateAnchor(headBone, "CosmeticAnchor_Face", new Vector3(0f, 0.06f, 0.08f), Quaternion.identity);
        if (hatTopAnchor == null) hatTopAnchor = FindOrCreateAnchor(headBone, "CosmeticAnchor_HatTop", new Vector3(0f, 0.11f, 0f), Quaternion.identity);
    }

    private static Transform FindOrCreateAnchor(Transform headBone, string name, Vector3 localPos, Quaternion localRot)
    {
        var existing = headBone.Find(name);
        if (existing != null) return existing;
        var go = new GameObject(name);
        go.transform.SetParent(headBone, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = Vector3.one;
        return go.transform;
    }

    public void LoadCosmetics(string json)
    {
        EnsureAnchors();
        ClearInstances();

        var refs = ParseAssetRefs(json, MaxEquippedCosmetics);
        _lastEquippedRefs.Clear();
        _lastEquippedRefs.AddRange(refs);

        foreach (var r in refs)
            TryAttach(r);

        SendToFlutter.Send("cosmetics_loaded");
    }

    public void PreviewCosmetic(string json)
    {
        EnsureAnchors();
        if (!TryParsePreview(json, out var assetRef)) return;

        ClearInstances();
        TryAttach(assetRef);
        SendToFlutter.Send("cosmetic_preview_ready");
    }

    public void ClearPreview()
    {
        LoadCosmetics(BuildCosmeticsJson(_lastEquippedRefs));
    }

    private static string BuildCosmeticsJson(IReadOnlyList<string> refs)
    {
        var sb = new StringBuilder();
        sb.Append("{\"cosmetics\":[");
        for (var i = 0; i < refs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"assetRef\":\"");
            sb.Append(EscapeJson(refs[i]));
            sb.Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void ClearInstances()
    {
        foreach (var go in _equippedRoots)
        {
            if (go != null) Destroy(go);
        }
        _equippedRoots.Clear();
    }

    private static List<string> ParseAssetRefs(string json, int max)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            var env = JsonUtility.FromJson<CosmeticsEnvelope>(json);
            if (env?.cosmetics == null) return list;
            foreach (var e in env.cosmetics)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.assetRef)) continue;
                list.Add(e.assetRef.Trim());
                if (list.Count >= max) break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CosmeticManager] Parse cosmetics JSON failed: " + ex.Message);
        }
        return list;
    }

    private static bool TryParsePreview(string json, out string assetRef)
    {
        assetRef = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var p = JsonUtility.FromJson<PreviewEnvelope>(json);
            if (p == null || string.IsNullOrWhiteSpace(p.assetRef)) return false;
            assetRef = p.assetRef.Trim();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CosmeticManager] Parse preview JSON failed: " + ex.Message);
            return false;
        }
    }

    private void TryAttach(string assetRef)
    {
        if (string.IsNullOrEmpty(assetRef)) return;
        if (headAnchor == null || facewearAnchor == null || hatTopAnchor == null) return;

        var prefab = Resources.Load<GameObject>($"{ResourcesPrefabRoot}/{assetRef}");
        if (prefab == null)
        {
            Debug.LogWarning($"[CosmeticManager] Missing cosmetic prefab for Resources path '{ResourcesPrefabRoot}/{assetRef}'.");
            return;
        }

        var instance = Instantiate(prefab);
        instance.name = assetRef;
        var attach = instance.GetComponent<CosmeticAttachment>();
        if (attach == null)
        {
            Debug.LogWarning($"[CosmeticManager] Prefab '{assetRef}' is missing CosmeticAttachment.");
            Destroy(instance);
            return;
        }
        var parent = ResolveParent(attach.anchorKind);
        instance.transform.SetParent(parent, false);
        instance.transform.localPosition = attach.localOffset;
        instance.transform.localRotation = attach.localRotation;
        instance.transform.localScale = attach.localScale;
        _equippedRoots.Add(instance);
    }

    private Transform ResolveParent(CosmeticAnchorKind kind)
    {
        return kind switch
        {
            CosmeticAnchorKind.Facewear => facewearAnchor != null ? facewearAnchor : headAnchor,
            CosmeticAnchorKind.HatTop => hatTopAnchor != null ? hatTopAnchor : headAnchor,
            _ => headAnchor,
        };
    }
}
