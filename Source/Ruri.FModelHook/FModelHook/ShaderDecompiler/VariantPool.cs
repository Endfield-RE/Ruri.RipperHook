using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One archive's shader variants, each written once however many of its maps use it.
///
/// A shader map names the shaders the engine compiled it to, and the archive hands the same
/// shader to every map that compiled to it: a depth-only vertex program is one blob that
/// thousands of materials share. Writing each map's variants under the map's own folder wrote
/// that blob out again per map -- measured on one shipped title, two thirds of four hundred
/// thousand written files were a copy of one already there.
///
/// What is NOT shared is the naming. The same shader reads the material constant buffer, and
/// this pipeline names that buffer's members from the material's own parameters, so one shader
/// under two materials is the same code with different member names -- the most useful thing
/// the output says. So the file is keyed by its own CONTENT, not by the shader it came from:
/// identical text is written once, text that differs by a single member name is two files. That
/// is exact rather than nearly exact, and it is what makes the sharing lossless.
///
/// The provenance a variant used to carry in its header -- which map, which material, which slot
/// of that map -- is what made identical code look different, and it is already stated by the
/// .shader that includes the file. So the pooled file states only what is true of the shader
/// itself, and the map states the rest.
///
/// Which files the pool already holds is read off the folder once, when the pool is opened, and
/// kept in memory with every file this run adds: asking the disk about each of a run's variants
/// was a seek per variant on a spinning disk, for a question the folder answers in one listing.
/// </summary>
internal sealed class VariantPool
{
    public const string FolderName = "_Shaders";

    /// <summary>
    /// How many bytes of the text's digest name the file. Eight is what keeps two DIFFERENT
    /// shaders off one name across an install: six left a one-in-a-thousand chance of a
    /// collision over an install's variants, and a collision is silently the wrong shader.
    /// </summary>
    private const int DigestBytes = 8;

    private readonly string directory;
    private readonly OutputWriter writer;
    private readonly Lazy<bool> folder;
    private readonly ConcurrentDictionary<string, byte> present;
    private int written;
    private int shared;

    public VariantPool(string archiveDirectory, OutputWriter writer)
    {
        directory = Path.Combine(archiveDirectory, FolderName);
        this.writer = writer;
        folder = new Lazy<bool>(CreateFolder, LazyThreadSafetyMode.ExecutionAndPublication);
        present = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                present.TryAdd(Path.GetFileName(file), 0);
            }
        }
    }

    /// <summary>How many files this run put in the pool, and how many writes the pool absorbed.</summary>
    public int Written => Volatile.Read(ref written);

    public int Shared => Volatile.Read(ref shared);

    /// <summary>
    /// The pool file holding this text, queued for writing if the pool does not hold it yet. The
    /// name carries the variant's own keyword so a reader can still tell what it is, a digest of
    /// the text so two spellings of one shader never land on the same name, and the extension of
    /// the language the text is actually in: a ray-tracing stage comes out as GLSL, and a GLSL
    /// body under an <c>.hlsl</c> name is a lie every tool downstream believes.
    /// </summary>
    public string Include(string variantKeyword, string extension, string text)
    {
        string fileName = variantKeyword + "_" + Digest(text) + extension;
        if (present.TryAdd(fileName, 0))
        {
            _ = folder.Value;
            writer.Write(Path.Combine(directory, fileName), text);
            Interlocked.Increment(ref written);
        }
        else
        {
            Interlocked.Increment(ref shared);
        }
        return FolderName + "/" + fileName;
    }

    private bool CreateFolder()
    {
        Directory.CreateDirectory(directory);
        return true;
    }

    private static string Digest(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, DigestBytes);
    }
}
