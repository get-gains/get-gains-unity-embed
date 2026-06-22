using UnityEngine;

namespace FlutterEmbed
{
    /// <summary>
    /// Replaces every Renderer material under this GameObject with a shared avatar material.
    /// Attach to the character model root so the workout/cosmetic views always use the same look.
    /// Supports a runtime color tint so the same source material can be reused with different hues
    /// (e.g. green coach vs orange client in side-by-side comparison mode).
    /// </summary>
    public class AvatarMaterialApplier : MonoBehaviour
    {
        [SerializeField] private Material material;
        [SerializeField] private bool includeInactive = true;

        /// <summary>Runtime tint applied on top of the source material. White = no tint.</summary>
        [ColorUsage(false, true)]
        [SerializeField] private Color tint = Color.white;

        private Material _instancedMaterial;

        private void Start() => Apply();

        private void OnDestroy()
        {
            if (_instancedMaterial != null)
            {
                Destroy(_instancedMaterial);
                _instancedMaterial = null;
            }
        }

        /// <summary>Sets a runtime color tint and re-applies materials.</summary>
        public void SetTint(Color color)
        {
            tint = color;
            Apply();
        }

        /// <summary>Assigns <see cref="material"/> to all child renderers.</summary>
        public void Apply()
        {
            if (material == null) return;

            if (_instancedMaterial == null)
            {
                _instancedMaterial = new Material(material);
            }

            _instancedMaterial.color = tint;
            if (_instancedMaterial.HasProperty("_BaseColor"))
                _instancedMaterial.SetColor("_BaseColor", tint);
            if (_instancedMaterial.HasProperty("_Color"))
                _instancedMaterial.SetColor("_Color", tint);

            foreach (var renderer in GetComponentsInChildren<Renderer>(includeInactive))
                renderer.sharedMaterial = _instancedMaterial;
        }
    }
}
