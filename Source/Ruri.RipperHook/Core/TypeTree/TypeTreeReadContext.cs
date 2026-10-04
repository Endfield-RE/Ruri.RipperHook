using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// One read of a captured node: the value, and the object whose slot it fills -- the stock instance that was being read
/// when the reader met the node, or null where no stock class holds the enclosing structure.
/// </summary>
public readonly record struct TypeTreeCapture(object? Owner, TypeTreeValue Value);

public sealed class TypeTreeReadContext
{
    private readonly Dictionary<string, TypeTreeValue> captured = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypeTreeCapture>> occurrences = new(StringComparer.Ordinal);

    public IUnityObjectBase Asset { get; private set; } = null!;

    public TypeTreeVersion Version { get; private set; }

    public ClassIDType ClassID { get; private set; }

    internal void Begin(IUnityObjectBase asset, ClassIDType classID, TypeTreeVersion version)
    {
        captured.Clear();
        Asset = asset;
        ClassID = classID;
        Version = version;
    }

    /// <summary>
    /// The read is over: nothing of it is held any more. A context is reused by every read on its
    /// thread, and one that still held its last asset and the values captured from it held that
    /// asset's collection, the bundle around it and every file the bundle was read from -- a whole
    /// load per thread that had ever read one, never let go.
    /// </summary>
    internal void End()
    {
        captured.Clear();
        occurrences.Clear();
        Asset = null!;
    }

    internal void Capture(string path, object? owner, TypeTreeValue value)
    {
        captured[path] = value;
        if (!occurrences.TryGetValue(path, out List<TypeTreeCapture>? reads))
        {
            reads = new List<TypeTreeCapture>();
            occurrences.Add(path, reads);
        }
        reads.Add(new TypeTreeCapture(owner, value));
    }

    /// <summary>The last value read at the path -- the only one, for a node no sequence encloses.</summary>
    public TypeTreeValue? Find(string path) => captured.TryGetValue(path, out TypeTreeValue? value) ? value : null;

    /// <summary>
    /// Every value read at the path, in read order, each with the object that holds it. A node inside a sequence is read
    /// once per element, and the owner tells the reads apart where the order alone would not.
    /// </summary>
    public IReadOnlyList<TypeTreeCapture> FindAll(string path) =>
        occurrences.TryGetValue(path, out List<TypeTreeCapture>? reads) ? reads : Array.Empty<TypeTreeCapture>();

    /// <summary>The declared paths this read met at least once.</summary>
    public IEnumerable<string> CapturedPaths => occurrences.Keys;

    public TypeTreeValue Require(string path) => Find(path)
        ?? throw new InvalidOperationException(
            $"[TypeTree] '{path}' was not captured while reading {ClassID}. " +
            "Declare it in the hook's Captures list so the read plan retains it.");

    public bool GetBoolean(string path) => Require(path).AsBoolean();

    public int GetInt32(string path) => Require(path).AsInt32();

    public uint GetUInt32(string path) => Require(path).AsUInt32();

    public float GetSingle(string path) => Require(path).AsSingle();

    public byte[] GetByteArray(string path) => Require(path).AsByteArray();

    public Utf8String GetUtf8String(string path) => Require(path).AsUtf8String();

    public bool Has(string path) => captured.ContainsKey(path);
}
