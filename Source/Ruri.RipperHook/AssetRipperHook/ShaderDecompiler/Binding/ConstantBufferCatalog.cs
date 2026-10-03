using System.Collections.Concurrent;
using AssetRipper.Import.Logging;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Constant-buffer layouts stated outside the programs. A program's parameters name only the part of a buffer shared with the
/// engine that the program happens to list; a description of the whole buffer names the rest. A description is taken for a
/// buffer only when it proves to lay that buffer out -- the same size, and every member the program states starting at one
/// of its fields -- and exactly one candidate does. The program's own names always stand, being what the source spelled; a
/// description only fills the offsets the program leaves unnamed.
/// </summary>
public sealed class ConstantBufferCatalog
{
    public sealed record Member(string Name, int Offset, int ByteSize, ShaderParamType Type, int Rows, int Columns, int ArraySize);

    public sealed record Layout(string Source, int Size, IReadOnlyList<Member> Members);

    private readonly Func<string, IReadOnlyList<Layout>> _candidatesFor;
    private readonly ConcurrentDictionary<string, IReadOnlyList<Layout>> _candidates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);

    public ConstantBufferCatalog(Func<string, IReadOnlyList<Layout>> candidatesFor) => _candidatesFor = candidatesFor;

    public void Enrich(SerializedProgramData symbols)
    {
        foreach (ConstantBufferParameter buffer in symbols.ConstantBufferParameters)
        {
            if (string.IsNullOrEmpty(buffer.Name))
            {
                continue;
            }

            IReadOnlyList<Layout> candidates = _candidates.GetOrAdd(buffer.Name, _candidatesFor);
            if (candidates.Count == 0)
            {
                continue;
            }

            List<Layout> proven = candidates.Where(layout => Lays(layout, buffer)).ToList();
            if (proven.Count == 1)
            {
                Fill(buffer, proven[0]);
            }
            else if (_reported.TryAdd(buffer.Name, true))
            {
                Logger.Warning(LogCategory.Export,
                    $"constant buffer {buffer.Name} ({buffer.Size} bytes): {proven.Count} of the stated layouts "
                    + $"[{string.Join(", ", candidates.Select(static layout => layout.Source))}] lay it out; its unnamed members stay unnamed");
            }
        }
    }

    private static bool Lays(Layout layout, ConstantBufferParameter buffer)
    {
        if (Registers(layout.Size) != Registers(buffer.Size))
        {
            return false;
        }

        var starts = new HashSet<int>(layout.Members.Select(static member => member.Offset));
        return buffer.AllNumericParameters.All(stated => starts.Contains(stated.Index))
            && buffer.StructParameters.All(stated => starts.Contains(stated.Index));
    }

    private static void Fill(ConstantBufferParameter buffer, Layout layout)
    {
        var occupied = new List<(int Start, int End)>();
        foreach (NumericShaderParameter stated in buffer.AllNumericParameters)
        {
            occupied.Add((stated.Index, stated.Index + Span(stated)));
        }
        foreach (StructParameter stated in buffer.StructParameters)
        {
            occupied.Add((stated.Index, stated.Index + stated.StructSize * Math.Max(1, stated.ArraySize)));
        }

        var vectors = new List<VectorParameter>(buffer.VectorParameters);
        var matrices = new List<MatrixParameter>(buffer.MatrixParameters);
        foreach (Member member in layout.Members)
        {
            if (occupied.Any(span => member.Offset >= span.Start && member.Offset < span.End))
            {
                continue;
            }

            if (member.Rows > 1)
            {
                matrices.Add(new MatrixParameter(member.Name, member.Type, member.Offset, member.ArraySize, member.Rows, member.Columns));
            }
            else
            {
                vectors.Add(new VectorParameter(member.Name, member.Type, member.Offset, member.ArraySize, member.Columns));
            }
        }

        buffer.VectorParameters = vectors.OrderBy(static parameter => parameter.Index).ToArray();
        buffer.MatrixParameters = matrices.OrderBy(static parameter => parameter.Index).ToArray();
    }

    private static int Span(NumericShaderParameter stated)
    {
        int count = Math.Max(1, stated.ArraySize);
        if (stated.IsMatrix)
        {
            return 16 * Math.Max((int)stated.RowCount, stated.ColumnCount) * count;
        }
        return count > 1 ? 16 * count : 4 * Math.Max(1, (int)stated.RowCount);
    }

    private static int Registers(int size) => (size + 15) / 16;
}
