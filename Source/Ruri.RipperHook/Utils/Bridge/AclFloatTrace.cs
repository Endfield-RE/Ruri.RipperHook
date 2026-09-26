using System.Globalization;
using System.Text;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Opt-in lossless dump of every ACL scalar track before Humanoid conversion.
/// Set RURI_ACL_FLOAT_TRACE to a TSV path.  The normal export is unchanged when
/// the variable is absent.
/// </summary>
internal static class AclFloatTrace
{
    internal sealed record Binding(
        int DecoderTrack,
        int BindingIndex,
        uint Path,
        uint Attribute,
        int ClassId,
        byte CustomType,
        string Name,
        bool IsConstant);

    public static string? RequestedPath(string clipName)
    {
        string? requested = Environment.GetEnvironmentVariable("RURI_ACL_FLOAT_TRACE");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return requested
            .Replace("{clip}", SanitizeFileName(clipName), StringComparison.OrdinalIgnoreCase);
    }

    public static void Write(string path, string clipName, float sampleRate, int frameCount,
        IReadOnlyList<Binding> bindings, IReadOnlyList<float[]> samples)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder output = new();
        output.Append("# clip\t").AppendLine(Escape(clipName));
        output.Append("# sampleRate\t")
            .AppendLine(sampleRate.ToString("R", CultureInfo.InvariantCulture));
        output.Append("# frameCount\t").AppendLine(frameCount.ToString(CultureInfo.InvariantCulture));
        output.Append("decoderTrack\tbindingIndex\tpath\tattribute\tclassId\tcustomType\tname\tconstant");
        for (int frame = 0; frame < frameCount; frame++)
        {
            output.Append("\tf").Append(frame.ToString(CultureInfo.InvariantCulture));
        }
        output.AppendLine();

        for (int track = 0; track < bindings.Count; track++)
        {
            Binding binding = bindings[track];
            output.Append(binding.DecoderTrack).Append('\t')
                .Append(binding.BindingIndex).Append('\t')
                .Append("0x").Append(binding.Path.ToString("X8", CultureInfo.InvariantCulture)).Append('\t')
                .Append("0x").Append(binding.Attribute.ToString("X8", CultureInfo.InvariantCulture)).Append('\t')
                .Append(binding.ClassId).Append('\t')
                .Append(binding.CustomType).Append('\t')
                .Append(Escape(binding.Name)).Append('\t')
                .Append(binding.IsConstant ? '1' : '0');
            foreach (float value in samples[track])
            {
                output.Append('\t').Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            output.AppendLine();
        }

        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
        Console.Error.WriteLine(
            $"[ACLFloatTrace] '{clipName}' wrote {bindings.Count} raw scalar tracks x " +
            $"{frameCount} frames to '{Path.GetFullPath(path)}'.");
    }

    private static string Escape(string value) =>
        value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }
        return value;
    }
}
