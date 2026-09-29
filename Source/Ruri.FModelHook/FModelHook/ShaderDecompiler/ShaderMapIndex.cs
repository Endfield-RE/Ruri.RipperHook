using System.Diagnostics;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Which maps a run writes, where each of them lives and which assets name it: everything the
/// run has to know about its subjects before it decompiles anything, and nothing more.
///
/// A shader is shared by every map that compiled to it, so how long its source must be kept is a
/// fact about every map the run will write -- not about the maps read so far. Resolving every
/// subject first, and keeping only each map's hash, place and namers, is what lets each archive's
/// stream count every shader's readers exactly, decompile it once, and let it go the moment its
/// last reader is written. Asking in passes instead decompiled a shader again in every pass whose
/// maps shared it: an install's 2.77 million map entries over 768 thousand distinct shaders.
///
/// The assets are let go as they are read, a unit of subjects at a time, so the index is a few
/// strings per map however large the install. A map's own facts are read again, from its first
/// namer, when its archive's turn comes.
/// </summary>
internal sealed class ShaderMapIndex
{
    /// <summary>
    /// How many bytes of packages one unit keeps together before letting them go. Materials that
    /// share a template sit next to each other in the order they are asked about, so the template
    /// is read once per unit rather than once per material. A unit ends on what it keeps, not on
    /// how many namers it has read: a material package is a few hundred kilobytes and a
    /// world-partition cell carrying one landscape material is megabytes, and a count sized for
    /// the one held hundreds of the other together -- gigabytes at a time.
    /// </summary>
    public const long BytesPerUnit = 256L << 20;

    /// <summary>How often a long index states how far it has got.</summary>
    private const int ProgressEverySubjects = 8192;

    private ShaderMapIndex(IReadOnlyList<IndexedArchive> archives, int named, int distinct)
    {
        Archives = archives;
        Named = named;
        Distinct = distinct;
    }

    /// <summary>Every archive a subject named a map in, by name, each with its maps in the order they were first named.</summary>
    public IReadOnlyList<IndexedArchive> Archives { get; }

    public int Named { get; }

    public int Distinct { get; }

    public static ShaderMapIndex Build(ShaderSourceRequest request, ShaderMapCatalog catalog, Action<string> log, Action<string> logError)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Dictionary<string, IndexedMap> byHash = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unplaced = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IndexedArchive> byArchive = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IShaderMapSubject> subjects = request.Subjects;
        int named = 0;
        int position = 0;
        int units = 0;
        while (position < subjects.Count)
        {
            units++;
            using (DataUnit unit = DataUnit.Begin())
            {
                do
                {
                    foreach (ShaderMapTarget target in subjects[position].Resolve(request.Provider, log, logError))
                    {
                        named++;
                        if (byHash.TryGetValue(target.ShaderMapHash, out IndexedMap? known))
                        {
                            known.NameBy(target.AssetPath);
                            continue;
                        }
                        if (unplaced.Contains(target.ShaderMapHash))
                        {
                            continue;
                        }
                        if (!catalog.TryPlace(target.ShaderMapHash, log, logError, out ShaderMapCatalog.Placement placement))
                        {
                            unplaced.Add(target.ShaderMapHash);
                            logError($"[ShaderSource] '{target.AssetPath}': no archive carries shader map {target.ShaderMapHash}.");
                            continue;
                        }
                        IndexedMap map = new(target, placement);
                        byHash[target.ShaderMapHash] = map;
                        if (!byArchive.TryGetValue(placement.ArchiveName, out IndexedArchive? archive))
                        {
                            archive = new IndexedArchive(placement.ArchiveName, placement.Library);
                            byArchive[placement.ArchiveName] = archive;
                        }
                        archive.Maps.Add(map);
                    }
                    position++;
                    if (subjects.Count > ProgressEverySubjects && position % ProgressEverySubjects == 0)
                    {
                        log($"[ShaderSource] indexed {position}/{subjects.Count} subject(s): {byHash.Count} distinct map(s) so far, {clock.Elapsed.TotalSeconds:F0} s.");
                    }
                }
                while (position < subjects.Count && unit.Held < BytesPerUnit);
            }
        }
        log($"[ShaderSource] {subjects.Count} subject(s) named {named} map(s), {byHash.Count} of them distinct, in {clock.ElapsedMilliseconds} ms "
            + $"over {units} unit(s) ({catalog.OpenedArchiveCount} archive(s) open, {catalog.IndexedMapCount} maps indexed).");
        return new ShaderMapIndex(
            byArchive.Values.OrderBy(static archive => archive.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            named,
            byHash.Count);
    }
}

/// <summary>One archive a run writes into, and the maps of it the run was asked about, in the order they were first named.</summary>
internal sealed class IndexedArchive(string name, ShaderLibrary library)
{
    public string Name { get; } = name;

    public ShaderLibrary Library { get; } = library;

    public List<IndexedMap> Maps { get; } = new();
}

/// <summary>
/// One map the run writes: its hash, where the archive keeps it, the asset that named it first
/// -- what its source is named after and what its facts are read from -- and every asset that
/// named it, in the order they did.
/// </summary>
internal sealed class IndexedMap
{
    private readonly HashSet<string> namers = new(StringComparer.OrdinalIgnoreCase);

    public IndexedMap(ShaderMapTarget first, ShaderMapCatalog.Placement placement)
    {
        ShaderMapHash = first.ShaderMapHash;
        PrimaryAsset = first.AssetPath;
        ShaderPlatform = first.ShaderPlatform;
        Source = first.Source;
        Placement = placement;
        NameBy(first.AssetPath);
    }

    public string ShaderMapHash { get; }

    public string PrimaryAsset { get; }

    public string ShaderPlatform { get; }

    public IShaderMapSubject? Source { get; }

    public ShaderMapCatalog.Placement Placement { get; }

    public List<string> NamedBy { get; } = new();

    public void NameBy(string assetPath)
    {
        if (namers.Add(assetPath))
        {
            NamedBy.Add(assetPath);
        }
    }
}
