using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The one thing that writes a run's files.
///
/// A run's output is hundreds of thousands of small files, and who writes them decides how the
/// disk is used. Every worker writing its own file the moment it exists is sixteen streams of
/// scattered small writes, which a spinning disk serves one seek at a time while the workers that
/// issued them wait on it. Every file goes through here instead, queued in the order it was
/// finished and written by one thread in that order: the disk sees one stream, a worker hands a
/// file over and goes back to decompiling, and a disk slower than the decompiler holds the run
/// back only once the queue is full.
///
/// Nothing but the output is ever written. A write that fails stops the queue from taking more
/// and is rethrown when the run completes, so a run that could not write its output never
/// reports that it did.
/// </summary>
internal sealed class OutputWriter
{
    private const int QueuedFiles = 1024;

    private readonly BlockingCollection<(string Path, string Text)> queue = new(QueuedFiles);
    private readonly Thread writer;
    private volatile ExceptionDispatchInfo? failure;
    private long filesWritten;
    private long charactersWritten;

    public OutputWriter()
    {
        writer = new Thread(Drain)
        {
            IsBackground = true,
            Name = "Shader source output",
        };
        writer.Start();
    }

    /// <summary>How many files and characters have reached the disk so far.</summary>
    public long FilesWritten => Interlocked.Read(ref filesWritten);

    public long CharactersWritten => Interlocked.Read(ref charactersWritten);

    /// <summary>Queues one file; blocks only while the queue is full.</summary>
    public void Write(string path, string text)
    {
        failure?.Throw();
        queue.Add((path, text));
    }

    /// <summary>Every queued file written, or the first failure among them thrown.</summary>
    public void Complete()
    {
        queue.CompleteAdding();
        writer.Join();
        failure?.Throw();
    }

    private void Drain()
    {
        foreach ((string path, string text) in queue.GetConsumingEnumerable())
        {
            if (failure is not null)
            {
                continue;
            }
            try
            {
                OutputFile.Write(path, text);
                Interlocked.Increment(ref filesWritten);
                Interlocked.Add(ref charactersWritten, text.Length);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
