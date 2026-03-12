using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Editor utility to generate placeholder cosmetic prefabs for testing.
/// Creates one simple primitive-based prefab per category under
/// Assets/FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/.
///
/// Also creates a Resources mirror at Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets/
/// so that CosmeticManager can load them via Resources.Load at runtime.
///
/// Usage: Tools → GetGains → Generate Placeholder Cosmetic Prefabs
/// </summary>
public class PlaceholderCosmeticGenerator : EditorWindow
{
    private struct PrefabDef
    {
        public string AssetRef;
        public string Category;
        public PrimitiveType Shape;
        public Vector3 Scale;
        public Color Color;
    }

    private static readonly PrefabDef[] Prefabs = new[]
    {
        // Headwear — small sphere on top of head
        new PrefabDef
        {
            AssetRef = "headwear_placeholder_cap",
            Category = "Headwear",
            Shape = PrimitiveType.Sphere,
            Scale = new Vector3(0.25f, 0.12f, 0.25f),
            Color = new Color(0.9f, 0.3f, 0.1f) // Orange
        },
        // Top — flat cube on chest
        new PrefabDef
        {
            AssetRef = "top_placeholder_vest",
            Category = "Top",
            Shape = PrimitiveType.Cube,
            Scale = new Vector3(0.35f, 0.4f, 0.2f),
            Color = new Color(0.2f, 0.5f, 0.9f) // Blue
        },
        // Bottom — cylinder on hips
        new PrefabDef
        {
            AssetRef = "bottom_placeholder_shorts",
            Category = "Bottom",
            Shape = PrimitiveType.Cylinder,
            Scale = new Vector3(0.3f, 0.2f, 0.3f),
            Color = new Color(0.3f, 0.7f, 0.3f) // Green
        },
        // Accessory — small cube on hand
        new PrefabDef
        {
            AssetRef = "accessory_placeholder_band",
            Category = "Accessory",
            Shape = PrimitiveType.Cube,
            Scale = new Vector3(0.08f, 0.08f, 0.08f),
            Color = new Color(0.9f, 0.8f, 0.1f) // Yellow/gold
        }
    };

    [MenuItem("Tools/GetGains/Generate Placeholder Cosmetic Prefabs")]
    public static void GeneratePlaceholders()
    {
        int created = 0;
        int skipped = 0;

        foreach (var def in Prefabs)
        {
            // Create in both the asset folder and Resources folder
            string assetDir = $"Assets/FlutterEmbed/Cosmetics/CosmeticAssets/{def.Category}";
            string resourceDir = $"Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets/{def.Category}";

            bool createdHere = CreatePrefab(def, assetDir, ref created, ref skipped);
            CreatePrefab(def, resourceDir, ref created, ref skipped);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Placeholder Cosmetics",
            $"Done! Created {created} prefab(s), skipped {skipped} existing.\n\n" +
            "These are simple colored primitives for testing the cosmetic pipeline.\n" +
            "Replace with real models when available.",
            "OK");
    }

    private static bool CreatePrefab(PrefabDef def, string directory, ref int created, ref int skipped)
    {
        string prefabPath = $"{directory}/{def.AssetRef}.prefab";

        // Ensure directory exists
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }

        // Skip if already exists
        if (File.Exists(prefabPath))
        {
            skipped++;
            Debug.Log($"[PlaceholderCosmeticGenerator] Skipping existing: {prefabPath}");
            return false;
        }

        // Create a primitive in the scene temporarily
        var go = GameObject.CreatePrimitive(def.Shape);
        go.name = def.AssetRef;
        go.transform.localScale = def.Scale;

        // Remove the collider (not needed for cosmetics)
        var collider = go.GetComponent<Collider>();
        if (collider != null)
            DestroyImmediate(collider);

        // Create a simple colored material
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (material == null)
            material = new Material(Shader.Find("Standard"));
        material.color = def.Color;

        // Save material as asset
        string matPath = $"{directory}/{def.AssetRef}_mat.mat";
        if (!File.Exists(matPath))
        {
            AssetDatabase.CreateAsset(material, matPath);
        }
        else
        {
            material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        }

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;

        // Save as prefab
        PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        created++;
        Debug.Log($"[PlaceholderCosmeticGenerator] Created: {prefabPath}");
        return true;
    }
}
