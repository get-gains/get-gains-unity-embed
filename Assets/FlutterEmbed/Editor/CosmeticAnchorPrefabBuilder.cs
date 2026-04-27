#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds flat cosmetic prefabs (with <see cref="CosmeticAttachment"/>) from FBX under
/// <c>Assets/Resources/Cosmetics/{Head,Facewear,Hats}</c>, and mirrors them to
/// <c>Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets</c> (UnityEngine.Resources load path)
/// and <c>Assets/FlutterEmbed/Cosmetics/CosmeticAssets</c> (repo copy).
/// </summary>
public static class CosmeticAnchorPrefabBuilder
{
    private const string SourceRoot = "Assets/Resources/Cosmetics";
    private const string OutResources = "Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets";
    private const string OutMirror = "Assets/FlutterEmbed/Cosmetics/CosmeticAssets";

    private const float TargetHeadHeight = 0.35f;
    private const float TargetFaceHeight = 0.22f;
    private const float TargetHatHeight = 0.32f;

    [MenuItem("Flutter Embed/Cosmetics/Rebuild cosmetic prefabs from Resources/Cosmetics")]
    public static void BuildAll()
    {
        Directory.CreateDirectory(OutResources);
        Directory.CreateDirectory(OutMirror);

        var jobs = new List<(string path, CosmeticAnchorKind kind)>();
        Collect($"{SourceRoot}/Head", CosmeticAnchorKind.Head, jobs);
        Collect($"{SourceRoot}/Facewear", CosmeticAnchorKind.Facewear, jobs);
        Collect($"{SourceRoot}/Hats", CosmeticAnchorKind.HatTop, jobs);

        foreach (var (path, kind) in jobs)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var assetRef = FbxNameToAssetRef(fileName);
            if (string.IsNullOrEmpty(assetRef)) continue;
            try
            {
                BuildOne(path, kind, assetRef);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CosmeticAnchorPrefabBuilder] Failed '{assetRef}': {e.Message}\n{e.StackTrace}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CosmeticAnchorPrefabBuilder] Done — {jobs.Count} source model(s) processed.");
    }

    private static void Collect(string folder, CosmeticAnchorKind kind, List<(string, CosmeticAnchorKind)> jobs)
    {
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning($"[CosmeticAnchorPrefabBuilder] Missing folder: {folder}");
            return;
        }

        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;
            jobs.Add((p, kind));
        }
    }

    private static string FbxNameToAssetRef(string fileNameWithoutExt)
    {
        return fileNameWithoutExt.Replace('-', '_').Replace(' ', '_').Trim();
    }

    private static Quaternion GetMeshRotationOverride(string assetRef)
    {
        switch (assetRef)
        {
            case "cowboy_hat_get_gains":
                return Quaternion.Euler(90f, 0f, 0f);
            case "glasses_get_gains":
            case "2016_gamer_glasses_get_gains":
                return Quaternion.Euler(0f, 0f, -90f);
            case "beanie_get_gains":
                return Quaternion.identity;
            case "eye_cosmetic_get_gains":
                return Quaternion.Euler(0f, 90f, 0f);
            default:
                return Quaternion.identity;
        }
    }

    private static void BuildOne(string fbxPath, CosmeticAnchorKind kind, string assetRef)
    {
        var fbxMain = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbxMain == null)
        {
            Debug.LogWarning($"[CosmeticAnchorPrefabBuilder] Skip (could not load): {fbxPath}");
            return;
        }

        var root = new GameObject(assetRef);
        var attach = root.AddComponent<CosmeticAttachment>();
        attach.anchorKind = kind;
        attach.ApplyDefaults(kind);
        attach.focusHeightBias = kind == CosmeticAnchorKind.HatTop ? 0.48f : kind == CosmeticAnchorKind.Facewear ? 0.38f : 0.43f;

        var holder = new GameObject("MeshHolder");
        holder.transform.SetParent(root.transform, false);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxMain, holder.transform);
        instance.name = fbxMain.name;
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        var renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            UnityEngine.Object.DestroyImmediate(root);
            Debug.LogWarning($"[CosmeticAnchorPrefabBuilder] Skip (no renderers): {assetRef}");
            return;
        }

        holder.transform.localRotation = GetMeshRotationOverride(assetRef);

        var wb0 = CombinedWorldBounds(renderers);
        var maxExt = Mathf.Max(wb0.extents.x, wb0.extents.y, wb0.extents.z, 1e-4f);
        var target = kind == CosmeticAnchorKind.HatTop
            ? TargetHatHeight
            : kind == CosmeticAnchorKind.Facewear
                ? TargetFaceHeight
                : TargetHeadHeight;
        var uniform = target / (2f * maxExt);
        holder.transform.localScale = Vector3.one * uniform;

        var wb1 = CombinedWorldBounds(renderers);
        var pivotW = kind == CosmeticAnchorKind.HatTop
            ? new Vector3(wb1.center.x, wb1.min.y, wb1.center.z)
            : wb1.center;
        holder.transform.localPosition = root.transform.InverseTransformVector(root.transform.position - pivotW);

        var primary = OutResources + "/" + assetRef + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(root, primary);
        UnityEngine.Object.DestroyImmediate(root);

        var mirrorPath = OutMirror + "/" + assetRef + ".prefab";
        if (File.Exists(mirrorPath)) AssetDatabase.DeleteAsset(mirrorPath);
        AssetDatabase.CopyAsset(primary, mirrorPath);
    }

    private static Bounds CombinedWorldBounds(IEnumerable<Renderer> renderers)
    {
        Bounds b = default;
        var first = true;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (first)
            {
                b = r.bounds;
                first = false;
            }
            else b.Encapsulate(r.bounds);
        }
        return b;
    }
}
#endif
