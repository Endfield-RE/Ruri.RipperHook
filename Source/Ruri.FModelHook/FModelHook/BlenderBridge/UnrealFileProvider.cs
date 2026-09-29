using AssetRipper.Import.Logging;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Versions;
using Ruri.FModelHook.ShaderDecompiler.Semantics;
using Ruri.RipperHook.BlenderBridge.Data;
using System.Collections.Concurrent;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// The provider with one instance per package for the length of one piece of work: within a
/// <see cref="DataUnit"/>, every path into a package -- the loader asking for it, another
/// package's import map reaching into it -- meets the same object graph, so a package is read
/// and deserialized once per answer however many packages import it, and a material's textures
/// or a mesh's skeleton are not held in a private copy by every importer. The unit keeps those
/// instances and lets them go when the answer is given; outside every unit a package is read
/// afresh and kept by nobody. The material semantics read off the mounted game's compiled base
/// passes are the mount's: the shader libraries are the archives', so the index over them and
/// what each shader map was found to sample live as long as the mount, whichever answers ask.
/// </summary>
public sealed class UnrealFileProvider : DefaultFileProvider
{
    private readonly Lazy<MaterialSemanticsResolver> semantics;
    private readonly Func<GameFile, IPackage> readAfresh;
    private readonly Func<GameFile, long> weigh;

    public UnrealFileProvider(DirectoryInfo directory, DirectoryInfo[] extraDirectories, SearchOption searchOption, VersionContainer versions, StringComparer pathComparer)
        : base(directory, extraDirectories, searchOption, versions, pathComparer)
    {
        readAfresh = LoadUncached;
        weigh = Weigh;
        semantics = new Lazy<MaterialSemanticsResolver>(
            () => new MaterialSemanticsResolver(this, message => Logger.Info(LogCategory.Import, message), message => Logger.Verbose(LogCategory.Import, message)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The material semantics of this mount, built on first use and kept until the mount is disposed.</summary>
    public MaterialSemanticsResolver Semantics => semantics.Value;

    public override IPackage LoadPackage(GameFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return DataUnit.Current is { } unit
            ? unit.Keep(this, static () => new PackageInstances()).Load(file, readAfresh, weigh, unit)
            : LoadUncached(file);
    }

    /// <summary>The package read afresh and kept by nobody, for a scan that only reads its header.</summary>
    public IPackage LoadUncached(GameFile file) => base.LoadPackage(file);

    /// <summary>
    /// What reading a package keeps for as long as the package is kept: its header and its
    /// export data, each read whole. Its bulk data is read when an export asks for it and kept
    /// by nobody.
    /// </summary>
    private long Weigh(GameFile file)
    {
        Files.FindPayloads(file, out GameFile? exportData, out _, out _);
        return file.Size + (exportData?.Size ?? 0);
    }

    public override void Dispose()
    {
        if (semantics.IsValueCreated)
        {
            semantics.Value.Dispose();
        }
        base.Dispose();
    }

    /// <summary>The one instance of each package one unit has read.</summary>
    private sealed class PackageInstances : IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<IPackage>> packages = new(StringComparer.OrdinalIgnoreCase);

        public IPackage Load(GameFile file, Func<GameFile, IPackage> read, Func<GameFile, long> weigh, DataUnit unit)
        {
            Lazy<IPackage> entry = packages.GetOrAdd(file.Path, _ => new Lazy<IPackage>(() =>
            {
                IPackage package = read(file);
                unit.Hold(weigh(file));
                return package;
            }, LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return entry.Value;
            }
            catch
            {
                packages.TryRemove(new KeyValuePair<string, Lazy<IPackage>>(file.Path, entry));
                throw;
            }
        }

        public void Dispose() => packages.Clear();
    }
}
