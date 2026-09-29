namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// When each of an archive's maps can be written, and when each of its shaders can be let go.
///
/// A map is written from the decompiled source of every shader it names, and one shader is
/// named by many maps -- a depth-only program by every material that draws the same way. So a
/// map is written the moment the last of its shaders settles, and a shader is let go the moment
/// the last map naming it has been written. The archive streams through memory instead of being
/// held in it until its last shader is in.
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
    private readonly Dictionary<int, Holding> holdingByShader = new();

    public ShaderEmissionSchedule(ShaderSourceState state)
    {
        this.state = state;
        shadersByMap = new int[state.ShaderMaps.Count][];
        unsettledByMap = new int[state.ShaderMaps.Count];
        Dictionary<int, List<int>> mapsByShader = new();
        for (int map = 0; map < state.ShaderMaps.Count; map++)
        {
            List<int> shaders = new(state.ShaderMaps[map].Members.Count);
            HashSet<int> named = new();
            foreach (ShaderMapMember member in state.ShaderMaps[map].Members)
            {
                if (named.Add(member.ArchiveShaderIndex))
                {
                    shaders.Add(member.ArchiveShaderIndex);
                }
            }
            shadersByMap[map] = shaders.ToArray();
            unsettledByMap[map] = shaders.Count;
            foreach (int shader in shaders)
            {
                if (!mapsByShader.TryGetValue(shader, out List<int>? maps))
                {
                    maps = new List<int>();
                    mapsByShader[shader] = maps;
                }
                maps.Add(map);
            }
        }
        foreach ((int shader, List<int> maps) in mapsByShader)
        {
            holdingByShader[shader] = new Holding(maps.ToArray());
        }
    }

    /// <summary>How many distinct shaders the archive's maps name: the most the stream can ever hand the decompiler.</summary>
    public int ShaderCount => holdingByShader.Count;

    /// <summary>A shader's result is in.</summary>
    public void Decompiled(int shaderIndex) => Settle(holdingByShader[shaderIndex]);

    /// <summary>A map could not prepare this shader; it settles without a result when no later map names it.</summary>
    public void NotPrepared(int shaderIndex, int map)
    {
        Holding holding = holdingByShader[shaderIndex];
        if (holding.LastMap == map)
        {
            Settle(holding);
        }
    }

    /// <summary>Every map that names no shader at all, which no settling shader would ever write.</summary>
    public void WriteEmptyMaps()
    {
        for (int map = 0; map < shadersByMap.Length; map++)
        {
            if (shadersByMap[map].Length == 0)
            {
                Write(map);
            }
        }
    }

    /// <summary>
    /// Once the stream has ended, every map has been written. A map still waiting on a shader
    /// is a map this run would otherwise leave out without a word, so it is named instead.
    /// </summary>
    public void ConfirmEveryMapWritten()
    {
        List<string> unwritten = new();
        for (int map = 0; map < unsettledByMap.Length; map++)
        {
            if (Volatile.Read(ref unsettledByMap[map]) != 0)
            {
                unwritten.Add($"{state.ShaderMaps[map].ShaderMapHash} ({unsettledByMap[map]} shader(s) never settled)");
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
        foreach (int shader in shadersByMap[map])
        {
            if (Interlocked.Decrement(ref holdingByShader[shader].Unwritten) == 0)
            {
                state.ShaderPrepByIndex.TryRemove(shader, out _);
                state.DecompileResultByIndex.TryRemove(shader, out _);
            }
        }
    }

    /// <summary>The maps naming one shader, the last of them, and how far it has got.</summary>
    private sealed class Holding(int[] maps)
    {
        public int[] Maps { get; } = maps;

        public int LastMap { get; } = maps[^1];

        public int Unwritten = maps.Length;

        public int Settled;
    }
}
