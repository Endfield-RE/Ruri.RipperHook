using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ruri.RipperHook.Humanoid;

/// <summary>Opt-in dump of the solver's Unity-space local bone rotations.</summary>
internal static class HumanoidSolverTrace
{
    public static void WriteIfRequested(string clipName, SolvedHumanoidPose pose)
    {
        string? path = Environment.GetEnvironmentVariable("RURI_HUMANOID_SOLVER_TRACE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        path = path.Replace("{clip}", SanitizeFileName(clipName), StringComparison.OrdinalIgnoreCase);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder output = new();
        output.AppendLine("frame\ttime\tpath\ttransform\tlocalRot.x\tlocalRot.y\tlocalRot.z\tlocalRot.w");
        foreach ((string bonePath, Quaternion[] rotations) in pose.BoneRotations)
        {
            string transform = bonePath.Replace('\\', '/').Split('/')[^1];
            for (int frame = 0; frame < pose.FrameCount; frame++)
            {
                Quaternion q = rotations[frame];
                output.Append(frame).Append('\t')
                    .Append(F(frame / pose.SampleRate)).Append('\t')
                    .Append(Escape(bonePath)).Append('\t').Append(Escape(transform)).Append('\t')
                    .Append(F(q.X)).Append('\t').Append(F(q.Y)).Append('\t')
                    .Append(F(q.Z)).Append('\t').Append(F(q.W)).AppendLine();
            }
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
        Console.Error.WriteLine(
            $"[HumanoidSolverTrace] '{clipName}' wrote {pose.BoneRotations.Count} bones x " +
            $"{pose.FrameCount} frames to '{Path.GetFullPath(path)}'.");
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
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
