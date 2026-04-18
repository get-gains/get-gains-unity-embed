using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Builds cosmetic prefabs for all four categories (HEADWEAR, TOP, BOTTOM, ACCESSORY)
/// by auto-discovering FBX files under Assets/Resources/Cosmetics/ subfolders.
///
/// Prefab hierarchy: {assetRef} → CosmeticAnchor → MeshHolder → (FBX instance)
/// Prefabs are saved to:
///   Assets/FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/{assetRef}.prefab
///   Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/{assetRef}.prefab  ← runtime loading
///
/// To add models for a new category, place FBX files in a recognised subfolder under
/// Assets/Resources/Cosmetics/ (see FolderToCategory below), then run:
///   Tools → GetGains → Build All Cosmetic Prefabs
///
/// To fine-tune scaling for a specific item, add an entry to SizeOverrides below and rebuild.
/// Use Tools → GetGains → Report Selected Cosmetic Bounds to measure an instance in-scene.
/// </summary>
public static class CosmeticAnchorPrefabBuilder
{
    // ── Paths ──

    private const string SourceRoot          = "Assets/Resources/Cosmetics";
    private const string OutputRootFlux      = "Assets/FlutterEmbed/Cosmetics/CosmeticAssets";
    private const string OutputRootResources = "Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets";

    // ── Category Mapping ──

    /// <summary>
    /// Maps subfolder names (case-insensitive) to cosmetic categories.
    /// Add new subfolder names here as the art pipeline grows.
    /// </summary>
    private static readonly Dictionary<string, CosmeticSlot.CosmeticCategory> FolderToCategory
        = new Dictionary<string, CosmeticSlot.CosmeticCategory>(StringComparer.OrdinalIgnoreCase)
    {
        { "Hats",        CosmeticSlot.CosmeticCategory.HEADWEAR },
        { "Facewear",    CosmeticSlot.CosmeticCategory.HEADWEAR },
        { "Headwear",    CosmeticSlot.CosmeticCategory.HEADWEAR },
        { "Helmets",     CosmeticSlot.CosmeticCategory.HEADWEAR },
        { "Tops",        CosmeticSlot.CosmeticCategory.TOP },
        { "Shirts",      CosmeticSlot.CosmeticCategory.TOP },
        { "Tanks",       CosmeticSlot.CosmeticCategory.TOP },
        { "Hoodies",     CosmeticSlot.CosmeticCategory.TOP },
        { "Bottoms",     CosmeticSlot.CosmeticCategory.BOTTOM },
        { "Pants",       CosmeticSlot.CosmeticCategory.BOTTOM },
        { "Shorts",      CosmeticSlot.CosmeticCategory.BOTTOM },
        { "Skirts",      CosmeticSlot.CosmeticCategory.BOTTOM },
        { "Accessories", CosmeticSlot.CosmeticCategory.ACCESSORY },
        { "Wristbands",  CosmeticSlot.CosmeticCategory.ACCESSORY },
        { "Watches",     CosmeticSlot.CosmeticCategory.ACCESSORY },
        { "Gloves",      CosmeticSlot.CosmeticCategory.ACCESSORY },
    };

    // ── Scaling ──

    /// <summary>Default target max world size per category (longest axis after scaling).</summary>
    private static readonly Dictionary<CosmeticSlot.CosmeticCategory, float> DefaultTargetSize
        = new Dictionary<CosmeticSlot.CosmeticCategory, float>
    {
        { CosmeticSlot.CosmeticCategory.HEADWEAR,  0.20f },
        { CosmeticSlot.CosmeticCategory.TOP,       0.50f },
        { CosmeticSlot.CosmeticCategory.BOTTOM,    0.40f },
        { CosmeticSlot.CosmeticCategory.ACCESSORY, 0.08f },
    };

    /// <summary>
    /// Per-item size overrides keyed by assetRef.
    /// Use Tools → GetGains → Report Selected Cosmetic Bounds to measure, then add here and rebuild.
    /// </summary>
    private static readonly Dictionary<string, float> SizeOverrides
        = new Dictionary<string, float>
    {
        { "headwear_cup_lp",  0.26f },
        { "headwear_glasses", 0.11f },
    };

    // ── Discovery ──

    private struct FbxEntry
    {
        public string FbxAssetPath;
        public CosmeticSlot.CosmeticCategory Category;
    }

    private static List<FbxEntry> DiscoverFbxFiles()
    {
        var results = new List<FbxEntry>();

        if (!AssetDatabase.IsValidFolder(SourceRoot))
        {
            Debug.LogWarning($"[CosmeticAnchorPrefabBuilder] Source root not found: '{SourceRoot}'");
            return results;
        }

        string fullPath = Path.GetFullPath(SourceRoot);
        foreach (string subDir in Directory.GetDirectories(fullPath))
        {
            string folderName = Path.GetFileName(subDir);
            if (!FolderToCategory.TryGetValue(folderName, out var category))
            {
                Debug.LogWarning(
                    $"[CosmeticAnchorPrefabBuilder] Subfolder '{folderName}' in '{SourceRoot}' has no " +
                    "category mapping. Add it to FolderToCategory to include it in builds.");
                continue;
            }

            string assetSubfolder = $"{SourceRoot}/{folderName}";
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { assetSubfolder });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    results.Add(new FbxEntry { FbxAssetPath = assetPath, Category = category });
            }
        }

        return results;
    }

    // ── Helpers ──

    /// <summary>
    /// Maps category to the folder name used by CosmeticManager.GetCategoryFolder().
    /// Must stay in sync with CosmeticManager.cs:314-323.
    /// </summary>
    private static string GetCategoryFolder(CosmeticSlot.CosmeticCategory category)
    {
        switch (category)
        {
            case CosmeticSlot.CosmeticCategory.HEADWEAR:  return "Headwear";
            case CosmeticSlot.CosmeticCategory.TOP:       return "Top";
            case CosmeticSlot.CosmeticCategory.BOTTOM:    return "Bottom";
            case CosmeticSlot.CosmeticCategory.ACCESSORY: return "Accessory";
            default: return category.ToString();
        }
    }

    /// <summary>
    /// Generates an assetRef from category + FBX filename.
    /// Convention: {category_lowercase}_{sanitized_filename}
    /// Example: HEADWEAR + "Cup_LP.fbx" → "headwear_cup_lp"
    /// </summary>
    private static string SanitizeAssetRef(CosmeticSlot.CosmeticCategory category, string fbxFileName)
    {
        string name = Path.GetFileNameWithoutExtension(fbxFileName).ToLowerInvariant();
        name = name.Replace(' ', '_').Replace('-', '_');
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        return $"{category.ToString().ToLowerInvariant()}_{sb}";
    }

    private static float GetTargetSize(string assetRef, CosmeticSlot.CosmeticCategory category)
    {
        if (SizeOverrides.TryGetValue(assetRef, out float overrideSize))
            return overrideSize;
        return DefaultTargetSize.TryGetValue(category, out float catSize) ? catSize : 0.20f;
    }

    // ── Menu Items ──

    [MenuItem("Tools/GetGains/Build All Cosmetic Prefabs")]
    public static void BuildAll()
    {
        var entries = DiscoverFbxFiles();
        if (entries.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Cosmetic Prefab Builder",
                $"No FBX files found under '{SourceRoot}/'.\n\n" +
                "Place FBX models in category subfolders:\n" +
                "  HEADWEAR : Hats/, Facewear/, Headwear/, Helmets/\n" +
                "  TOP      : Tops/, Shirts/, Tanks/, Hoodies/\n" +
                "  BOTTOM   : Bottoms/, Pants/, Shorts/, Skirts/\n" +
                "  ACCESSORY: Accessories/, Wristbands/, Watches/, Gloves/",
                "OK");
            return;
        }

        // Ensure all output directories exist before building
        foreach (CosmeticSlot.CosmeticCategory cat in Enum.GetValues(typeof(CosmeticSlot.CosmeticCategory)))
        {
            string folder = GetCategoryFolder(cat);
            EnsureDirectory($"{OutputRootFlux}/{folder}");
            EnsureDirectory($"{OutputRootResources}/{folder}");
        }

        int ok = 0;
        int failed = 0;
        var lines = new List<string>();

        foreach (var entry in entries)
        {
            string assetRef       = SanitizeAssetRef(entry.Category, Path.GetFileName(entry.FbxAssetPath));
            float  targetSize     = GetTargetSize(assetRef, entry.Category);
            string categoryFolder = GetCategoryFolder(entry.Category);
            string fluxDir        = $"{OutputRootFlux}/{categoryFolder}";
            string resDir         = $"{OutputRootResources}/{categoryFolder}";

            if (BuildOne(entry.FbxAssetPath, assetRef, targetSize, Vector3.zero, fluxDir, resDir))
            {
                ok++;
                lines.Add($"  + {entry.Category}: {assetRef}  (size={targetSize:F2})");
            }
            else
            {
                failed++;
                lines.Add($"  x {entry.Category}: {Path.GetFileName(entry.FbxAssetPath)}  (FAILED — see Console)");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string summary = string.Join("\n", lines);
        EditorUtility.DisplayDialog(
            "Cosmetic Prefab Builder",
            $"Built {ok} prefab(s), {failed} failed.\n\n{summary}\n\n" +
            "Use 'Report Selected Cosmetic Bounds' to verify or tune scaling.",
            "OK");
    }

    // ── Core Build ──

    /// <returns>True if prefabs were written to both output directories.</returns>
    private static bool BuildOne(
        string  fbxAssetPath,
        string  assetRef,
        float   targetMaxWorldSize,
        Vector3 anchorRotationEuler,
        string  outputDirFlux,
        string  outputDirResources)
    {
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
        if (fbxRoot == null)
        {
            Debug.LogError(
                $"[CosmeticAnchorPrefabBuilder] FBX not found: '{fbxAssetPath}'. " +
                "Import the mesh or pull Git LFS, then run this menu again.");
            return false;
        }

        // Build hierarchy: Root → CosmeticAnchor → MeshHolder → FBX instance
        var root = new GameObject(assetRef);
        root.transform.position   = Vector3.zero;
        root.transform.rotation   = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        var anchor = new GameObject("CosmeticAnchor");
        anchor.transform.SetParent(root.transform, false);
        anchor.transform.localPosition = Vector3.zero;
        anchor.transform.localRotation = Quaternion.Euler(anchorRotationEuler);
        anchor.transform.localScale    = Vector3.one;

        var holder = new GameObject("MeshHolder");
        holder.transform.SetParent(anchor.transform, false);
        holder.transform.localPosition = Vector3.zero;
        holder.transform.localRotation = Quaternion.identity;
        holder.transform.localScale    = Vector3.one;

        var meshInstance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        meshInstance.name = Path.GetFileNameWithoutExtension(fbxAssetPath);
        meshInstance.transform.SetParent(holder.transform, false);
        meshInstance.transform.localPosition = Vector3.zero;
        meshInstance.transform.localRotation = Quaternion.identity;
        meshInstance.transform.localScale    = Vector3.one;

        StripCollidersRecursive(meshInstance);

        var renderers = meshInstance.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            Debug.LogError($"[CosmeticAnchorPrefabBuilder] No renderers found under '{fbxAssetPath}'.");
            Object.DestroyImmediate(root);
            return false;
        }

        // Centre mesh to bounds origin, then scale to target world size
        Bounds b = EncapsulateRenderers(renderers);
        holder.transform.position = -b.center;

        b = EncapsulateRenderers(renderers);
        float maxSize = Mathf.Max(b.size.x, b.size.y, b.size.z);
        if (maxSize > 1e-5f)
            holder.transform.localScale = Vector3.one * (targetMaxWorldSize / maxSize);

        // Save prefab and mirror to Resources
        string fluxPath = $"{outputDirFlux}/{assetRef}.prefab";
        string resPath  = $"{outputDirResources}/{assetRef}.prefab";

        if (AssetDatabase.LoadAssetAtPath<Object>(fluxPath) != null)
            AssetDatabase.DeleteAsset(fluxPath);
        if (AssetDatabase.LoadAssetAtPath<Object>(resPath) != null)
            AssetDatabase.DeleteAsset(resPath);

        PrefabUtility.SaveAsPrefabAsset(root, fluxPath);
        Object.DestroyImmediate(root);

        AssetDatabase.CopyAsset(fluxPath, resPath);
        Debug.Log($"[CosmeticAnchorPrefabBuilder] Built '{fluxPath}' and mirrored to '{resPath}'.");
        return true;
    }

    // ── Utilities ──

    private static Bounds EncapsulateRenderers(Renderer[] renderers)
    {
        var b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        return b;
    }

    private static void StripCollidersRecursive(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(c);
    }

    private static void EnsureDirectory(string assetPath)
    {
        assetPath = assetPath.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        var leaf   = Path.GetFileName(assetPath);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureDirectory(parent);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            AssetDatabase.CreateFolder(parent, leaf);
    }

    /// <summary>
    /// Logs world bounds and a scale suggestion for the selected cosmetic root in the Hierarchy.
    /// Useful for calibrating SizeOverrides entries.
    /// </summary>
    [MenuItem("Tools/GetGains/Report Selected Cosmetic Bounds")]
    public static void ReportBounds()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            Debug.LogWarning("[CosmeticBounds] Select a cosmetic prefab instance or root in the Hierarchy.");
            return;
        }

        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[CosmeticBounds] No Renderer found under selection.");
            return;
        }

        Bounds b       = EncapsulateRenderers(renderers);
        float maxSize  = Mathf.Max(b.size.x, b.size.y, b.size.z);
        const float exampleTarget = 0.22f;
        float suggested = maxSize > 1e-5f ? exampleTarget / maxSize : 1f;

        Debug.Log(
            $"[CosmeticBounds] '{go.name}'\n" +
            $"  bounds center={b.center}  size={b.size}  maxAxis={maxSize:F4}\n" +
            $"  Suggested scale to reach maxAxis≈{exampleTarget}: multiply by {suggested:F4}  (tune per item).");
    }
}
