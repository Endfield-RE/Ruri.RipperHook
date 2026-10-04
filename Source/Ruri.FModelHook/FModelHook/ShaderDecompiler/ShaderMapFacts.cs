using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// What one map's first namer states about it, read when the map's turn comes: which shader each
/// of its entries is and with which parameter map, the symbols its shaders are named from, the
/// properties it exposes and how its material draws.
///
/// The index let go of the asset once it knew the map's hash, so the asset is asked again here,
/// through the subject that named the map and nothing else. What the map keeps is the facts; the
/// asset goes with the unit it was read in. A map named by no asset -- a global or a script's --
/// states nothing here beyond its own place in the archive.
/// </summary>
internal static class ShaderMapFacts
{
    public readonly record struct Reading(bool Properties, bool RenderState);

    public static Reading Read(ShaderSourceState state, ShaderMapInfo map)
    {
        ShaderMapTarget target = Target(state, map);
        EngineMetadata metadata = state.Metadata;
        map.ContainerByShaderIndex = ShaderMapIdentity.Of(target, map.Placement, map.PrimaryName,
            metadata.ShaderTypes, metadata.VertexFactoryTypes, metadata.PipelineTypes);
        map.ParameterMapByShaderIndex = ParameterMaps(target, map.Members);

        bool properties = false;
        if ((target.ShaderMap?.Content as FMaterialShaderMapContent)?.MaterialCompilationOutput?.UniformExpressionSet is { } expressions)
        {
            properties = ShaderLabProperties.Read(map, expressions);
            map.Symbols = MaterialSymbols.Of(map.PrimaryAsset, map.ShaderPlatform, expressions, target.Material ?? target.Owner);
            if (map.Symbols is null)
            {
                state.LogError($"Shader map {map.ShaderMapHash} ({map.PrimaryAsset}) states an expression set but no symbols came of it - material CB will be unnamed.");
            }
        }
        bool renderState = target.Material is { } material && ShaderLabRenderState.Read(map, material);
        return new Reading(properties, renderState);
    }

    /// <summary>
    /// The map as its first namer states it, asked again; a map whose naming holds nothing to
    /// read is only its hash and its namer's path.
    /// </summary>
    private static ShaderMapTarget Target(ShaderSourceState state, ShaderMapInfo map)
    {
        if (map.Source is { } source)
        {
            foreach (ShaderMapTarget stated in source.Resolve(state.Request.Provider, state.Log, state.LogError))
            {
                if (string.Equals(stated.ShaderMapHash, map.ShaderMapHash, StringComparison.OrdinalIgnoreCase))
                {
                    return stated;
                }
            }
            state.LogError($"[ShaderSource] '{map.PrimaryAsset}' named shader map {map.ShaderMapHash} when indexed and no longer does; its shaders are written without its symbols.");
        }
        return new ShaderMapTarget
        {
            ShaderMapHash = map.ShaderMapHash,
            AssetPath = map.PrimaryAsset,
            OwningAssetPath = map.PrimaryAsset,
            ShaderPlatform = map.ShaderPlatform,
        };
    }

    /// <summary>
    /// Each shader's parameter map as the material states it, joined to the archive by the
    /// resource index the map counts its shaders along.
    /// </summary>
    private static Dictionary<int, FShaderParameterMapInfo> ParameterMaps(ShaderMapTarget target, List<ShaderMapMember> members)
    {
        Dictionary<int, FShaderParameterMapInfo> byArchiveIndex = new();
        if (target.ShaderMap?.Content is not FMaterialShaderMapContent content)
        {
            return byArchiveIndex;
        }
        Dictionary<int, FShaderParameterMapInfo> byResourceIndex = new();
        Collect(content.Shaders, byResourceIndex);
        foreach (FMeshMaterialShaderMap meshMap in content.OrderedMeshShaderMaps ?? [])
        {
            Collect(meshMap?.Shaders, byResourceIndex);
        }
        if (byResourceIndex.Count == 0)
        {
            return byArchiveIndex;
        }
        foreach (ShaderMapMember member in members)
        {
            if (byResourceIndex.TryGetValue(member.RelativeIndex, out FShaderParameterMapInfo? parameterMap))
            {
                byArchiveIndex[member.ArchiveShaderIndex] = parameterMap;
            }
        }
        return byArchiveIndex;
    }

    private static void Collect(FShader[]? shaders, Dictionary<int, FShaderParameterMapInfo> destination)
    {
        foreach (FShader shader in shaders ?? [])
        {
            if (shader?.ParameterMapInfo is { } parameterMap)
            {
                destination[shader.ResourceIndex] = parameterMap;
            }
        }
    }
}
