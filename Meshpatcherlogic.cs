using System.Linq;
using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NifFile = NiflySharp.NifFile;
using BSLightingShaderProperty = NiflySharp.Blocks.BSLightingShaderProperty;
using BSShaderTextureSet = NiflySharp.Blocks.BSShaderTextureSet;
using SkyrimShaderPropertyFlags1 = NiflySharp.Enums.SkyrimShaderPropertyFlags1;
using SkyrimShaderPropertyFlags2 = NiflySharp.Enums.SkyrimShaderPropertyFlags2;


namespace MeshPatcherProject
{
    /// <summary>
    /// Core batch-patching logic, extracted so it can be driven from either a console
    /// entry point or a GUI without duplicating anything. All progress/status goes through
    /// the supplied <paramref name="log"/> callback rather than Console.WriteLine.
    /// </summary>
    internal static class MeshPatcherLogic
    {
        public class RunResult
        {
            public int PatchedFiles;
            public int PatchedShapes;
            public int CopiedFiles;
            public int SkippedFiles;
        }

        public static RunResult Run(string inputFolder, string settingsPath, string outputFolder,
            bool dryRun, Action<string> log)
        {
            var result = new RunResult();

            var settings = Settings.LoadFromFile(settingsPath);
            log($"Loaded preset '{settings.PresetName}' from {settingsPath}");
            log($"Input:  {inputFolder}");
            log($"Output: {outputFolder}");

            var nifFiles = Directory.EnumerateFiles(inputFolder, "*.nif", SearchOption.AllDirectories);

            foreach (var nifPath in nifFiles)
            {
                var relativePath = Path.GetRelativePath(inputFolder, nifPath);
                var outputPath = Path.Combine(outputFolder, relativePath);

                var nif = new NifFile();
                var loadResult = nif.Load(nifPath);
                if (loadResult != 0 || !nif.Valid)
                {
                    log($"  [skip] Failed to load: {relativePath}");
                    result.SkippedFiles++;
                    continue;
                }

                int shapesPatchedInFile = 0;

                foreach (var shape in nif.GetShapes())
                {
                    if (shape.ShaderPropertyRef is null || shape.ShaderPropertyRef.IsEmpty())
                        continue;

                    var shader = nif.GetBlock<INiObject>(shape.ShaderPropertyRef);
                    if (shader is not BSLightingShaderProperty lsp)
                        continue;

                    // Only touch shapes actually set up for environment mapping - everything else
                    // (Default, Glow_Shader, Parallax, Skin_Tint, etc.) is left completely alone.
                    if (!IsEnvironmentMapShader(lsp))
                        continue;

                    ApplySettings(nif, lsp, settings, log);
                    shapesPatchedInFile++;
                }

                if (!dryRun)
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                if (shapesPatchedInFile > 0)
                {
                    log($"  [patch] {relativePath} ({shapesPatchedInFile} shape(s))");
                    result.PatchedFiles++;
                    result.PatchedShapes += shapesPatchedInFile;

                    if (!dryRun)
                    {
                        try
                        {
                            var saveResult = nif.Save(outputPath);
                            if (saveResult != 0)
                                log($"  [ERROR] Save failed (result={saveResult}) for {outputPath}");
                            else
                                log($"  [saved] {outputPath}");
                        }
                        catch (Exception ex)
                        {
                            log($"  [ERROR] Exception saving {outputPath}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    if (!dryRun)
                        File.Copy(nifPath, outputPath, overwrite: true);
                    result.CopiedFiles++;
                }
            }

            log(dryRun
                ? $"Dry run complete: would patch {result.PatchedShapes} shape(s) across {result.PatchedFiles} file(s), and copy {result.CopiedFiles} unchanged file(s)."
                : $"Done: patched {result.PatchedShapes} shape(s) across {result.PatchedFiles} file(s), copied {result.CopiedFiles} unchanged file(s).");

            return result;
        }

        // We tried guessing NiflySharp's exact property/enum name for nifxml's "Skyrim Shader Type"
        // field twice and both guesses were wrong for the 2.0.4 package actually in use. Reflection
        // then revealed the real shape: BSLightingShaderProperty exposes several per-game-version
        // backing properties (ShaderType_SK_FO4, ShaderType_FO3_NV, ShaderType_FO76_SF) plus one
        // plain "ShaderType" property (type BSLightingShaderType) that's the version-normalized one
        // meant for direct use - that's the one we want here, not the raw per-version fields.
        static readonly PropertyInfo ShaderTypeProperty = FindShaderTypeProperty();

        static PropertyInfo FindShaderTypeProperty()
        {
            var properties = typeof(BSLightingShaderProperty)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsEnum &&
                            p.Name.Replace("_", "").Contains("ShaderType", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var exact = properties.FirstOrDefault(p => p.Name.Equals("ShaderType", StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            throw new InvalidOperationException(
                $"Couldn't find a plain 'ShaderType' property on BSLightingShaderProperty via reflection " +
                $"(found {properties.Count} enum-typed candidate(s) instead: " +
                $"{string.Join(", ", properties.Select(c => $"{c.PropertyType.Name}.{c.Name}"))}). " +
                "Open BSLightingShaderProperty in your IDE and pick the right one manually - likely whichever " +
                "matches the game version these nif files target.");
        }

        static bool IsEnvironmentMapShader(BSLightingShaderProperty lsp)
        {
            var value = ShaderTypeProperty.GetValue(lsp)?.ToString() ?? string.Empty;
            return string.Equals(value.Replace("_", ""), "EnvironmentMap", StringComparison.OrdinalIgnoreCase);
        }

        // NiflySharp (as of the version in use here) does not generate public setters for
        // Glossiness/SpecularStrength/LightingEffect1/LightingEffect2 on BSLightingShaderProperty -
        // confirmed via reflection: the private backing fields (_glossiness, _specularStrength,
        // _lightingEffect1, _lightingEffect2) exist and are read by INiShader's get-only interface
        // properties, but nothing publicly writes them. Until the library adds real setters (worth
        // filing upstream at github.com/ousnius/NiflySharp/issues), we write those four fields
        // directly via reflection. EnvironmentMapScale IS a normal public settable property, so
        // that one goes through the regular API.
        static readonly Dictionary<string, FieldInfo> ShaderFloatFields = new()
        {
            ["Glossiness"] = GetField("_glossiness"),
            ["SpecularStrength"] = GetField("_specularStrength"),
            ["LightingEffect1"] = GetField("_lightingEffect1"),
            ["LightingEffect2"] = GetField("_lightingEffect2"),
        };

        static FieldInfo GetField(string name) =>
            typeof(BSLightingShaderProperty).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"BSLightingShaderProperty no longer has a private field named '{name}' - " +
                "the NiflySharp version in use may have changed; re-run the reflection dump.");

        static void SetShaderFloat(BSLightingShaderProperty lsp, string name, float value) =>
            ShaderFloatFields[name].SetValue(lsp, value);

        static void ApplySettings(NifFile nif, BSLightingShaderProperty lsp, Settings settings, Action<string> log)
        {
            // --- Shader floats ---
            SetShaderFloat(lsp, "Glossiness", settings.Shader.Glossiness);
            SetShaderFloat(lsp, "SpecularStrength", settings.Shader.SpecularStrength);
            SetShaderFloat(lsp, "LightingEffect1", settings.Shader.LightingEffect1);
            SetShaderFloat(lsp, "LightingEffect2", settings.Shader.LightingEffect2);
            lsp.EnvironmentMapScale = settings.Shader.EnvironmentScale; // real public property, no workaround needed

            // --- Shader flags (replace wholesale from the preset) ---
            lsp.ShaderFlags_SSPF1 = ParseFlags1(settings.Flags1, log);
            lsp.ShaderFlags_SSPF2 = ParseFlags2(settings.Flags2, log);

            // --- Textures: only overwrite slots that are non-empty in the preset ---
            if (lsp.TextureSetRef is not null && !lsp.TextureSetRef.IsEmpty())
            {
                var textureSet = nif.GetBlock<BSShaderTextureSet>(lsp.TextureSetRef);
                if (textureSet is not null)
                {
                    SetSlotIfNotEmpty(textureSet, 0, settings.Textures.Diffuse);
                    SetSlotIfNotEmpty(textureSet, 1, settings.Textures.Normal);
                    SetSlotIfNotEmpty(textureSet, 2, settings.Textures.Opacity);
                    SetSlotIfNotEmpty(textureSet, 3, settings.Textures.Height);
                    SetSlotIfNotEmpty(textureSet, 4, settings.Textures.Metal); // cubemap slot
                    SetSlotIfNotEmpty(textureSet, 5, settings.Textures.AO);
                    SetSlotIfNotEmpty(textureSet, 6, settings.Textures.Emissive);
                    SetSlotIfNotEmpty(textureSet, 7, settings.Textures.Roughness);
                    SetSlotIfNotEmpty(textureSet, 8, settings.Textures.ID);
                    // Transmissive: only present on newer (FO4+) texture sets - add if/when needed.
                }
            }
        }

        static void SetSlotIfNotEmpty(BSShaderTextureSet textureSet, int slot, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            if (textureSet.Textures is not null && slot < textureSet.Textures.Count)
                textureSet.Textures[slot] = new NiString4(value, false);
        }

        static SkyrimShaderPropertyFlags1 ParseFlags1(List<string> names, Action<string> log)
        {
            SkyrimShaderPropertyFlags1 flags = 0;
            foreach (var name in names)
            {
                if (Enum.TryParse<SkyrimShaderPropertyFlags1>(name, ignoreCase: true, out var parsed))
                    flags |= parsed;
                else
                    log($"  [warn] Unknown Flags1 entry: {name}");
            }
            return flags;
        }

        static SkyrimShaderPropertyFlags2 ParseFlags2(List<string> names, Action<string> log)
        {
            SkyrimShaderPropertyFlags2 flags = 0;
            foreach (var name in names)
            {
                if (Enum.TryParse<SkyrimShaderPropertyFlags2>(name, ignoreCase: true, out var parsed))
                    flags |= parsed;
                else
                    log($"  [warn] Unknown Flags2 entry: {name}");
            }
            return flags;
        }
    }
}