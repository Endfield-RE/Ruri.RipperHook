using System.Collections.Concurrent;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// One piece of work asked of the session: whatever the answer reads, kept together for
/// exactly as long as the answer takes and let go the moment it is given.
///
/// A reader that shares what it reads between the parts of one answer -- a package every
/// component of a scene imports, read once however many import it -- keeps that sharing HERE
/// and not on itself. Kept on the reader, it outlived every answer that made it and grew with
/// every question the session was asked: a whole-install shader run read an install's worth of
/// material packages and held every one of them, some 1.7 MB a material, until the machine was
/// swapping. Kept here, it ends with the answer, and a read outside every unit shares nothing
/// and keeps nothing, so no caller can forget to let go.
///
/// A unit belongs to the flow that began it: every dataset call begins one unless it is already
/// part of a caller's, and work the answer hands to other threads is part of the same unit. A
/// job that answers in passes begins a fresh unit for each pass, so each pass lets go of what
/// the one before it read.
/// </summary>
public sealed class DataUnit : IDisposable
{
    private static readonly AsyncLocal<DataUnit?> Open = new();

    private readonly DataUnit? outer;
    private readonly ConcurrentDictionary<object, Lazy<IDisposable>> kept = new(ReferenceEqualityComparer.Instance);
    private int ended;

    private DataUnit(DataUnit? outer)
    {
        this.outer = outer;
    }

    /// <summary>The unit this flow is part of, or null outside every unit.</summary>
    public static DataUnit? Current => Open.Value;

    /// <summary>A fresh unit for this flow, whatever it was part of; ending it returns the flow to that.</summary>
    public static DataUnit Begin()
    {
        DataUnit unit = new(Open.Value);
        Open.Value = unit;
        return unit;
    }

    /// <summary>What <paramref name="owner"/> keeps for the length of this unit, made the first time it asks.</summary>
    public T Keep<T>(object owner, Func<T> make) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(make);
        return (T)kept.GetOrAdd(owner, _ => new Lazy<IDisposable>(make, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
        {
            return;
        }
        Open.Value = outer;
        foreach (Lazy<IDisposable> value in kept.Values)
        {
            if (value.IsValueCreated)
            {
                value.Value.Dispose();
            }
        }
        kept.Clear();
    }
}
