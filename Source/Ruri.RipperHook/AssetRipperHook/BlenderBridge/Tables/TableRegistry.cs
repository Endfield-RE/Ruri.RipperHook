using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.BlenderBridge.Tables;

/// <summary>
/// Every table that can be opened by its handle -- to be searched, or drawn as a view.
///
/// A dataset's table can be opened for as long as it exists: while a host holds it (the pins it
/// was handed keep it) or while the session keeps the answer to hand out again. Registered here
/// for good, every table ever asked for stayed reachable, and so did everything it was built
/// from. A table a host assembled itself has nothing else holding it, so it is kept under its
/// handle until the host states the next one under the same handle.
/// </summary>
public static class TableRegistry
{
    private const int FirstPruneAt = 64;

    private static readonly ConcurrentDictionary<string, WeakReference<ColumnTable>> Asked = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ColumnTable> Hosted = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<ColumnTable, ColumnSearch> Searches = new();
    private static int pruneAt = FirstPruneAt;

    public static void Register(string handle, ColumnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Asked[handle] = new WeakReference<ColumnTable>(table);
        if (Asked.Count >= Volatile.Read(ref pruneAt))
        {
            Prune();
        }
    }

    public static ColumnSearch Opened(string handle)
    {
        ColumnTable table = Hosted.TryGetValue(handle, out ColumnTable? hosted)
            ? hosted
            : Asked.TryGetValue(handle, out WeakReference<ColumnTable>? asked) && asked.TryGetTarget(out ColumnTable? alive)
                ? alive
                : throw new InvalidOperationException(
                    $"no table is open under handle '{handle}' -- open it before searching it.");
        return Searches.GetValue(table, static opened => new ColumnSearch(opened));
    }

    public static int[] Search(string handle, string query, IReadOnlyList<FilterRule>? rules)
        => Opened(handle).Search(query, rules);

    /// <summary>A list the caller assembled, published as a table so it gets the one search,
    /// the one rule evaluator and the one view engine every other list gets. Columns are
    /// spelled as they are anywhere else ("name", "count#", "name|Displayed Name"), and
    /// <paramref name="roles"/> states positionally what each one answers.</summary>
    public static string OpenHostTable(string handle, string[] columns, int[]? roles,
        string[] flatValues)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(flatValues);
        if (columns.Length == 0)
        {
            throw new ArgumentException("a table needs at least one column", nameof(columns));
        }
        if (flatValues.Length % columns.Length != 0)
        {
            throw new ArgumentException(
                $"flatValues length {flatValues.Length} is not a multiple of the {columns.Length} column(s)",
                nameof(flatValues));
        }
        TableBuilder table = new(handle, columns);
        if (roles is { Length: > 0 })
        {
            table.Roles(roles.Select(role => (ColumnRole)role).ToArray());
        }
        foreach (string value in flatValues)
        {
            table.Add(value);
        }
        Hosted[handle] = table.Build();
        return handle;
    }

    /// <summary>Drops the handles whose table no longer exists, once twice as many are registered
    /// as were alive at the last pass -- so a session that asks a million questions holds a
    /// handle for each one it can still open, not for each one it ever asked.</summary>
    private static void Prune()
    {
        foreach ((string handle, WeakReference<ColumnTable> table) in Asked)
        {
            if (!table.TryGetTarget(out _))
            {
                Asked.TryRemove(new KeyValuePair<string, WeakReference<ColumnTable>>(handle, table));
            }
        }
        Volatile.Write(ref pruneAt, Math.Max(FirstPruneAt, Asked.Count * 2));
    }
}
