using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The source of the shaders a set of assets compiled to.
///
/// Two stages, and neither of them install-wide beyond what was asked. The index resolves every
/// subject into the maps it names -- each map's hash, the archive that carries it and every asset
/// that named it -- keeping nothing of the assets but that. Then each archive is one stream: the
/// maps are read from their first namer in the order they were named, every shader they name is
/// read out of the archive and decompiled once, and each map is written the moment its shaders
/// are in, each shader let go once the last map naming it is written. Nothing is gathered that
/// was not asked for, nothing is decompiled twice, and nothing but the source reaches the disk.
/// </summary>
public static class ShaderSourceRun
{
    public static ShaderSourceSummary Execute(ShaderSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("A run writes source files; state where with OutputDirectory.", nameof(request));
        }
        Action<string> log = request.Log ?? (_ => { });
        Action<string> logError = request.LogError ?? (_ => { });

        Stopwatch whole = Stopwatch.StartNew();
        string gameVersion = request.Provider.Versions.Game.ToString();
        EngineMetadata metadata = EngineMetadata.Cached(request.EngineUbMetadataDirectory, gameVersion, log, logError);
        MaterialConstantBufferReader.Opcodes = metadata.PreshaderOpcodes;
        MaterialUniformBufferRecipe.Current = metadata.MaterialUniformBuffer;

        ShaderMapIndex index = ShaderMapIndex.Build(request, ShaderMapCatalog.For(request.Provider), log, logError);
        if (index.Distinct == 0)
        {
            log("[ShaderSource] nothing named a compiled shader map.");
            return new ShaderSourceSummary(0, 0, 0, 0, []);
        }

        int maps = 0, decompiled = 0, skipped = 0, failed = 0, alreadyWritten = 0;
        List<ShaderSourceArchive> archives = new(index.Archives.Count);
        OutputWriter writer = new();
        ExceptionDispatchInfo? stopped = null;
        try
        {
            using ShaderDecompilerEngine engine = new();
            foreach (IndexedArchive archive in index.Archives)
            {
                string outputDirectory = Path.Combine(request.OutputDirectory, archive.Name).Replace('\\', '/');
                List<IndexedMap> pending = archive.Maps;
                if (request.ResumeFromOutput)
                {
                    HashSet<string> written = ShaderLabEmitter.Written(outputDirectory);
                    pending = archive.Maps.Where(map => !written.Contains(ShaderLabEmitter.HashPrefix(map.ShaderMapHash))).ToList();
                    alreadyWritten += archive.Maps.Count - pending.Count;
                    if (pending.Count == 0)
                    {
                        continue;
                    }
                }
                ShaderSourceState state = new(request, metadata, archive.Library, archive.Name, outputDirectory, writer);
                foreach (IndexedMap indexed in pending)
                {
                    state.ShaderMaps.Add(ShaderMapInfo.Of(indexed));
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                Stream(state, engine);
                ShaderLibrary library = archive.Library;
                log($"[ShaderSource] {archive.Name}: shader-maps={state.ShaderMaps.Count} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}, "
                    + $"{state.Variants.Written} variant file(s) new and {state.Variants.Shared} shared, read {library.BytesRead / (1024 * 1024)} MB of the archive's {library.Size / (1024 * 1024)} MB, "
                    + $"in {stopwatch.ElapsedMilliseconds} ms -> {outputDirectory}");

                archives.Add(new ShaderSourceArchive(archive.Name, state.ShaderMaps.Count, state.Decompiled, outputDirectory));
                maps += state.ShaderMaps.Count;
                decompiled += state.Decompiled;
                skipped += state.Skipped;
                failed += state.Failed;
            }
        }
        catch (Exception exception)
        {
            stopped = ExceptionDispatchInfo.Capture(exception);
        }
        try
        {
            writer.Complete();
        }
        catch when (stopped is not null)
        {
        }
        stopped?.Throw();

        log($"[ShaderSource] whole run {whole.ElapsedMilliseconds} ms: {maps} map(s), {decompiled} decompiled, {failed} failed, "
            + $"{writer.FilesWritten} file(s) written ({writer.CharactersWritten / (1024 * 1024)} M characters)"
            + (alreadyWritten > 0 ? $", {alreadyWritten} map(s) already written." : "."));
        return new ShaderSourceSummary(maps, decompiled, skipped, failed, archives);
    }

    /// <summary>
    /// Every shader this archive's maps name, prepared, decompiled and written as ONE stream.
    ///
    /// The maps are read in the order they were named, and their shaders prepared in that order
    /// and decompiled across every core while the rest are still being read. A map is written the
    /// moment its shaders and its facts are in, and a shader is let go once every map naming it
    /// has been written, so what the archive occupies is the work in flight.
    ///
    /// One stream per archive rather than one per map: the pool is only as wide as what it is
    /// handed, and a map is forty-odd shaders whose costs differ by orders of magnitude, so per
    /// map the workers finished early and waited on each map's one slow shader.
    /// </summary>
    private static void Stream(ShaderSourceState state, ShaderDecompilerEngine engine)
    {
        ShaderEmissionSchedule schedule = new(state);
        int[] shaderBySequence = new int[schedule.ShaderCount];
        engine.DecompileEach(Requests(state, schedule, shaderBySequence), (sequence, result) =>
        {
            int shaderIndex = shaderBySequence[sequence];
            state.DecompileResultByIndex[shaderIndex] = result;
            schedule.Decompiled(shaderIndex);
        });
        schedule.ConfirmEveryMapWritten();
    }

    /// <summary>The prepared shaders as the decompiler takes them, each position remembered so its result finds its shader.</summary>
    private static IEnumerable<(byte[] Binary, EngineDecompileOptions Options)> Requests(
        ShaderSourceState state, ShaderEmissionSchedule schedule, int[] shaderBySequence)
    {
        int sequence = 0;
        foreach (PreparedShader shader in ShaderBinaries.Prepare(state, schedule))
        {
            shaderBySequence[sequence++] = shader.ShaderIndex;
            yield return (shader.Code, shader.Options);
        }
    }
}

/// <summary>
/// What the engine's own type registrations say, dumped per engine version: the uniform buffers a
/// shader type binds, and the names behind the hashes a cooked map states.
/// </summary>
internal sealed class EngineMetadata
{
    private EngineMetadata(EngineUbMetadataRegistry uniformBuffers, ShaderTypeSeedRegistry shaderTypes,
        HashNameIndex vertexFactoryTypes, HashNameIndex pipelineTypes, MaterialPreshaderOpcodes preshaderOpcodes,
        MaterialUniformBufferRecipe materialUniformBuffer)
    {
        UniformBuffers = uniformBuffers;
        ShaderTypes = shaderTypes;
        VertexFactoryTypes = vertexFactoryTypes;
        PipelineTypes = pipelineTypes;
        PreshaderOpcodes = preshaderOpcodes;
        MaterialUniformBuffer = materialUniformBuffer;
    }

    public EngineUbMetadataRegistry UniformBuffers { get; }
    public ShaderTypeSeedRegistry ShaderTypes { get; }
    public HashNameIndex VertexFactoryTypes { get; }
    public HashNameIndex PipelineTypes { get; }
    public MaterialPreshaderOpcodes PreshaderOpcodes { get; }
    public MaterialUniformBufferRecipe MaterialUniformBuffer { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Game), EngineMetadata> Loaded = new();

    /// <summary>
    /// The engine facts for one metadata folder and game, read once per process. They are files
    /// on disk that do not change while the process runs, and reading a few hundred of them was
    /// paid again on every request when it need only ever be paid on the first.
    /// </summary>
    public static EngineMetadata Cached(string? directory, string gameVersion, Action<string> log, Action<string> logError)
    {
        string root = directory ?? ShaderSourceRequest.DefaultEngineUbMetadataDirectory;
        return Loaded.GetOrAdd((root, gameVersion), key => Load(key.Root, key.Game, log, logError));
    }

    public static EngineMetadata Load(string? directory, string gameVersion, Action<string> log, Action<string> logError)
    {
        string root = directory ?? ShaderSourceRequest.DefaultEngineUbMetadataDirectory;
        bool tryBase = ShaderDecompilerSettingsAccess.Current.TryMatchBaseEngineVersion;
        string? game = string.IsNullOrEmpty(gameVersion) ? null : gameVersion;
        return new EngineMetadata(
            EngineUbMetadataRegistry.LoadForGame(root, game, tryBase, log, logError),
            ShaderTypeSeedRegistry.LoadForGame(root, game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_VertexFactoryType", game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_ShaderPipelineType", game, tryBase, log, logError),
            MaterialPreshaderOpcodes.LoadForGame(root, game, tryBase, log, logError),
            MaterialUniformBufferRecipe.LoadForGame(root, game, tryBase, log, logError));
    }
}
