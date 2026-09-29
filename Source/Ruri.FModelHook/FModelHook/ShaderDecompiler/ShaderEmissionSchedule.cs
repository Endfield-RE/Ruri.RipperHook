namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// When each of an archive's maps can be written, and when each of its shaders can be let go.
///
/// A map is written from the decompiled source of every shader it names and from what its first
/// namer states about it, and one shader is named by many maps -- a depth-only program by every
/// material that draws the same way. So a map is written the moment the last of those settles,
/// and a shader is let go the moment the last map naming it has been written. Every map the
/// archive will write is known before the stream starts, so each shader's readers are counted
/// exactly: it is decompiled once and kept exactly as long as some map still needs it.
///
/// A shader settles when its result is in, or when the last map that names it could not prepare
/// it either. Preparing is retried at every map that names the shader until one succeeds,
/// because a shader is prepared with the symbols of the map that prepares it; settling it at an
/// earlier map would write that map without the shader a later map was still going to give it.
/// </summary>
internal sealed class ShaderEmissionSchedule
{
    private readonly ShaderSourceState state;
    private readonly int[][] shadersByMap;
    private readonly int[] unsettledByMap;
    private readonly Holding?[] holdingByShader;
    private int written;

    public ShaderEmissionSchedule(ShaderSourceState state)
    {
        this.state = state;
        int mapCount = state.ShaderMaps.Count;
        shadersByMap = new int[mapCount][];
        unsettledByMap = new int[mapCount];
        holdingByShader = new Holding?[state.Library.ShaderEntries.Length];
        int[] readers = new int[holdingByShader.Length];
        HashSet<int> named = new();
        for (int map = 0; map < mapCount; map++)
        {
            List<ShaderMapMember> members = state.ShaderMaps[map].Members;
            List<int> shaders = new(members.Count);
            named.Clear();
            foreach (ShaderMapMember member in members)
            {
                if (named.Add(member.ArchiveShaderIndex))
                {
                    shaders.Add(member.ArchiveShaderIndex);
                    readers[member.ArchiveShaderIndex]++;
                }
            }
            shadersByMap[map] = shaders.ToArray();
            unsettledByMap[map] = shaders.Count + 1;
        }
        int[] filled = new int[holdingByShader.Length];
        for (int map = 0; map < mapCount; map++)
        {
            foreach (int shader in shadersByMap[map])
            {
                Holding holding = holdingByShader[shader] ??= new Holding(new int[readers[shader]]);
                holding.Maps[filled[shader]++] = map;
                ShaderCount += filled[shader] == 1 ? 1 : 0;
            }
        }
    }

    /// <summary>How many distinct shaders the archive's maps name: the most the stream can ever hand the decompiler.</summary>
    public int ShaderCount { get; }

    /// <summary>How many of the archive's maps have been written so far.</summary>
    public int Written => Volatile.Read(ref written);

    /// <summary>What the map's first namer states about it has been read.</summary>
    public void FactsRead(int map)
    {
        if (Interlocked.Decrement(ref unsettledByMap[map]) == 0)
        {
            Write(map);
        }
    }

    /// <summary>A shader's result is in.</summary>
    public void Decompiled(int shaderIndex) => Settle(holdingByShader[shaderIndex]!);

    /// <summary>A map could not prepare this shader; it settles without a result when no later map names it.</summary>
    public void NotPrepared(int shaderIndex, int map)
    {
        Holding holding = holdingByShader[shaderIndex]!;
        if (holding.LastMap == map)
        {
            Settle(holding);
        }
    }

    /// <summary>
    /// Once the stream has ended, every map has been written. A map still waiting is a map this
    /// run would otherwise leave out without a word, so it is named instead.
    /// </summary>
    public void ConfirmEveryMapWritten()
    {
        List<string> unwritten = new();
        for (int map = 0; map < unsettledByMap.Length; map++)
        {
            if (Volatile.Read(ref unsettledByMap[map]) != 0)
            {
                unwritten.Add($"{state.ShaderMaps[map].ShaderMapHash} (still waiting on {unsettledByMap[map]})");
            }
        }
        if (unwritten.Count > 0)
        {
            throw new InvalidOperationException(
                $"[ShaderSource] {state.ArchiveName}: {unwritten.Count} map(s) were never written: {string.Join(", ", unwritten)}.");
        }
    }

    private void Settle(Holding holding)
    {
        if (Interlocked.Exchange(ref holding.Settled, 1) == 1)
        {
            return;
        }
        foreach (int map in holding.Maps)
        {
            if (Interlocked.Decrement(ref unsettledByMap[map]) == 0)
            {
                Write(map);
            }
        }
    }

    private void Write(int map)
    {
        ShaderLabEmitter.Emit(state, state.ShaderMaps[map]);
        Interlocked.Increment(ref written);
        foreach (int shader in shadersByMap[map])
        {
            if (Interlocked.Decrement(ref holdingByShader[shader]!.Unwritten) == 0)
            {
                state.ShaderPrepByIndex.TryRemove(shader, out _);
                state.DecompileResultByIndex.TryRemove(shader, out _);
            }
        }
    }

    /// <summary>The maps naming one shader in stream order, the last of them, and how far it has got.</summary>
    private sealed class Holding(int[] maps)
    {
        public int[] Maps { get; } = maps;

        public int LastMap => Maps[^1];

        public int Unwritten = maps.Length;

        public int Settled;
    }
}
