using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class GlbMaterialShaderReplacerWindow : EditorWindow
{
    const string DefaultOutputFolder = "Assets/Generated/GLBShaderReplacements";

    GameObject m_Target;
    Shader m_TargetShader;
    string m_OutputFolder = DefaultOutputFolder;
    bool m_CreatePrefabCopy = true;
    bool m_CopyColorsAndFloats = true;
    bool m_SelectResult = true;
    Vector2 m_Scroll;

    [MenuItem("Tools/GLB Material Shader Replacer")]
    static void Open()
    {
        GetWindow<GlbMaterialShaderReplacerWindow>("GLB Shader Replacer");
    }

    void OnGUI()
    {
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

        EditorGUILayout.LabelField("GLB Material Shader Replacer", EditorStyles.boldLabel);
        EditorGUILayout.Space(6);

        m_Target = (GameObject)EditorGUILayout.ObjectField("Target GLB/Prefab/Object", m_Target, typeof(GameObject), true);
        m_TargetShader = (Shader)EditorGUILayout.ObjectField("Target Shader", m_TargetShader, typeof(Shader), false);
        m_OutputFolder = EditorGUILayout.TextField("Output Folder", m_OutputFolder);

        bool targetIsSceneObject = IsSceneObject(m_Target);
        if (m_Target != null)
            m_CreatePrefabCopy = !targetIsSceneObject;

        using (new EditorGUI.DisabledScope(true))
        {
            m_CreatePrefabCopy = EditorGUILayout.Toggle("Create Prefab Copy", m_CreatePrefabCopy);
        }

        m_CopyColorsAndFloats = EditorGUILayout.Toggle("Copy Colors/Floats", m_CopyColorsAndFloats);
        m_SelectResult = EditorGUILayout.Toggle("Select Result", m_SelectResult);

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "Project GLB/Prefab: creates new materials and optionally a prefab copy.\nScene object: replaces materials on the selected instance.\nTextures are matched by common PBR semantics first, then by similar property names.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(m_Target == null || m_TargetShader == null))
        {
            if (GUILayout.Button("Replace Materials", GUILayout.Height(30)))
                Replace();
        }

        EditorGUILayout.EndScrollView();
    }

    void Replace()
    {
        if (m_Target == null || m_TargetShader == null)
            return;

        string outputFolder = EnsureOutputFolder(m_OutputFolder);
        bool targetIsSceneObject = IsSceneObject(m_Target);

        GameObject workingObject = m_Target;
        GameObject tempInstance = null;
        string createdPrefabPath = null;

        try
        {
            if (!targetIsSceneObject)
            {
                tempInstance = (GameObject)PrefabUtility.InstantiatePrefab(m_Target);
                if (tempInstance == null)
                    tempInstance = Instantiate(m_Target);

                tempInstance.name = m_Target.name;
                workingObject = tempInstance;
            }

            var options = new GlbMaterialShaderReplacer.Options
            {
                OutputFolder = outputFolder,
                CopyColorsAndFloats = m_CopyColorsAndFloats
            };

            GlbMaterialShaderReplacer.Result result = GlbMaterialShaderReplacer.ReplaceMaterials(workingObject, m_TargetShader, options);

            if (!targetIsSceneObject)
            {
                string prefabName = $"{SanitizeFileName(m_Target.name)}_{SanitizeFileName(m_TargetShader.name)}.prefab";
                createdPrefabPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(outputFolder, prefabName));
                PrefabUtility.SaveAsPrefabAsset(workingObject, createdPrefabPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (m_SelectResult)
            {
                if (!string.IsNullOrEmpty(createdPrefabPath))
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(createdPrefabPath);
                else
                    Selection.activeObject = workingObject;
            }

            Debug.Log(
                $"GLB shader replacement complete. Renderers: {result.RendererCount}, slots: {result.SlotCount}, created materials: {result.CreatedMaterialCount}" +
                (string.IsNullOrEmpty(createdPrefabPath) ? string.Empty : $", prefab: {createdPrefabPath}"));
        }
        finally
        {
            if (tempInstance != null)
                DestroyImmediate(tempInstance);
        }
    }

    static bool IsSceneObject(GameObject gameObject)
    {
        return gameObject != null && !EditorUtility.IsPersistent(gameObject);
    }

    static string EnsureOutputFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            folder = DefaultOutputFolder;

        folder = folder.Replace("\\", "/").TrimEnd('/');
        if (!folder.StartsWith("Assets", StringComparison.Ordinal))
            folder = Path.Combine("Assets", folder).Replace("\\", "/");

        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }

        return folder;
    }

    static string SanitizeFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Asset";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value.Replace('/', '_').Replace('\\', '_');
    }
}

public static class GlbMaterialShaderReplacer
{
    public struct Options
    {
        public string OutputFolder;
        public bool CopyColorsAndFloats;
    }

    public struct Result
    {
        public int RendererCount;
        public int SlotCount;
        public int CreatedMaterialCount;
    }

    enum TextureRole
    {
        BaseColor,
        Normal,
        Metallic,
        Roughness,
        Occlusion,
        Emission,
        Height,
        Detail,
        Mask,
        Unknown
    }

    static readonly Dictionary<string, TextureRole> KnownTextureRoles = new(StringComparer.Ordinal)
    {
        ["_BaseMap"] = TextureRole.BaseColor,
        ["_MainTex"] = TextureRole.BaseColor,
        ["_BaseColorMap"] = TextureRole.BaseColor,
        ["baseColorTexture"] = TextureRole.BaseColor,
        ["_BumpMap"] = TextureRole.Normal,
        ["_NormalMap"] = TextureRole.Normal,
        ["normalTexture"] = TextureRole.Normal,
        ["_MetallicGlossMap"] = TextureRole.Metallic,
        ["_MetallicMap"] = TextureRole.Metallic,
        ["metallicRoughnessTexture"] = TextureRole.Metallic,
        ["_SpecGlossMap"] = TextureRole.Roughness,
        ["_RoughnessMap"] = TextureRole.Roughness,
        ["_OcclusionMap"] = TextureRole.Occlusion,
        ["occlusionTexture"] = TextureRole.Occlusion,
        ["_EmissionMap"] = TextureRole.Emission,
        ["_EmissiveMap"] = TextureRole.Emission,
        ["emissiveTexture"] = TextureRole.Emission,
        ["_ParallaxMap"] = TextureRole.Height,
        ["_HeightMap"] = TextureRole.Height,
        ["_DetailMask"] = TextureRole.Detail,
        ["_DetailAlbedoMap"] = TextureRole.Detail,
        ["_DetailNormalMap"] = TextureRole.Detail,
        ["_MaskMap"] = TextureRole.Mask
    };

    static readonly Dictionary<TextureRole, string[]> RoleKeywords = new()
    {
        [TextureRole.BaseColor] = new[] { "base", "albedo", "main", "diffuse", "color" },
        [TextureRole.Normal] = new[] { "normal", "bump", "nrm" },
        [TextureRole.Metallic] = new[] { "metal", "metallic" },
        [TextureRole.Roughness] = new[] { "rough", "smooth", "gloss", "spec" },
        [TextureRole.Occlusion] = new[] { "occlusion", "ao", "ambient" },
        [TextureRole.Emission] = new[] { "emission", "emissive", "emit" },
        [TextureRole.Height] = new[] { "height", "parallax", "displace" },
        [TextureRole.Detail] = new[] { "detail" },
        [TextureRole.Mask] = new[] { "mask" }
    };

    public static Result ReplaceMaterials(GameObject root, Shader targetShader, Options options)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));
        if (targetShader == null)
            throw new ArgumentNullException(nameof(targetShader));

        if (string.IsNullOrEmpty(options.OutputFolder))
            options.OutputFolder = "Assets";
        options.OutputFolder = EnsureAssetFolder(options.OutputFolder);

        var materialCache = new Dictionary<Material, Material>();
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

        var result = new Result { RendererCount = renderers.Length };

        foreach (Renderer renderer in renderers)
        {
            Material[] sharedMaterials = renderer.sharedMaterials;
            bool changed = false;

            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                Material sourceMaterial = sharedMaterials[i];
                if (sourceMaterial == null)
                    continue;

                if (!materialCache.TryGetValue(sourceMaterial, out Material replacement))
                {
                    replacement = CreateReplacementMaterial(sourceMaterial, targetShader, options);
                    materialCache.Add(sourceMaterial, replacement);
                    result.CreatedMaterialCount++;
                }

                sharedMaterials[i] = replacement;
                result.SlotCount++;
                changed = true;
            }

            if (changed)
                renderer.sharedMaterials = sharedMaterials;
        }

        return result;
    }

    static Material CreateReplacementMaterial(Material sourceMaterial, Shader targetShader, Options options)
    {
        var replacement = new Material(targetShader)
        {
            name = $"{sourceMaterial.name}_{targetShader.name.Split('/').Last()}",
            renderQueue = sourceMaterial.renderQueue
        };

        CopyTextures(sourceMaterial, replacement);

        if (options.CopyColorsAndFloats)
        {
            CopyColors(sourceMaterial, replacement);
            CopyFloats(sourceMaterial, replacement);
        }

        string fileName = $"{SanitizeFileName(replacement.name)}.mat";
        string path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(options.OutputFolder, fileName));
        AssetDatabase.CreateAsset(replacement, path);
        return replacement;
    }

    static void CopyTextures(Material source, Material target)
    {
        string[] sourceTextureProperties = source.GetTexturePropertyNames();
        string[] targetTextureProperties = target.GetTexturePropertyNames();

        var sourceInfos = sourceTextureProperties
            .Select(property => new TextureInfo(source, property))
            .Where(info => info.Texture != null)
            .ToList();

        var usedSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (string targetProperty in targetTextureProperties)
        {
            TextureInfo? match = FindBestTextureMatch(targetProperty, sourceInfos, usedSources);
            if (!match.HasValue)
                continue;

            TextureInfo info = match.Value;
            target.SetTexture(targetProperty, info.Texture);
            CopyTextureScaleOffset(source, target, info.PropertyName, targetProperty);
            usedSources.Add(info.PropertyName);
        }
    }

    static TextureInfo? FindBestTextureMatch(string targetProperty, List<TextureInfo> sourceInfos, HashSet<string> usedSources)
    {
        TextureRole targetRole = GuessTextureRole(targetProperty);

        TextureInfo? best = null;
        int bestScore = int.MinValue;

        foreach (TextureInfo info in sourceInfos)
        {
            int score = ScoreTextureMatch(targetProperty, targetRole, info);
            if (usedSources.Contains(info.PropertyName))
                score -= 10;

            if (score > bestScore)
            {
                best = info;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    static int ScoreTextureMatch(string targetProperty, TextureRole targetRole, TextureInfo sourceInfo)
    {
        TextureRole sourceRole = sourceInfo.Role;
        int score = 0;

        if (sourceRole != TextureRole.Unknown && sourceRole == targetRole)
            score += 100;

        if (string.Equals(NormalizeName(sourceInfo.PropertyName), NormalizeName(targetProperty), StringComparison.Ordinal))
            score += 80;

        if (targetRole != TextureRole.Unknown && RoleKeywords.TryGetValue(targetRole, out string[] targetKeywords))
        {
            string sourceName = NormalizeName(sourceInfo.PropertyName);
            string textureName = NormalizeName(sourceInfo.Texture.name);
            foreach (string keyword in targetKeywords)
            {
                if (sourceName.Contains(keyword, StringComparison.Ordinal))
                    score += 20;
                if (textureName.Contains(keyword, StringComparison.Ordinal))
                    score += 12;
            }
        }

        string targetName = NormalizeName(targetProperty);
        string sourcePropertyName = NormalizeName(sourceInfo.PropertyName);
        if (sourcePropertyName.Contains(targetName, StringComparison.Ordinal) || targetName.Contains(sourcePropertyName, StringComparison.Ordinal))
            score += 10;

        if (targetRole == TextureRole.Unknown && sourceRole == TextureRole.Unknown)
            score += 1;

        return score;
    }

    static void CopyTextureScaleOffset(Material source, Material target, string sourceProperty, string targetProperty)
    {
        try
        {
            target.SetTextureScale(targetProperty, source.GetTextureScale(sourceProperty));
            target.SetTextureOffset(targetProperty, source.GetTextureOffset(sourceProperty));
        }
        catch (ArgumentException)
        {
            // Some importer-created material properties do not expose scale/offset.
        }
    }

    static void CopyColors(Material source, Material target)
    {
        string[] sourceProperties = GetShaderPropertyNames(source.shader, ShaderUtil.ShaderPropertyType.Color);
        string[] targetProperties = GetShaderPropertyNames(target.shader, ShaderUtil.ShaderPropertyType.Color);
        CopyByRoleOrName(sourceProperties, targetProperties, property => source.GetColor(property), (property, value) => target.SetColor(property, value), GuessColorRole);
    }

    static void CopyFloats(Material source, Material target)
    {
        string[] sourceProperties = GetShaderPropertyNames(source.shader, ShaderUtil.ShaderPropertyType.Float, ShaderUtil.ShaderPropertyType.Range);
        string[] targetProperties = GetShaderPropertyNames(target.shader, ShaderUtil.ShaderPropertyType.Float, ShaderUtil.ShaderPropertyType.Range);

        CopyByRoleOrName(sourceProperties, targetProperties, property => source.GetFloat(property), (property, value) => target.SetFloat(property, value), GuessFloatRole);
    }

    static string[] GetShaderPropertyNames(Shader shader, params ShaderUtil.ShaderPropertyType[] propertyTypes)
    {
        if (shader == null)
            return Array.Empty<string>();

        var typeSet = new HashSet<ShaderUtil.ShaderPropertyType>(propertyTypes);
        var names = new List<string>();
        int propertyCount = ShaderUtil.GetPropertyCount(shader);

        for (int i = 0; i < propertyCount; i++)
        {
            ShaderUtil.ShaderPropertyType propertyType = ShaderUtil.GetPropertyType(shader, i);
            if (!typeSet.Contains(propertyType))
                continue;

            names.Add(ShaderUtil.GetPropertyName(shader, i));
        }

        return names.ToArray();
    }

    static void CopyByRoleOrName<T>(
        string[] sourceProperties,
        string[] targetProperties,
        Func<string, T> getSourceValue,
        Action<string, T> setTargetValue,
        Func<string, string> guessRole)
    {
        var usedSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (string targetProperty in targetProperties)
        {
            string targetRole = guessRole(targetProperty);
            string bestSource = null;
            int bestScore = int.MinValue;

            foreach (string sourceProperty in sourceProperties)
            {
                int score = 0;
                string sourceRole = guessRole(sourceProperty);

                if (!string.IsNullOrEmpty(targetRole) && targetRole == sourceRole)
                    score += 100;

                if (NormalizeName(sourceProperty) == NormalizeName(targetProperty))
                    score += 80;

                if (usedSources.Contains(sourceProperty))
                    score -= 10;

                if (score > bestScore)
                {
                    bestSource = sourceProperty;
                    bestScore = score;
                }
            }

            if (bestSource == null || bestScore <= 0)
                continue;

            setTargetValue(targetProperty, getSourceValue(bestSource));
            usedSources.Add(bestSource);
        }
    }

    static TextureRole GuessTextureRole(string propertyName)
    {
        if (KnownTextureRoles.TryGetValue(propertyName, out TextureRole role))
            return role;

        string normalized = NormalizeName(propertyName);
        foreach (var pair in RoleKeywords)
        {
            if (pair.Value.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal)))
                return pair.Key;
        }

        return TextureRole.Unknown;
    }

    static string GuessColorRole(string propertyName)
    {
        string normalized = NormalizeName(propertyName);
        if (normalized.Contains("base", StringComparison.Ordinal) ||
            normalized.Contains("albedo", StringComparison.Ordinal) ||
            normalized.Contains("main", StringComparison.Ordinal) ||
            normalized.Contains("color", StringComparison.Ordinal))
            return "base";
        if (normalized.Contains("emission", StringComparison.Ordinal) || normalized.Contains("emissive", StringComparison.Ordinal))
            return "emission";
        if (normalized.Contains("spec", StringComparison.Ordinal))
            return "specular";
        return string.Empty;
    }

    static string GuessFloatRole(string propertyName)
    {
        string normalized = NormalizeName(propertyName);
        if (normalized.Contains("metal", StringComparison.Ordinal))
            return "metallic";
        if (normalized.Contains("smooth", StringComparison.Ordinal) || normalized.Contains("gloss", StringComparison.Ordinal))
            return "smoothness";
        if (normalized.Contains("rough", StringComparison.Ordinal))
            return "roughness";
        if (normalized.Contains("cutoff", StringComparison.Ordinal) || normalized.Contains("alpha", StringComparison.Ordinal))
            return "alpha";
        if (normalized.Contains("bump", StringComparison.Ordinal) || normalized.Contains("normal", StringComparison.Ordinal))
            return "normal";
        if (normalized.Contains("occlusion", StringComparison.Ordinal) || normalized.Contains("ao", StringComparison.Ordinal))
            return "occlusion";
        if (normalized.Contains("emission", StringComparison.Ordinal) || normalized.Contains("emissive", StringComparison.Ordinal))
            return "emission";
        return string.Empty;
    }

    static string NormalizeName(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("_", string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
    }

    static string SanitizeFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Material";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value.Replace('/', '_').Replace('\\', '_');
    }

    static string EnsureAssetFolder(string folder)
    {
        folder = folder.Replace("\\", "/").TrimEnd('/');
        if (string.IsNullOrEmpty(folder))
            folder = "Assets";
        if (!folder.StartsWith("Assets", StringComparison.Ordinal))
            folder = Path.Combine("Assets", folder).Replace("\\", "/");

        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }

        return folder;
    }

    readonly struct TextureInfo
    {
        public TextureInfo(Material material, string propertyName)
        {
            PropertyName = propertyName;
            Texture = material.GetTexture(propertyName);
            Role = GuessTextureRole(propertyName);
        }

        public readonly string PropertyName;
        public readonly Texture Texture;
        public readonly TextureRole Role;
    }
}
