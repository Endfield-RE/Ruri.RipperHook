using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class ShaderLabEmitter
{
    private sealed class ContainerOutputEntry
    {
        public required ShaderPrep Prep { get; init; }
        public required DecompileResult Result { get; init; }
        public required string BasePath { get; init; }
        public required string SourceExtension { get; init; }
    }

    public static void Emit(ShaderSourceState state, ShaderMapInfo map)
        => EmitShaderMap(state, map);

    private static void EmitShaderMap(ShaderSourceState state, ShaderMapInfo map)
    {
        List<ContainerOutputEntry> outputs = new(map.Members.Count);
        foreach (ShaderMapMember member in map.Members)
        {
            if (!state.ShaderPrepByIndex.TryGetValue(member.ArchiveShaderIndex, out ShaderPrep? prep)) continue;
            if (!state.DecompileResultByIndex.TryGetValue(member.ArchiveShaderIndex, out DecompileResult? result)) continue;

            ContainerOutputEntry? output = FinalizeForMap(state, map, member, prep, result);
            if (output != null)
            {
                outputs.Add(output);
            }
        }

        if (outputs.Count == 0)
        {
            System.Threading.Interlocked.Increment(ref state.Skipped);
            return;
        }

        WriteShaderMapOutputs(state, map, outputs);
    }

    private static ContainerOutputEntry? FinalizeForMap(
        ShaderSourceState state,
        ShaderMapInfo map,
        ShaderMapMember member,
        ShaderPrep prep,
        DecompileResult? result)
    {
        if (result == null)
        {
            System.Threading.Interlocked.Increment(ref state.Failed);
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}): batch worker returned no result.");
            return null;
        }

        if (!result.Success)
        {
            System.Threading.Interlocked.Increment(ref state.Failed);
            string firstLine = result.ErrorMessage?.Split('\n', 2)[0]?.Trim() ?? "<no message>";
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}) [reached {result.FailedStage}]: {firstLine}");
            return new ContainerOutputEntry
            {
                Prep = prep,
                Result = result,
                BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
                SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
            };
        }

        System.Threading.Interlocked.Increment(ref state.Decompiled);
        return new ContainerOutputEntry
        {
            Prep = prep,
            Result = result,
            BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
            SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
        };
    }

    private static string BuildShaderMapStem(ShaderMapInfo map)
        => SanitizeFileStem($"{HashPrefix(map.ShaderMapHash)}_{map.PrimaryName}");

    /// <summary>
    /// What a shader map's own folder is called wherever it lands: its hash, then the material it
    /// was first named by. The hash prefix is what says WHICH map a folder holds, so an output
    /// folder states for itself which maps it already carries.
    /// </summary>
    public static string HashPrefix(string shaderMapHash)
        => "SM" + (shaderMapHash.Length >= 12 ? shaderMapHash[..12] : shaderMapHash);

    /// <summary>The map hashes one output folder already holds source for.</summary>
    public static HashSet<string> Written(string outputDirectory)
    {
        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outputDirectory))
        {
            return hashes;
        }
        foreach (string file in Directory.EnumerateFiles(outputDirectory, "SM*.shader"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            int underscore = stem.IndexOf('_');
            hashes.Add(underscore > 0 ? stem[..underscore] : stem);
        }
        return hashes;
    }

    private static void WriteShaderMapOutputs(ShaderSourceState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        string containerStem = BuildShaderMapStem(map);
        string containerBasePath = Path.Combine(state.OutputDirectory, containerStem);

        UeShaderLabContainerMetadata metadata = BuildShaderMapMetadata(state, map, outputs);

        HashSet<string> splittableStages = ComputeSplittableStages(metadata.Programs, state.Request.SplitVariantsToHlslFiles);

        Dictionary<UeShaderLabProgramData, string> pooled = new();
        if (splittableStages.Count > 0)
        {
            foreach (UeShaderLabProgramData program in metadata.Programs)
            {
                if (!splittableStages.Contains(program.Stage)) continue;
                string keyword = BuildVariantKeyword(program);
                pooled[program] = state.Variants.Include(keyword, program.SourceFileExtension, WriteVariantHlslFile(metadata, program, keyword));
            }
        }

        state.Writer.Write(containerBasePath + ".shader", WriteContainerShaderFile(metadata, pooled, splittableStages));
    }

    /// <summary>
    /// Which stages are written as their own .hlsl files: the ones holding more than one program.
    /// A stage with a single body reads better where it stands than behind an #include.
    /// </summary>
    private static HashSet<string> ComputeSplittableStages(List<UeShaderLabProgramData> programs, bool splitEnabled)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (!splitEnabled)
        {
            return result;
        }
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (UeShaderLabProgramData program in programs)
        {
            counts[program.Stage] = counts.GetValueOrDefault(program.Stage) + 1;
        }
        foreach (KeyValuePair<string, int> stage in counts)
        {
            if (stage.Value > 1)
            {
                result.Add(stage.Key);
            }
        }
        return result;
    }

    private static string WriteVariantHlslFile(UeShaderLabContainerMetadata metadata, UeShaderLabProgramData program, string keyword)
    {
        StringBuilder sb = new();
        sb.AppendLine("// =============================================================");
        sb.AppendLine($"// Variant: {keyword}");
        sb.AppendLine($"// Stage: {program.Stage}");
        sb.AppendLine($"// ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"// PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"// ShaderHash: {program.ShaderHash}");
        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName)) sb.AppendLine($"// ShaderType: {program.ShaderTypeName}");
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName)) sb.AppendLine($"// VertexFactoryType: {program.VertexFactoryTypeName}");
        if (!string.IsNullOrWhiteSpace(program.PipelineTypeName)) sb.AppendLine($"// PipelineType: {program.PipelineTypeName}");
        sb.AppendLine("// =============================================================");
        sb.AppendLine();

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            foreach (string line in SplitLines(program.SourceCode!))
            {
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        sb.AppendLine("// Decompile failed.");
        if (!string.IsNullOrWhiteSpace(program.ErrorMessage))
        {
            foreach (string line in SplitLines(program.ErrorMessage!))
            {
                sb.Append("// ");
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    private static UeShaderLabContainerMetadata BuildShaderMapMetadata(ShaderSourceState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        return new UeShaderLabContainerMetadata
        {
            Name = map.PrimaryName,
            ContainerKey = $"SM{(map.ShaderMapHash.Length >= 12 ? map.ShaderMapHash[..12] : map.ShaderMapHash)}",
            MaterialName = map.PrimaryName,
            UsedMaterials = new List<string>(map.Assets),
            PropertiesBlock = map.PropertiesBlock,
            SubShaderTags = map.SubShaderTags,
            PassCommands = map.PassCommands,
            Programs = outputs
                .OrderBy(static o => StageSortKey(StageName(o.Result.Stage)))
                .ThenBy(static o => o.Prep.ShaderIndex)
                .Select(output =>
                {
                    ShaderContainerInfo? perMap = ResolvePerMapContainer(state, map, output.Prep.ShaderIndex);
                    ShaderContainerInfo? container = perMap ?? output.Prep.ContainerInfo;
                    string? source = output.Result.SourceCode;
                    if (output.Result.Success && !string.IsNullOrWhiteSpace(source))
                    {
                        source = RenameAnonymousGlobals(source, container?.ShaderTypeName ?? string.Empty, out int collisions);
                        System.Threading.Interlocked.Add(ref state.NameCollisions, collisions);
                    }
                    return new UeShaderLabProgramData
                    {
                        Stage = StageName(output.Result.Stage),
                        ShaderIndex = output.Prep.ShaderIndex,
                        ResourceIndex = container?.ResourceIndex ?? -1,
                        PermutationId = container?.PermutationId ?? -1,
                        PipelineTypeHash = container?.PipelineTypeHash ?? string.Empty,
                        PipelineTypeName = container?.PipelineTypeName ?? string.Empty,
                        ShaderTypeHash = container?.ShaderTypeHash ?? string.Empty,
                        ShaderTypeName = container?.ShaderTypeName ?? string.Empty,
                        VertexFactoryTypeHash = container?.VertexFactoryTypeHash ?? string.Empty,
                        VertexFactoryTypeName = container?.VertexFactoryTypeName ?? string.Empty,
                        ShaderMapHash = map.ShaderMapHash,
                        ShaderHash = container?.ShaderHash ?? string.Empty,
                        SourceLanguage = output.Result.SourceLanguage,
                        SourceFileExtension = output.Result.SourceFileExtension,
                        Success = output.Result.Success,
                        SourceCode = source,
                        ErrorMessage = output.Result.ErrorMessage,
                        SymbolMetadata = output.Result.FinalSymbols,
                    };
                })
                .ToList()
        };
    }

    private static ShaderContainerInfo? ResolvePerMapContainer(ShaderSourceState state, ShaderMapInfo map, int archiveShaderIndex)
    {
        return map.ContainerByShaderIndex.GetValueOrDefault(archiveShaderIndex);
    }

    private static string WriteContainerShaderFile(UeShaderLabContainerMetadata metadata, IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, HashSet<string> splittableStages)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Shader \"{metadata.Name}\" {{");
        sb.AppendLine($"    // UE ContainerKey: {metadata.ContainerKey}");
        sb.AppendLine($"    // Material: {metadata.MaterialName}");
        if (metadata.UsedMaterials.Count > 0)
        {
            sb.AppendLine("    // UsedMaterials:");
            foreach (string material in metadata.UsedMaterials)
            {
                sb.AppendLine($"    //   {material}");
            }
        }
        if (!string.IsNullOrEmpty(metadata.PropertiesBlock))
        {
            foreach (string line in metadata.PropertiesBlock.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) sb.AppendLine();
                else sb.AppendLine("    " + trimmed);
            }
        }
        sb.AppendLine("    SubShader {");
        if (!string.IsNullOrEmpty(metadata.SubShaderTags))
        {
            foreach (string line in metadata.SubShaderTags.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) sb.AppendLine();
                else sb.AppendLine("        " + trimmed);
            }
        }
        if (metadata.Programs.Count > 0)
        {
            List<UeShaderLabProgramData> passPrograms = metadata.Programs
                .OrderBy(static p => StageSortKey(p.Stage))
                .ThenBy(static p => p.ShaderIndex)
                .ToList();
            sb.AppendLine("        Pass {");
            if (!string.IsNullOrWhiteSpace(metadata.ContainerKey)) sb.AppendLine($"            // ContainerKey: {metadata.ContainerKey}");
            if (!string.IsNullOrEmpty(metadata.PassCommands))
            {
                foreach (string line in metadata.PassCommands.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine("            " + trimmed);
                }
            }
            foreach (string typeName in passPrograms.Select(p => p.ShaderTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // ShaderType: {typeName}");
            }
            foreach (string vfName in passPrograms.Select(p => p.VertexFactoryTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // VertexFactoryType: {vfName}");
            }
            foreach (string pipeline in passPrograms.Select(p => p.PipelineTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // PipelineType: {pipeline}");
            }
            if (!string.IsNullOrWhiteSpace(passPrograms[0].ShaderMapHash)) sb.AppendLine($"            // ShaderMapHash: {passPrograms[0].ShaderMapHash}");

            bool allGlsl = passPrograms.All(static p => IsGlsl(p));
            sb.AppendLine(allGlsl ? "            GLSLPROGRAM" : "            HLSLPROGRAM");

            if (!allGlsl)
            {
                sb.AppendLine("            #pragma target 5.0");
                sb.AppendLine("            #pragma use_dxc");
            }

            foreach (string pragma in passPrograms
                         .Select(p => TryGetStagePragma(p.Stage, out string pr) ? pr : string.Empty)
                         .Where(static p => !string.IsNullOrEmpty(p))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(static p => p, StringComparer.Ordinal))
            {
                sb.AppendLine($"            {pragma} main");
            }


            sb.AppendLine();

            foreach (IGrouping<string, UeShaderLabProgramData> stageGroup in passPrograms
                         .GroupBy(static p => p.Stage, StringComparer.Ordinal)
                         .OrderBy(static g => StageSortKey(g.Key)))
            {
                List<UeShaderLabProgramData> stagePrograms = stageGroup
                    .OrderBy(static p => p.PermutationId)
                    .ThenBy(static p => p.ShaderIndex)
                    .ToList();

                sb.AppendLine($"            // ============================================================");
                sb.AppendLine($"            // Stage: {stageGroup.Key} — {stagePrograms.Count} variant(s)");
                sb.AppendLine($"            // ============================================================");

                string? stageMacro = GetShaderStageMacro(stageGroup.Key);
                if (stageMacro != null)
                {
                    sb.AppendLine($"            #ifdef {stageMacro}");
                }

                EmitStageVariants(sb, stagePrograms, pooled,
                    splitInclude: splittableStages.Contains(stageGroup.Key), allGlsl);

                if (stageMacro != null)
                {
                    sb.AppendLine($"            #endif");
                }
                sb.AppendLine();
            }
            sb.AppendLine(allGlsl ? "            ENDGLSL" : "            ENDHLSL");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool TryGetStagePragma(string stage, out string pragma)
    {
        pragma = stage switch
        {
            "Vertex" => "#pragma vertex",
            "Fragment" => "#pragma fragment",
            "Geometry" => "#pragma geometry",
            "Hull" => "#pragma hull",
            "Domain" => "#pragma domain",
            "Compute" => "#pragma kernel",
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(pragma);
    }

    private static string BuildPassGroupKey(UeShaderLabProgramData program)
    {
        string pipeline = string.IsNullOrWhiteSpace(program.PipelineTypeHash) ? "NOPIPE" : program.PipelineTypeHash;
        string vf = string.IsNullOrWhiteSpace(program.VertexFactoryTypeHash) ? "NOVF" : program.VertexFactoryTypeHash;
        string type = string.IsNullOrWhiteSpace(program.ShaderTypeHash) ? "NOTYPE" : program.ShaderTypeHash;
        return $"P{pipeline}_V{vf}_S{type}";
    }

    /// <summary>Prefix of the keyword that selects one compiled variant of a stage.</summary>
    private const string VariantKeywordPrefix = "RURI_VARIANT_";

    /// <summary>What selects one variant: its own name, which is also the name of its file.</summary>
    private static string VariantSelectKeyword(UeShaderLabProgramData program)
        => VariantKeywordPrefix + BuildVariantKeyword(program);

    /// <summary>
    /// Every program this stage compiled to, each reachable by a keyword of its own.
    ///
    /// A shader map holds one program per permutation the material was compiled for, and they are
    /// ALTERNATIVES -- exactly one is what a given draw runs. When each is written as its own
    /// file, naming only the first left every other file unreferenced: on one character material
    /// that was 69 of 72 written files that nothing in the shader pointed at, which is not a
    /// translation of anything. Each is guarded by its own keyword instead, and the first is what
    /// a reader who names none gets, so the shader still says something on its own.
    ///
    /// The keywords are deliberately NOT declared as a multi_compile set: which vertex program ran
    /// with which pixel program is a pairing the archive does not record, and declaring a set
    /// would state one.
    ///
    /// Variants only exist to be selected between when they were written separately. A stage kept
    /// inline states the one program it was asked for and says so, which is what asking for it
    /// inline means.
    /// </summary>
    private static void EmitStageVariants(StringBuilder sb, List<UeShaderLabProgramData> stagePrograms,
        IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, bool splitInclude, bool blockIsGlsl)
    {
        if (stagePrograms.Count == 1)
        {
            EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, blockIsGlsl);
            return;
        }

        if (!splitInclude)
        {
            sb.AppendLine($"            // Note: {stagePrograms.Count - 1} further variant(s) of this stage were not emitted."
                          + " Ask for split variants to get each as its own file.");
            EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, blockIsGlsl);
            return;
        }

        sb.AppendLine($"            // Define one of these to pick a variant; none defined compiles the first.");
        for (int i = 0; i < stagePrograms.Count; i++)
        {
            sb.AppendLine($"            #{(i == 0 ? "if" : "elif")} defined({VariantSelectKeyword(stagePrograms[i])})");
            EmitProgramBlock(sb, stagePrograms[i], pooled, splitInclude, blockIsGlsl);
        }
        sb.AppendLine("            #else");
        EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, blockIsGlsl);
        sb.AppendLine("            #endif");
    }

    /// <summary>
    /// Whether a program's source is GLSL. The block a pass is written in is GLSL only when every
    /// program of it is: a map can hold both -- tessellation stages have no HLSL spelling in the
    /// backend while the vertex and pixel stages beside them do -- and ShaderLab has one block
    /// per pass, so a program whose language is not the block's says so where it stands.
    /// </summary>
    private static bool IsGlsl(UeShaderLabProgramData program)
        => string.Equals(program.SourceLanguage, "glsl", StringComparison.OrdinalIgnoreCase);

    private static void EmitProgramBlock(StringBuilder sb, UeShaderLabProgramData program, IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, bool splitInclude, bool blockIsGlsl)
    {
        if (IsGlsl(program) != blockIsGlsl)
        {
            sb.AppendLine($"            // Language: {program.SourceLanguage.ToUpperInvariant()}");
        }
        if (splitInclude)
        {
            sb.AppendLine($"            #include \"{pooled[program]}\"");
            return;
        }

        sb.AppendLine($"            // Stage: {program.Stage}");
        sb.AppendLine($"            // ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"            // ResourceIndex: {program.ResourceIndex}");
        sb.AppendLine($"            // PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"            // ShaderHash: {program.ShaderHash}");

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            string adapted = AdaptHlslForUnity(program.SourceCode!);
            foreach (string line in SplitLines(adapted))
            {
                sb.Append("            ");
                sb.AppendLine(line);
            }
            return;
        }

        sb.AppendLine("            // Decompile failed.");
        if (!string.IsNullOrWhiteSpace(program.ErrorMessage))
        {
            foreach (string line in SplitLines(program.ErrorMessage!))
            {
                sb.Append("            // ");
                sb.AppendLine(line);
            }
        }
    }

    private static string? GetShaderStageMacro(string stage) => stage switch
    {
        "Vertex" => "SHADER_STAGE_VERTEX",
        "Fragment" => "SHADER_STAGE_FRAGMENT",
        "Geometry" => "SHADER_STAGE_GEOMETRY",
        "Hull" => "SHADER_STAGE_HULL",
        "Domain" => "SHADER_STAGE_DOMAIN",
        "RayTracing" => "SHADER_STAGE_RAY_TRACING",
        _ => null,
    };

    private static readonly System.Text.RegularExpressions.Regex MaterialSamplerDeclRegex =
        new(@"\bMaterial_(?<n>[A-Za-z0-9_]+)Sampler\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MaterialTextureDeclRegex =
        new(@"(?<t>Texture(?:2D|2DArray|Cube|CubeArray|3D)(?:<[^>]+>)?)\s+Material_(?<n>[A-Za-z0-9_]+)\s*:\s*register",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressDeclRegex =
        new(@"^\s*ByteAddressBuffer\s+T(?<n>\d+)_\d+\s*:\s*register\(t\k<n>[^\)]*\);\s*\r?\n",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressRefRegex =
        new(@"\bT(\d+)_\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SamplerStateDeclRegex =
        new(@"\bSamplerState\s+(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*register", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string AdaptHlslForUnity(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;

        body = MaterialSamplerDeclRegex.Replace(body, "sampler_${n}");

        HashSet<string> renamedTextures = new(StringComparer.Ordinal);
        body = MaterialTextureDeclRegex.Replace(body, m =>
        {
            renamedTextures.Add(m.Groups["n"].Value);
            return $"{m.Groups["t"].Value} _{m.Groups["n"].Value} : register";
        });
        foreach (string name in renamedTextures)
        {
            string from = "Material_" + name;
            string to = "_" + name;
            body = System.Text.RegularExpressions.Regex.Replace(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(from)}\b", to);
        }

        HashSet<string> renamedSamplers = new(StringComparer.Ordinal);
        body = SamplerStateDeclRegex.Replace(body, m =>
        {
            string name = m.Groups["n"].Value;
            if (name.StartsWith("sampler_", StringComparison.Ordinal) || ContainsInlineSamplerMode(name))
            {
                return m.Value;
            }
            renamedSamplers.Add(name);
            return $"SamplerState sampler{name}_LinearClamp : register";
        });
        foreach (string name in renamedSamplers)
        {
            string to = $"sampler{name}_LinearClamp";
            body = System.Text.RegularExpressions.Regex.Replace(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b(?!\s*:\s*register)", to);
        }

        body = AliasedByteAddressDeclRegex.Replace(body, string.Empty);
        body = AliasedByteAddressRefRegex.Replace(body, "T$1");

        return body;
    }

    private static bool ContainsInlineSamplerMode(string name)
    {
        bool hasFilter = name.Contains("Point", StringComparison.Ordinal)
                         || name.Contains("Linear", StringComparison.Ordinal)
                         || name.Contains("Trilinear", StringComparison.Ordinal);
        bool hasWrap = name.Contains("Clamp", StringComparison.Ordinal)
                       || name.Contains("Repeat", StringComparison.Ordinal)
                       || name.Contains("Mirror", StringComparison.Ordinal);
        return hasFilter && hasWrap;
    }

    private static List<string> BuildPassPermutationKeywords(List<UeShaderLabProgramData> programs)
    {
        return programs
            .Where(static p => p.PermutationId >= 0)
            .Select(static p => BuildPermutationKeyword(p.PermutationId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildPermutationKeyword(int permutationId) => $"PERM_{permutationId}";

    private static string BuildVariantKeyword(UeShaderLabProgramData program)
    {
        StringBuilder sb = new();
        sb.Append(string.IsNullOrWhiteSpace(program.Stage) ? "VARIANT" : program.Stage);

        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName))
        {
            sb.Append('_').Append(CompressTemplateIdent(program.ShaderTypeName));
        }
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName))
        {
            sb.Append('_').Append(CompressTemplateIdent(program.VertexFactoryTypeName));
        }
        if (program.PermutationId >= 0)
        {
            sb.Append("_PERM").Append(program.PermutationId);
        }

        if (!string.IsNullOrWhiteSpace(program.ShaderHash))
        {
            string shortHash = program.ShaderHash.Length >= 8 ? program.ShaderHash[..8] : program.ShaderHash;
            sb.Append('_').Append(shortHash);
        }
        else
        {
            sb.Append("_IDX").Append(program.ShaderIndex.ToString("D6"));
        }

        return sb.ToString();
    }

    private static string CompressTemplateIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int lt = raw.IndexOf('<');
        if (lt < 0) return SanitizeIdent(raw);
        string head = raw.Substring(0, lt);
        int depth = 0;
        int firstArgEnd = raw.Length;
        for (int i = lt; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 1) { firstArgEnd = i; break; }
        }
        string firstArg = (firstArgEnd > lt + 1) ? raw.Substring(lt + 1, firstArgEnd - lt - 1).Trim() : string.Empty;
        return SanitizeIdent(string.IsNullOrEmpty(firstArg) ? head : (head + "_" + firstArg));
    }


    /// <summary>
    /// A program's source with every binding no symbol named spelled after the shader it belongs to. A binding a
    /// uniform buffer carries is named upstream, from the shader's own resource table; one that reaches here still
    /// anonymous has no name in any source, so it keeps the one thing that is true of it -- which shader, which
    /// register -- rather than a name guessed from its type or from how it is used. The loose-parameter buffer is
    /// spelled after its shader type the same way. Two bindings that would come out under one name are left as
    /// the decompiler wrote them, and counted.
    /// </summary>
    private static string RenameAnonymousGlobals(string source, string shaderTypeName, out int collisions)
    {
        collisions = 0;
        string discriminator = string.IsNullOrWhiteSpace(shaderTypeName) ? string.Empty : SanitizeIdent(shaderTypeName);
        string result = source;
        if (discriminator.Length > 0)
        {
            result = result.Replace("_Globals_m0", $"_loose_{discriminator}", StringComparison.Ordinal);
            Dictionary<string, string> identToFinal = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in AnonymousBindingRegex.Matches(result))
            {
                string ident = m.Groups["ident"].Value;
                string suffix = ident.StartsWith('_')
                    ? $"{m.Groups["prefix"].Value.ToUpperInvariant()}{m.Groups["slot"].Value}"
                    : ident;
                identToFinal.TryAdd(ident, $"{discriminator}_{suffix}");
            }
            foreach (IGrouping<string, string> claimed in identToFinal
                         .GroupBy(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal)
                         .Where(static group => group.Count() > 1)
                         .ToList())
            {
                foreach (string ident in claimed)
                {
                    identToFinal.Remove(ident);
                    collisions++;
                }
            }
            foreach (KeyValuePair<string, string> kv in identToFinal)
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + @"\b",
                    kv.Value);
            }
        }

        if (result.Contains("_m0", StringComparison.Ordinal))
        {
            HashSet<string> cbufferNames = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^cbuffer\s+type_([A-Za-z_][A-Za-z0-9_]*)\s*:",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                cbufferNames.Add(m.Groups[1].Value);
            }
            foreach (string cb in cbufferNames)
            {
                string token = $"{cb}_m0";
                if (!result.Contains(token, StringComparison.Ordinal)) continue;
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
                    $"{cb}_loose");
            }
        }

        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex AnonymousBindingRegex = new(
        @"^[A-Za-z][A-Za-z0-9_]*(?:<[^>]+>)?\s+(?<ident>[TU]\d+|_\d+)\s*:\s*register\((?<prefix>[tusb])(?<slot>\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    private static string SanitizeIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
        }
        StringBuilder collapsed = new(sb.Length);
        bool prevUnderscore = false;
        foreach (char c in sb.ToString())
        {
            if (c == '_')
            {
                if (!prevUnderscore) collapsed.Append('_');
                prevUnderscore = true;
            }
            else
            {
                collapsed.Append(c);
                prevUnderscore = false;
            }
        }
        return collapsed.ToString().Trim('_');
    }

    private static int StageSortKey(string stage) => stage switch
    {
        "Vertex" => 0,
        "Amplification" => 1,
        "Mesh" => 2,
        "Hull" => 3,
        "Domain" => 4,
        "Geometry" => 5,
        "Fragment" => 6,
        "Compute" => 7,
        "RayGeneration" => 8,
        "Intersection" => 9,
        "AnyHit" => 10,
        "ClosestHit" => 11,
        "Miss" => 12,
        "Callable" => 13,
        _ => 100,
    };

    /// <summary>
    /// What to call the stage a shader runs at, as the BINARY declared it.
    ///
    /// Never from the container's frequency byte: that byte is numbered by the engine
    /// BUILD, and a fork that inserts a frequency shifts every later one. One shipped
    /// title numbers pixel 5 and geometry 6 where stock 4.26 numbers them 3 and 4, so
    /// every pixel shader it ships was written out as a compute shader and every map
    /// read as having no pixel shader at all -- silently, because a mislabelled file
    /// still decompiles.
    /// </summary>
    private static string StageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Vertex => "Vertex",
        PipelineStage.TessControl => "Hull",
        PipelineStage.TessEvaluation => "Domain",
        PipelineStage.Geometry => "Geometry",
        PipelineStage.Fragment => "Fragment",
        PipelineStage.Compute => "Compute",
        PipelineStage.RayGeneration => "RayGeneration",
        PipelineStage.Intersection => "Intersection",
        PipelineStage.AnyHit => "AnyHit",
        PipelineStage.ClosestHit => "ClosestHit",
        PipelineStage.Miss => "Miss",
        PipelineStage.Callable => "Callable",
        PipelineStage.Task => "Amplification",
        PipelineStage.Mesh => "Mesh",
        _ => "Unknown",
    };

    private static IEnumerable<string> SplitLines(string text)
    {
        using StringReader reader = new(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static string SanitizeFileStem(string value)
    {
        return string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class UeShaderLabContainerMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string ContainerKey { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public List<string> UsedMaterials { get; set; } = new();
        public List<UeShaderLabProgramData> Programs { get; set; } = new();
        public string PropertiesBlock { get; set; } = string.Empty;
        public string SubShaderTags { get; set; } = string.Empty;
        public string PassCommands { get; set; } = string.Empty;
    }

    private sealed class UeShaderLabProgramData
    {
        public string Stage { get; set; } = string.Empty;
        public int ShaderIndex { get; set; }
        public int ResourceIndex { get; set; }
        public int PermutationId { get; set; }
        public string PipelineTypeHash { get; set; } = string.Empty;
        public string PipelineTypeName { get; set; } = string.Empty;
        public string ShaderTypeHash { get; set; } = string.Empty;
        public string ShaderTypeName { get; set; } = string.Empty;
        public string VertexFactoryTypeHash { get; set; } = string.Empty;
        public string VertexFactoryTypeName { get; set; } = string.Empty;
        public string ShaderMapHash { get; set; } = string.Empty;
        public string ShaderHash { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string SourceLanguage { get; set; } = "hlsl";
        public string SourceFileExtension { get; set; } = ".hlsl";
        public string? SourceCode { get; set; }
        public string? ErrorMessage { get; set; }
        public SerializedProgramData? SymbolMetadata { get; set; }
    }
}
