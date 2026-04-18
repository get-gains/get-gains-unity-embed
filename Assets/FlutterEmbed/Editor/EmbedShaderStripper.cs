using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Strips Terrain Detail shader variants from Android builds.
///
/// The character embed scene contains no Unity Terrain, so these variants
/// are never needed at runtime. Stripping them:
///   - eliminates the "integer modulus may be much slower" Vulkan shader warning
///     (from URP ShaderVariablesFunctions.hlsl) for those three shaders
///   - reduces shader compile time and APK size marginally
///
/// The stripped shaders and their variants are:
///   Hidden/TerrainEngine/Details/UniversalPipeline/BillboardWavingDoublePass
///   Hidden/TerrainEngine/Details/UniversalPipeline/WavingDoublePass
///   Hidden/TerrainEngine/Details/UniversalPipeline/Vertexlit
/// </summary>
public class EmbedShaderStripper : IPreprocessShaders
{
    private static readonly HashSet<string> _stripNames = new HashSet<string>
    {
        "Hidden/TerrainEngine/Details/UniversalPipeline/BillboardWavingDoublePass",
        "Hidden/TerrainEngine/Details/UniversalPipeline/WavingDoublePass",
        "Hidden/TerrainEngine/Details/UniversalPipeline/Vertexlit",
    };

    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (_stripNames.Contains(shader.name))
        {
            data.Clear();
            Debug.Log($"[EmbedShaderStripper] Stripped all variants of '{shader.name}' (Terrain Detail — unused in character embed)");
        }
    }
}
