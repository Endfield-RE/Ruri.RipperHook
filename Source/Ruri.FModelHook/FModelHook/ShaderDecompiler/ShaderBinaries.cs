using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CUE4Parse.UE4.Assets.Exports.Material;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class ShaderBinaries
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_seedHitsByClass = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unknownShaderTypeHashes = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unmatchedClassNames = new(StringComparer.Ordinal);

    private static void ReconcileMaterialTextureBindings(ShaderSourceState state, int shaderIndex, FShaderParameterMapInfo? pmi, SerializedProgramData metadata)
    {
        bool hasPmi = pmi is not null;
        if (s_textureBindDiagLogged.Count < 12 && s_textureBindDiagLogged.TryAdd(shaderIndex.ToString(), true))
        {
            string props = hasPmi
                ? $"UniformBuffers[{pmi!.UniformBuffers?.Length ?? -1}],TextureSamplers[{pmi.TextureSamplers?.Length ?? -1}],SRVs[{pmi.SRVs?.Length ?? -1}],LooseParameterBuffers[{pmi.LooseParameterBuffers?.Length ?? -1}]"
                : "(none)";
            state.Log($"    [texbind-diag] shader={shaderIndex} uesTextures={metadata.TextureParameters.Count} pmi={hasPmi} props={props}");
        }
        if (metadata.TextureParameters.Count == 0) return;
        if (!hasPmi) return;

        var slots = new List<int>();
        foreach (FShaderParameterInfo[]? bindings in new[] { pmi!.TextureSamplers, pmi.SRVs })
        {
            foreach (FShaderParameterInfo entry in bindings ?? [])
            {
                if (entry is FShaderResourceParameterInfo { Type: EShaderParameterType.Sampler or EShaderParameterType.BindlessSampler }) continue;
                int slot = entry.BaseIndex;
                if (!slots.Contains(slot)) slots.Add(slot);
            }
        }
        if (slots.Count == 0) return;
        slots.Sort();

        if (slots.Count != metadata.TextureParameters.Count)
        {
            if (s_textureBindMismatchLogged.TryAdd(metadata.DebugName ?? shaderIndex.ToString(), true))
            {
                state.Log($"    [texbind] {metadata.DebugName}: UES 贴图 {metadata.TextureParameters.Count} 个 vs cook 资源槽 {slots.Count} 个 — 数量不等,保持匿名(拒绝按位错标)。");
            }
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            metadata.TextureParameters[i].Index = slots[i];
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_textureBindMismatchLogged = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_textureBindDiagLogged = new();

    private static ConstantBufferParameter? TryReconcileGlobalsCB(EngineUbMetadata seed, FShaderParameterMapInfo parameterMapInfo)
    {
        if (parameterMapInfo.LooseParameterBuffers is not { Length: > 0 } loose)
        {
            return null;
        }

        FShaderLooseParameterBufferInfo first = loose[0];
        if (first.Parameters is not { } parameters)
        {
            return null;
        }

        int seedCount = seed.ConstantBuffer!.VectorParameters.Length;
        int cookCount = parameters.Length;
        int pairCount = Math.Min(seedCount, cookCount);
        if (pairCount == 0) return null;

        VectorParameter[] reconciled = new VectorParameter[cookCount];
        int i = 0;
        foreach (FShaderLooseParameterInfo p in parameters)
        {
            int baseIdx = p.BaseIndex;
            int sizeBytes = p.Size;
            if (baseIdx < 0 || sizeBytes <= 0) return null;

            int rowCount = Math.Clamp(sizeBytes / 4, 1, 4);
            if (i < pairCount)
            {
                VectorParameter src = seed.ConstantBuffer.VectorParameters[i];
                reconciled[i] = new VectorParameter
                {
                    Name = src.Name,
                    NameIndex = -1,
                    Type = src.Type,
                    Index = baseIdx,
                    ArraySize = src.ArraySize,
                    IsMatrix = false,
                    RowCount = (byte)rowCount,
                    ColumnCount = 1,
                };
            }
            else
            {
                reconciled[i] = new VectorParameter
                {
                    Name = $"_loose_at_c{baseIdx / 16}",
                    NameIndex = -1,
                    Type = ShaderParamType.Float,
                    Index = baseIdx,
                    ArraySize = 0,
                    IsMatrix = false,
                    RowCount = (byte)rowCount,
                    ColumnCount = 1,
                };
            }
            i++;
        }

        int totalSize = first.Size > 0 ? first.Size : seed.ConstantBuffer.Size;
        return new ConstantBufferParameter
        {
            Name = "$Globals",
            NameIndex = -1,
            VectorParameters = reconciled,
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = totalSize,
            IsPartialCB = false,
        };
    }

    /// <summary>How often a long archive states how far its stream has got.</summary>
    private const int ProgressEveryMaps = 2048;

    /// <summary>
    /// Every shader the archive's maps name, prepared in the order the maps name them and
    /// handed on the moment it is ready, so the decompiler starts on the first while the rest
    /// are still being read.
    ///
    /// A map's facts are read from its first namer when its turn comes -- a unit of namers at a
    /// time, let go before the next -- and a shader is prepared with the facts of the first map
    /// that prepares it. One that a map cannot prepare is tried again at the next map naming it,
    /// and only when the last map naming it has failed too is it told to the schedule as settled
    /// without a result.
    /// </summary>
    public static IEnumerable<PreparedShader> Prepare(ShaderSourceState state, ShaderEmissionSchedule schedule)
    {
        s_seedHitsByClass.Clear();
        s_unknownShaderTypeHashes.Clear();
        s_unmatchedClassNames.Clear();
        s_textureBindMismatchLogged.Clear();
        s_textureBindDiagLogged.Clear();

        Directory.CreateDirectory(state.OutputDirectory);

        ShaderLibrary lib = state.Library;
        HashSet<int> prepared = new();
        List<int> missed = new();
        int wanted = 0;
        int withProperties = 0;
        int withRenderState = 0;
        Stopwatch clock = Stopwatch.StartNew();
        List<ShaderMapInfo> maps = state.ShaderMaps;
        int map = 0;
        int units = 0;
        while (map < maps.Count)
        {
            units++;
            using (DataUnit unit = DataUnit.Begin())
            {
                do
                {
                    ShaderMapInfo info = maps[map];
                    ShaderMapFacts.Reading reading = ShaderMapFacts.Read(state, info);
                    withProperties += reading.Properties ? 1 : 0;
                    withRenderState += reading.RenderState ? 1 : 0;
                    schedule.FactsRead(map);
                    missed.Clear();
                    foreach (ShaderMapMember member in info.Members)
                    {
                        int i = member.ArchiveShaderIndex;
                        if (prepared.Contains(i)) continue;
                        wanted++;
                        PreparedShader? shader = TryPrepare(state, lib, info, i);
                        if (shader is null)
                        {
                            missed.Add(i);
                            continue;
                        }
                        prepared.Add(i);
                        state.ShaderPrepByIndex[i] = shader.Value.Prep;
                        yield return shader.Value;
                    }
                    foreach (int i in missed)
                    {
                        if (!prepared.Contains(i))
                        {
                            schedule.NotPrepared(i, map);
                        }
                    }
                    if (maps.Count > ProgressEveryMaps && (map + 1) % ProgressEveryMaps == 0)
                    {
                        state.Log($"[ShaderSource] {state.ArchiveName}: read {map + 1}/{maps.Count} map(s), {schedule.Written} written, "
                            + $"{prepared.Count} shader(s) prepared, {clock.Elapsed.TotalSeconds:F0} s.");
                    }
                    map++;
                }
                while (map < maps.Count && (unit.Held < ShaderMapIndex.BytesPerUnit || ReadFromSameNamer(maps[map - 1], maps[map])));
            }
        }

        state.Log($"    Properties: populated {withProperties}/{maps.Count} shader-maps.");
        state.Log($"    RenderState: populated {withRenderState}/{maps.Count} shader-maps.");
        state.Log($"    PrepareShaderBinaries: prepped {prepared.Count}/{wanted} binaries over {units} unit(s).");

        if (state.Metadata.ShaderTypes.HashToNameCount > 0)
        {
            int unknown = s_unknownShaderTypeHashes.Count;
            int unmatched = s_unmatchedClassNames.Count;
            int matched = s_seedHitsByClass.Count;
            state.Log($"    ShaderType seed coverage: matched-classes={matched} unmatched-class-with-name={unmatched} unknown-hashes={unknown}");
            int limit = 5;
            foreach (string h in s_unknownShaderTypeHashes.Keys)
            {
                if (limit-- <= 0) break;
                state.Log($"      unknown-hash={h} (generator's IMPLEMENT_*_SHADER_TYPE scan missed this class)");
            }
            int unmatchedLimit = 50;
            foreach (string n in s_unmatchedClassNames.Keys)
            {
                if (unmatchedLimit-- <= 0) { state.Log($"      ... ({unmatched - 50} more unmatched-class-with-name not shown)"); break; }
                state.Log($"      unmatched-with-name={n}");
            }
        }
    }

    /// <summary>
    /// Whether the next map's facts are read from the package the last map's were. A unit is
    /// never ended between two such maps: a level that is the first namer of a run of maps -- a
    /// landscape material instance each -- can hold more than a whole unit by itself, and ending
    /// the unit after each of them read that level again, all of it, for every map it names.
    /// </summary>
    private static bool ReadFromSameNamer(ShaderMapInfo read, ShaderMapInfo next)
        => read.Source is { } done
           && next.Source is { } coming
           && string.Equals(done.Named, coming.Named, StringComparison.OrdinalIgnoreCase);

    /// <summary>One shader prepared, or null when its code could not be read or its preparation threw.</summary>
    private static PreparedShader? TryPrepare(ShaderSourceState state, ShaderLibrary lib, ShaderMapInfo map, int shaderIndex)
    {
        byte[]? raw = lib.GetShaderCode(shaderIndex);
        if (raw == null)
        {
            Interlocked.Increment(ref state.Skipped);
            return null;
        }
        try
        {
            return PrepareSingleShader(state, map, shaderIndex, raw);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref state.Failed);
            state.LogError($"Shader {shaderIndex}: prep exception: {ex.Message}");
            return null;
        }
    }

    private static PreparedShader PrepareSingleShader(ShaderSourceState state, ShaderMapInfo map, int shaderIndex, byte[] raw)
    {
        ShaderContainerInfo? container = map.ContainerByShaderIndex.GetValueOrDefault(shaderIndex);
        FShaderParameterMapInfo? parameterMap = map.ParameterMapByShaderIndex.GetValueOrDefault(shaderIndex);
        MaterialSymbolSource? symbols = map.Symbols;
        ShaderTypeSeedRegistry shaderTypes = state.Metadata.ShaderTypes;

        byte[] strippedCode = UnrealShaderParser.Parse(raw, out ShaderBinaryFormat detectedFormat, out UnrealShaderParser.UnrealMetadata? unrealMetadata);

        MaterialSymbolSource? bestSource = symbols is null ? null : symbols with
        {
            Metadata = Clone(symbols.Metadata),
        };

        SerializedProgramData metadata = SubProgramMetadataReader.Read(unrealMetadata, bestSource, state.Metadata.UniformBuffers, state.Log);

        if (container != null
            && !string.IsNullOrWhiteSpace(container.ShaderTypeHash)
            && shaderTypes.HashToNameCount > 0)
        {
            string? resolvedName = shaderTypes.ResolveTypeName(container.ShaderTypeHash);
            if (resolvedName == null)
            {
                s_unknownShaderTypeHashes.TryAdd(container.ShaderTypeHash, true);
            }
            else
            {
                if (shaderTypes.TryLookupWithFallback(
                        container.ShaderTypeHash, container.ShaderTypeName,
                        out EngineUbMetadata _, out string _))
                {
                }
                else
                {
                    s_unmatchedClassNames.TryAdd(resolvedName, true);
                }
            }
        }

        if (container != null
            && !string.IsNullOrWhiteSpace(container.ShaderTypeHash)
            && shaderTypes.FileCount > 0
            && shaderTypes.TryLookupWithFallback(
                container.ShaderTypeHash, container.ShaderTypeName,
                out EngineUbMetadata typeSeed, out string matchKind))
        {
            string key = $"{container.ShaderTypeName}=>{typeSeed.Name}";
            if (s_seedHitsByClass.TryAdd(key, true))
            {
                int loose = typeSeed.ConstantBuffer?.VectorParameters?.Length ?? 0;
                int tex = (typeSeed.Textures?.Count ?? 0) + (typeSeed.Samplers?.Count ?? 0);
                int buf = (typeSeed.Buffers?.Count ?? 0) + (typeSeed.UAVs?.Count ?? 0);
                state.Log($"[ShaderTypeSeed-hit] cookName={container.ShaderTypeName} via={matchKind} seedClass={typeSeed.Name} loose-params={loose} resources={tex + buf}");
            }

            if (typeSeed.ConstantBuffer != null
                && typeSeed.ConstantBuffer.VectorParameters != null
                && typeSeed.ConstantBuffer.VectorParameters.Length > 0
                && parameterMap is not null)
            {
                ConstantBufferParameter? globalsCb = TryReconcileGlobalsCB(typeSeed, parameterMap);
                if (globalsCb != null)
                {
                    metadata.ConstantBufferParameters.Add(globalsCb);
                }
            }
        }

        ReconcileMaterialTextureBindings(state, shaderIndex, parameterMap, metadata);

        uint perShaderModel = state.Request.ShaderModel;
        bool optionallyMarkedSm6 = unrealMetadata?.IsSm6Shader == true;
        if (optionallyMarkedSm6 || detectedFormat == ShaderBinaryFormat.Dxil)
        {
            if (perShaderModel < 67) perShaderModel = 67;
        }

        EngineDecompileOptions engineOptions = new()
        {
            Format = detectedFormat,
            Symbols = metadata,
            ShaderModel = perShaderModel,
            SymbolEnricher = static (spv, symbols) => MaterialTextureNameInferrer.InferAndAppend(spv, symbols),
        };

        return new PreparedShader(shaderIndex, strippedCode, engineOptions, new ShaderPrep
        {
            ShaderIndex = shaderIndex,
            ContainerInfo = container,
        });
    }

    /// <summary>A per-shader copy, because the decompiler fills the symbols it is handed.</summary>
    private static SerializedProgramData Clone(SerializedProgramData source) => new()
    {
        ConstantBufferParameters = new List<ConstantBufferParameter>(source.ConstantBufferParameters),
        BufferBindingParameters = new List<BufferBindingParameter>(source.BufferBindingParameters),
        TextureParameters = new List<TextureParameter>(source.TextureParameters),
        SamplerParameters = new List<SamplerParameter>(source.SamplerParameters),
        UAVParameters = new List<UAVParameter>(source.UAVParameters),
        DescriptorSetParameters = new List<DescriptorSetParameter>(source.DescriptorSetParameters),
        EntryPoint = source.EntryPoint,
        DebugName = source.DebugName,
        UsedMaterials = new List<string>(source.UsedMaterials),
    };
}
