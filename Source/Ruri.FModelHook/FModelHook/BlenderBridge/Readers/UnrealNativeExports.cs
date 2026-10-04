using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.BlenderBridge.Readers;

/// <summary>
/// The exports of one package that are of the kinds a reader asks for, each read -- and nothing
/// else read to find them.
///
/// A package's header states every export's class, so which exports a reader wants is known
/// before any of them is deserialized. Reading all of them to keep a few is where a
/// whole-install question spent its time and its memory: a world-partition cell carries one
/// landscape material among thousands of actors, components and heightmap textures, and every
/// export whose class is a Blueprint opens the package defining that class, and its parents',
/// merely to be constructed.
///
/// What an export constructs as is the reader's own construction's answer
/// (<see cref="AbstractUePackage.ConstructObject"/>), asked with the class alone, once per class.
/// Only a class whose resolution opens no other package is asked -- a native one, as the reader
/// itself decides that. A Blueprint class is passed over unopened, so the kinds asked for must be
/// ones no Blueprint can derive: a material, a mesh, a compiled script. That is what makes the
/// answer the same set reading every export would have kept, rather than a guess at it.
/// </summary>
public static class UnrealNativeExports
{
    private const string NativePackagePrefix = "/Script/";

    public static IEnumerable<UObject> Of(IPackage package, IReadOnlyList<Type> kinds)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(kinds);
        return package switch
        {
            Package pak => Of(pak, kinds),
            IoPackage io => Of(io, kinds),
            _ => throw new NotSupportedException(
                $"[Unreal] '{package.Name}' is a {package.GetType().Name}, which states no export classes to choose by."),
        };
    }

    private static IEnumerable<UObject> Of(Package package, IReadOnlyList<Type> kinds)
    {
        Dictionary<int, bool> wantedByClass = new();
        for (int slot = 0; slot < package.ExportMap.Length; slot++)
        {
            FObjectExport export = package.ExportMap[slot];
            if (!wantedByClass.TryGetValue(export.ClassIndex.Index, out bool wanted))
            {
                wanted = ResolvesUnopened(package, export.ClassIndex)
                    && Constructs(package, package.ResolvePackageIndex(export.ClassIndex), (EObjectFlags)export.ObjectFlags, kinds);
                wantedByClass[export.ClassIndex.Index] = wanted;
            }
            if (wanted)
            {
                yield return package.ExportsLazy[slot].Value;
            }
        }
    }

    private static IEnumerable<UObject> Of(IoPackage package, IReadOnlyList<Type> kinds)
    {
        Dictionary<ulong, bool> wantedByClass = new();
        for (int slot = 0; slot < package.ExportMap.Length; slot++)
        {
            FExportMapEntry export = package.ExportMap[slot];
            if (!wantedByClass.TryGetValue(export.ClassIndex.TypeAndId, out bool wanted))
            {
                wanted = export.ClassIndex.IsScriptImport
                    && Constructs(package, package.ResolveObjectIndex(export.ClassIndex), export.ObjectFlags, kinds);
                wantedByClass[export.ClassIndex.TypeAndId] = wanted;
            }
            if (wanted)
            {
                yield return package.ExportsLazy[slot].Value;
            }
        }
    }

    /// <summary>
    /// Whether the reader resolves this class import without opening another package: its outer
    /// chain ends in this package's own export, or in a native package. The same walk the reader's
    /// import resolution makes before it decides to open anything.
    /// </summary>
    private static bool ResolvesUnopened(Package package, FPackageIndex @class)
    {
        if (!@class.IsImport)
        {
            return false;
        }
        FPackageIndex cursor = @class;
        for (int step = 0; step <= package.ImportMap.Length; step++)
        {
            if (cursor.IsExport)
            {
                return true;
            }
            int slot = -cursor.Index - 1;
            if (slot >= package.ImportMap.Length)
            {
                return false;
            }
            FObjectImport import = package.ImportMap[slot];
            if (import.OuterIndex.IsNull)
            {
                return import.ObjectName.Text.StartsWith(NativePackagePrefix, StringComparison.Ordinal);
            }
            cursor = import.OuterIndex;
        }
        return false;
    }

    private static bool Constructs(AbstractUePackage package, ResolvedObject? @class, EObjectFlags flags, IReadOnlyList<Type> kinds)
    {
        if (@class is null)
        {
            return false;
        }
        UObject probe = package.ConstructObject(@class, package, flags);
        foreach (Type kind in kinds)
        {
            if (kind.IsInstanceOfType(probe))
            {
                return true;
            }
        }
        return false;
    }
}
