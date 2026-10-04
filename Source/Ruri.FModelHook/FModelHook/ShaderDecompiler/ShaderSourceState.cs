using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.FileProvider.Vfs;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// What a run is asked for: which assets to answer about, where the source goes, and how much of
/// each variant to write. There is no filter here and no scope: what is named IS the work, so
/// nothing is gathered that a later step would have to throw away.
/// </summary>
public sealed class ShaderSourceRequest
{
    /// <summary>
    /// Where an install's shader source goes when the caller has nothing better to say:
    /// a folder of its own under the install itself, so two installs never write into
    /// each other's and nobody has to remember a path per game.
    /// </summary>
    public const string DefaultFolderName = "RuriShaderOutput";

    /// <summary>That folder for one install root, or empty when no root is known.</summary>
    public static string DefaultOutputDirectory(string? installRoot) =>
        string.IsNullOrWhiteSpace(installRoot) ? string.Empty : Path.Combine(installRoot, DefaultFolderName);

    public required AbstractVfsFileProvider Provider { get; init; }

    public required IReadOnlyList<IShaderMapSubject> Subjects { get; init; }

    public required string OutputDirectory { get; init; }

    public uint ShaderModel { get; init; } = 51;

    public bool SplitVariantsToHlslFiles { get; init; }

    /// <summary>
    /// Whether the source already in the output folder counts as work done.
    ///
    /// A shader map is compiled once and named by every material that shares it, so asking about
    /// a whole install names the same map again and again -- one install's materials name three
    /// times as many maps as the archives hold. Answering each time writes the same source to a
    /// second folder under a second material's name, and a run of that size cannot be finished in
    /// one sitting anyway. With this stated, a map whose folder is already there is left alone,
    /// so a run adds what is missing and a stopped run resumes by being started again.
    ///
    /// Left unstated for a request that NAMES its materials: what was asked for is written, every
    /// time, which is what asking for it means.
    /// </summary>
    public bool ResumeFromOutput { get; init; }

    public string? EngineUbMetadataDirectory { get; init; }

    /// <summary>
    /// Where the engine dumps live when a request names no folder: beside the assembly that reads
    /// them, which is where they are built. Asking the PROCESS for its base directory answers with
    /// the host's folder and, in a host that supplies none at all, with nothing -- and a relative
    /// path never resolved, so every engine fact silently read as absent under that host.
    /// </summary>
    public static string DefaultEngineUbMetadataDirectory => Path.Combine(
        Path.GetDirectoryName(typeof(ShaderSourceRequest).Assembly.Location) is { Length: > 0 } beside
            ? beside
            : AppContext.BaseDirectory,
        "EngineUbMetadata");

    public Action<string>? Log { get; init; }

    public Action<string>? LogError { get; init; }
}

/// <summary>One archive a run read from: what of it was asked for, and where that landed.</summary>
public sealed record ShaderSourceArchive(string Archive, int ShaderMaps, int Decompiled, string OutputDirectory);

public sealed record ShaderSourceSummary(int ShaderMaps, int Decompiled, int Skipped, int Failed, IReadOnlyList<ShaderSourceArchive> Archives);

/// <summary>One archive's worth of a run: the maps asked about that it carries, and what came of them.</summary>
internal sealed class ShaderSourceState
{
    public ShaderSourceState(ShaderSourceRequest request, EngineMetadata metadata, ShaderLibrary library,
        string archiveName, string outputDirectory, OutputWriter writer)
    {
        Request = request;
        Metadata = metadata;
        Library = library;
        ArchiveName = archiveName;
        OutputDirectory = outputDirectory;
        Writer = writer;
        Variants = new VariantPool(outputDirectory, writer);
        Log = request.Log ?? (_ => { });
        LogError = request.LogError ?? (_ => { });
    }

    /// <summary>Where this archive's shader variants land, one file per distinct text.</summary>
    public VariantPool Variants { get; }

    /// <summary>The run's one writer, which every file of this archive goes through.</summary>
    public OutputWriter Writer { get; }

    public ShaderSourceRequest Request { get; }
    public EngineMetadata Metadata { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }

    public ShaderLibrary Library { get; }
    public string ArchiveName { get; }
    public string OutputDirectory { get; }

    public List<ShaderMapInfo> ShaderMaps { get; } = new();

    /// <summary>
    /// The shaders prepared and decompiled so far that some map still to be written names.
    /// Filled while the archive streams and emptied as its maps are written, so at any moment
    /// they hold the work in flight rather than the archive.
    /// </summary>
    public ConcurrentDictionary<int, ShaderPrep> ShaderPrepByIndex { get; } = new();
    public ConcurrentDictionary<int, DecompileResult> DecompileResultByIndex { get; } = new();

    public int Decompiled;
    public int Skipped;
    public int Failed;

    /// <summary>Bindings left as the decompiler wrote them because two would have come out under one name.</summary>
    public int NameCollisions;
}

internal sealed class ShaderContainerInfo
{
    public string ContainerKey { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string ShaderMapHash { get; init; } = string.Empty;
    public string ShaderTypeHash { get; init; } = string.Empty;
    public string ShaderTypeName { get; set; } = string.Empty;
    public string VertexFactoryTypeHash { get; init; } = string.Empty;
    public string VertexFactoryTypeName { get; set; } = string.Empty;
    public string PipelineTypeHash { get; init; } = string.Empty;
    public string PipelineTypeName { get; set; } = string.Empty;
    public int PermutationId { get; init; }
    public int ResourceIndex { get; init; }
    public byte Frequency { get; init; }
    public string ShaderHash { get; init; } = string.Empty;
}

/// <summary>
/// One map an archive's stream writes. Where it is and which shaders it names are known from the
/// index and the archive's own tables before the stream starts; what its first namer states about
/// it -- each shader's identity and parameter map, the symbols its shaders are named from, the
/// properties and the render state -- is read when the map's turn comes, and is all the map keeps
/// of that asset.
/// </summary>
internal sealed class ShaderMapInfo
{
    public required string ShaderMapHash { get; init; }
    public required ShaderMapCatalog.Placement Placement { get; init; }
    public required IShaderMapSubject? Source { get; init; }
    public required string ShaderPlatform { get; init; }
    public required List<string> Assets { get; init; }
    public required string PrimaryAsset { get; init; }
    public required string PrimaryName { get; init; }
    public required List<ShaderMapMember> Members { get; init; }

    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; set; } = new();
    public Dictionary<int, FShaderParameterMapInfo> ParameterMapByShaderIndex { get; set; } = new();
    public MaterialSymbolSource? Symbols { get; set; }
    public string PropertiesBlock { get; set; } = string.Empty;
    public string SubShaderTags { get; set; } = string.Empty;
    public string PassCommands { get; set; } = string.Empty;

    /// <summary>
    /// Lets go of what the map's first namer stated about it, once the map is written. Those facts
    /// are read when the map's turn comes and used to prepare the shaders it is first to name and
    /// to write it; nothing reads them after. Held until the archive's stream ended, they were
    /// every map's identities, parameter maps and symbols at once -- gigabytes on an archive of
    /// tens of thousands of maps.
    /// </summary>
    public void LetGoOfFacts()
    {
        ContainerByShaderIndex = new();
        ParameterMapByShaderIndex = new();
        Symbols = null;
        PropertiesBlock = string.Empty;
        SubShaderTags = string.Empty;
        PassCommands = string.Empty;
    }

    /// <summary>
    /// One indexed map as its archive's stream starts it: named after the asset that named it
    /// first, the shaders it owns being its run of the archive's shared index list.
    /// </summary>
    public static ShaderMapInfo Of(IndexedMap indexed)
    {
        ShaderMapCatalog.Placement placement = indexed.Placement;
        List<ShaderMapMember> members = new((int)placement.Map.NumShaders);
        for (uint member = 0; member < placement.Map.NumShaders; member++)
        {
            long offset = placement.Map.ShaderIndicesOffset + member;
            if (offset < 0 || offset >= placement.Library.ShaderIndices.Length)
            {
                continue;
            }
            int shaderIndex = (int)placement.Library.ShaderIndices[offset];
            if (shaderIndex < 0 || shaderIndex >= placement.Library.ShaderEntries.Length)
            {
                continue;
            }
            members.Add(new ShaderMapMember { RelativeIndex = (int)member, ArchiveShaderIndex = shaderIndex });
        }
        string primaryName = Path.GetFileNameWithoutExtension(indexed.PrimaryAsset);
        return new ShaderMapInfo
        {
            ShaderMapHash = indexed.ShaderMapHash,
            Placement = placement,
            Source = indexed.Source,
            ShaderPlatform = indexed.ShaderPlatform,
            Assets = [.. indexed.NamedBy],
            PrimaryAsset = indexed.PrimaryAsset,
            PrimaryName = string.IsNullOrWhiteSpace(primaryName) ? "UnknownMaterial" : primaryName,
            Members = members,
        };
    }
}

internal sealed class ShaderMapMember
{
    public int RelativeIndex { get; init; }
    public int ArchiveShaderIndex { get; init; }
}

/// <summary>
/// What the emitter reads of a prepared shader. The code and the options it was decompiled
/// with go to the decompiler and nowhere else, so they are not kept past the handing over.
/// </summary>
internal sealed class ShaderPrep
{
    public required int ShaderIndex { get; init; }
    public ShaderContainerInfo? ContainerInfo { get; init; }
}

/// <summary>One shader ready for the decompiler: its code and options for the decompile, and what the emitter keeps of it.</summary>
internal readonly record struct PreparedShader(int ShaderIndex, byte[] Code, DecompileOptions Options, ShaderPrep Prep);
