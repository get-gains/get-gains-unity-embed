#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Creates simple placeholder prefabs for missing cosmetic asset ids (editor-only).</summary>
public static class PlaceholderCosmeticGenerator
{
    [MenuItem("Flutter Embed/Cosmetics/Generate placeholder cosmetic prefabs")]
    public static void Generate()
    {
        const string outRes = "Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets";
        const string mirror = "Assets/FlutterEmbed/Cosmetics/CosmeticAssets";
        Directory.CreateDirectory(outRes);
        Directory.CreateDirectory(mirror);

        foreach (var (id, kind, color) in new[]
                 {
                     ("placeholder_full_head", CosmeticAnchorKind.Head, new Color(0.6f, 0.7f, 1f)),
                     ("placeholder_facewear", CosmeticAnchorKind.Facewear, new Color(1f, 0.75f, 0.5f)),
                     ("placeholder_hat", CosmeticAnchorKind.HatTop, new Color(0.7f, 0.5f, 1f)),
                 })
        {
            var root = new GameObject(id);
            var attach = root.AddComponent<CosmeticAttachment>();
            attach.ApplyDefaults(kind);

            var holder = new GameObject("MeshHolder");
            holder.transform.SetParent(root.transform, false);

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "PlaceholderMesh";
            Object.DestroyImmediate(sphere.GetComponent<Collider>());
            sphere.transform.SetParent(holder.transform, false);
            sphere.transform.localPosition = Vector3.zero;
            sphere.transform.localRotation = Quaternion.identity;
            sphere.transform.localScale = kind == CosmeticAnchorKind.HatTop
                ? new Vector3(0.28f, 0.12f, 0.28f)
                : kind == CosmeticAnchorKind.Facewear
                    ? new Vector3(0.22f, 0.08f, 0.12f)
                    : new Vector3(0.2f, 0.22f, 0.2f);

            var r = sphere.GetComponent<Renderer>();
            if (r != null)
            {
                var s = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Unlit/Color");
                var mat = new Material(s) { color = color };
                r.sharedMaterial = mat;
            }

            var path = $"{outRes}/{id}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            var mp = $"{mirror}/{id}.prefab";
            if (File.Exists(mp)) AssetDatabase.DeleteAsset(mp);
            AssetDatabase.CopyAsset(path, mp);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PlaceholderCosmeticGenerator] Placeholder prefabs written.");
    }
}
#endif
