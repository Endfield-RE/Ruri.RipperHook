using System.Diagnostics.CodeAnalysis;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// The answers a session hands out again without making them again: up to a byte budget, the
/// least recently asked going first.
///
/// An answer about the install -- a roster, a language, a folder's children -- is asked again
/// every time a list is drawn, and costs next to nothing to keep. A payload -- a selection's
/// meshes and nodes, an asset's text -- is read once, by the load that asked for it. Kept for
/// the session, every load the session ever made stayed resident long after the host had
/// closed what it was handed, and memory only ever grew. One larger than the whole budget is
/// never kept at all: it lives exactly as long as whoever asked for it holds it.
/// </summary>
internal sealed class KeptAnswers(long budget)
{
    private readonly Dictionary<string, LinkedListNode<Entry>> byHandle = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recency = new();
    private readonly object gate = new();
    private long held;

    private readonly record struct Entry(string Handle, ColumnTable Table, long Bytes);

    public bool TryGet(string handle, [NotNullWhen(true)] out ColumnTable? table)
    {
        lock (gate)
        {
            if (byHandle.TryGetValue(handle, out LinkedListNode<Entry>? node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                table = node.Value.Table;
                return true;
            }
        }
        table = null;
        return false;
    }

    public void Offer(string handle, ColumnTable table)
    {
        long bytes = table.ByteSize;
        if (bytes > budget)
        {
            return;
        }
        lock (gate)
        {
            if (byHandle.Remove(handle, out LinkedListNode<Entry>? replaced))
            {
                recency.Remove(replaced);
                held -= replaced.Value.Bytes;
            }
            byHandle[handle] = recency.AddFirst(new Entry(handle, table, bytes));
            held += bytes;
            while (held > budget && recency.Last is { } oldest)
            {
                recency.RemoveLast();
                byHandle.Remove(oldest.Value.Handle);
                held -= oldest.Value.Bytes;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            byHandle.Clear();
            recency.Clear();
            held = 0;
        }
    }
}
