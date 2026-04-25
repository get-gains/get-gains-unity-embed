using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// One-click cosmetic scene wiring.
///
/// Menu: Tools → GetGains → Complete Cosmetic Scene Setup
///
/// What it does (idempotent — safe to run multiple times):
///   1. Finds the humanoid character in the scene (Animator + Humanoid Avatar).
///   2. Creates CosmeticSlot_ child GameObjects on the correct humanoid bones.
///   3. Creates (or updates) four CosmeticSlot ScriptableObjects under Assets/FlutterEmbed/Cosmetics/Slots/.
///   4. Adds CosmeticManager to the character if not already present.
///   5. Assigns the ScriptableObjects to CosmeticManager.slots.
///   6. Saves the scene.
/// </summary>
public static class CosmeticCompleteSetup
{
    private const string SlotsOutputDir = "Assets/FlutterEmbed/Cosmetics/Slots";

    private struct SlotDef
    {
        public string Name;
        public CosmeticSlot.CosmeticCategory Category;
        public HumanBodyBones Bone;
        public HumanBodyBones[] FallbackBones; // tried in order if primary not found
        public Vector3 LocalOffset;
    }

    private static readonly SlotDef[] SlotDefs = new[]
    {
        new SlotDef
        {
            Name          = "CosmeticSlot_Headwear",
            Category      = CosmeticSlot.CosmeticCategory.HEADWEAR,
            Bone          = HumanBodyBones.Head,
            FallbackBones = new HumanBodyBones[0],
            LocalOffset   = new Vector3(0f, 0.15f, 0f),
        },
        new SlotDef
        {
            Name          = "CosmeticSlot_Top",
            Category      = CosmeticSlot.CosmeticCategory.TOP,
            Bone          = HumanBodyBones.UpperChest,
            FallbackBones = new[] { HumanBodyBones.Chest, HumanBodyBones.Spine },
            LocalOffset   = Vector3.zero,
        },
        new SlotDef
        {
            Name          = "CosmeticSlot_Bottom",
            Category      = CosmeticSlot.CosmeticCategory.BOTTOM,
            Bone          = HumanBodyBones.Hips,
            FallbackBones = new HumanBodyBones[0],
            LocalOffset   = Vector3.zero,
        },
        new SlotDef
        {
            Name          = "CosmeticSlot_Accessory",
            Category      = CosmeticSlot.CosmeticCategory.ACCESSORY,
            Bone          = HumanBodyBones.RightHand,
            FallbackBones = new HumanBodyBones[0],
            LocalOffset   = Vector3.zero,
        },
    };

    [MenuItem("Tools/GetGains/Complete Cosmetic Scene Setup")]
    public static void Run()
    {
        // ── 1. Find humanoid character ──────────────────────────────────────
        Animator animator = FindHumanoidAnimator();
        if (animator == null)
        {
            EditorUtility.DisplayDialog(
                "Complete Cosmetic Scene Setup",
                "No humanoid character found in the scene.\n\n" +
                "Make sure there is a GameObject with an Animator component that has a " +
                "valid Humanoid Avatar assigned.",
                "OK");
            return;
        }

        GameObject character = animator.gameObject;
        Debug.Log($"[CosmeticSetup] Using character: '{character.name}'");

        Undo.SetCurrentGroupName("Complete Cosmetic Scene Setup");
        int undoGroup = Undo.GetCurrentGroup();

        // ── 2. Create bone anchor GameObjects ──────────────────────────────
        EnsureDirectory(SlotsOutputDir);

        CosmeticSlot[] slotAssets = new CosmeticSlot[SlotDefs.Length];

        for (int i = 0; i < SlotDefs.Length; i++)
        {
            var def = SlotDefs[i];

            // Resolve the bone Transform with fallbacks
            Transform bone = ResolveBone(animator, def);
            if (bone == null)
            {
                Debug.LogWarning(
                    $"[CosmeticSetup] Could not find bone for '{def.Name}'. Skipping slot.");
                continue;
            }

            // Find or create the anchor child on this bone
            Transform anchor = bone.Find(def.Name);
            if (anchor == null)
            {
                var go = new GameObject(def.Name);
                Undo.RegisterCreatedObjectUndo(go, $"Create {def.Name}");
                go.transform.SetParent(bone, false);
                go.transform.localPosition = def.LocalOffset;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale    = Vector3.one;
                anchor = go.transform;
                Debug.Log($"[CosmeticSetup] Created anchor '{def.Name}' on bone '{bone.name}'.");
            }
            else
            {
                Debug.Log($"[CosmeticSetup] Reusing existing anchor '{def.Name}' on bone '{bone.name}'.");
            }

            // ── 3. Create or update CosmeticSlot ScriptableObject ──────────
            string assetPath = $"{SlotsOutputDir}/{def.Name}.asset";
            CosmeticSlot slotAsset = AssetDatabase.LoadAssetAtPath<CosmeticSlot>(assetPath);

            if (slotAsset == null)
            {
                slotAsset = ScriptableObject.CreateInstance<CosmeticSlot>();
                AssetDatabase.CreateAsset(slotAsset, assetPath);
                Debug.Log($"[CosmeticSetup] Created CosmeticSlot asset: '{assetPath}'.");
            }
            else
            {
                Debug.Log($"[CosmeticSetup] Updating existing CosmeticSlot asset: '{assetPath}'.");
            }

            // Write fields via SerializedObject so Undo and dirty-marking work correctly
            var so = new SerializedObject(slotAsset);
            so.FindProperty("category").enumValueIndex      = (int)def.Category;
            so.FindProperty("attachmentPoint").objectReferenceValue = anchor;
            so.FindProperty("defaultOffset").vector3Value   = Vector3.zero;
            so.FindProperty("defaultRotation").vector3Value = Vector3.zero;
            so.FindProperty("defaultScale").vector3Value    = Vector3.one;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(slotAsset);

            slotAssets[i] = slotAsset;
        }

        AssetDatabase.SaveAssets();

        // ── 4. Add CosmeticManager to the character if missing ─────────────
        CosmeticManager manager = character.GetComponentInChildren<CosmeticManager>();
        if (manager == null)
        {
            Undo.AddComponent<CosmeticManager>(character);
            manager = character.GetComponent<CosmeticManager>();
            Debug.Log($"[CosmeticSetup] Added CosmeticManager to '{character.name}'.");
        }
        else
        {
            Debug.Log($"[CosmeticSetup] CosmeticManager already present on '{manager.gameObject.name}'.");
        }

        // ── 5. Assign slot ScriptableObjects to CosmeticManager ───────────
        var managerSo = new SerializedObject(manager);
        SerializedProperty slotsProp = managerSo.FindProperty("slots");

        // Count non-null slots
        int validCount = 0;
        foreach (var s in slotAssets)
            if (s != null) validCount++;

        slotsProp.arraySize = validCount;
        int idx = 0;
        foreach (var s in slotAssets)
        {
            if (s == null) continue;
            slotsProp.GetArrayElementAtIndex(idx).objectReferenceValue = s;
            idx++;
        }
        managerSo.ApplyModifiedProperties();
        EditorUtility.SetDirty(manager);

        // ── 6. Save scene ──────────────────────────────────────────────────
        Undo.CollapseUndoOperations(undoGroup);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

        EditorUtility.DisplayDialog(
            "Complete Cosmetic Scene Setup",
            $"Done! Wired {validCount} cosmetic slot(s) on '{character.name}'.\n\n" +
            "Slots created:\n" +
            "  CosmeticSlot_Headwear  → Head\n" +
            "  CosmeticSlot_Top       → UpperChest\n" +
            "  CosmeticSlot_Bottom    → Hips\n" +
            "  CosmeticSlot_Accessory → RightHand\n\n" +
            "CosmeticManager is attached and ready.\n" +
            "Run 'Build All Cosmetic Prefabs' if you haven't already.",
            "OK");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Animator FindHumanoidAnimator()
    {
        // Prefer the selected object, fall back to scene search
        if (Selection.activeGameObject != null)
        {
            var a = Selection.activeGameObject.GetComponentInChildren<Animator>();
            if (a != null && a.avatar != null && a.avatar.isHuman)
                return a;
        }

        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude))
        {
            if (a.avatar != null && a.avatar.isHuman)
                return a;
        }

        return null;
    }

    private static Transform ResolveBone(Animator animator, SlotDef def)
    {
        Transform t = animator.GetBoneTransform(def.Bone);
        if (t != null) return t;

        foreach (var fallback in def.FallbackBones)
        {
            t = animator.GetBoneTransform(fallback);
            if (t != null)
            {
                Debug.Log(
                    $"[CosmeticSetup] Primary bone '{def.Bone}' not found; " +
                    $"using fallback '{fallback}' for slot '{def.Name}'.");
                return t;
            }
        }

        return null;
    }

    private static void EnsureDirectory(string assetPath)
    {
        assetPath = assetPath.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        var parent = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        var leaf   = System.IO.Path.GetFileName(assetPath);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureDirectory(parent);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            AssetDatabase.CreateFolder(parent, leaf);
    }
}
