using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;

namespace AssetRipper.Export.Modules.Shaders.ShaderBlob;

public sealed class ShaderSubProgramBlob
{
	public void Read(AssetCollection shaderCollection, byte[] compressedBlob, uint offset, uint compressedLength, uint decompressedLength)
	{
		m_shaderCollection = shaderCollection;
		ReadBlob(compressedBlob, offset, compressedLength, decompressedLength, 0);
	}

	public void Read(AssetCollection shaderCollection, byte[] compressedBlob, AssetList<uint> offsets, AssetList<uint> compressedLengths, AssetList<uint> decompressedLengths)
	{
		m_shaderCollection = shaderCollection;
		for (int i = 0; i < offsets.Count; i++)
		{
			ReadBlob(compressedBlob, offsets[i], compressedLengths[i], decompressedLengths[i], i);
		}
	}

	private void ReadBlob(byte[] compressedBlob, uint offset, uint compressedLength, uint decompressedLength, int segment)
	{
		while (m_decompressedBlobSegments.Count < segment + 1) { m_decompressedBlobSegments.Add([]); }
		m_decompressedBlobSegments[segment] = DecompressedBlob.DecompressBlob(compressedBlob, offset, compressedLength, decompressedLength);

		if (segment == 0)
		{
			using MemoryStream blobMem = new MemoryStream(m_decompressedBlobSegments[segment]);
			using AssetReader blobReader = new AssetReader(blobMem, m_shaderCollection);
			Entries = ReadAssetArray(blobReader);
			m_cachedSubPrograms.Clear();
		}
	}

	private static ShaderSubProgramEntry[] ReadAssetArray(AssetReader reader)
	{
		int count = reader.ReadInt32();

		ShaderSubProgramEntry[] array = CreateAndInitializeArray<ShaderSubProgramEntry>(count);
		for (int i = 0; i < count; i++)
		{
			array[i].Read(reader);
		}
		if (reader.IsAlignArray)
		{
			reader.AlignStream();
		}
		return array;
	}

	private static T[] CreateAndInitializeArray<T>(int length) where T : new()
	{
		ArgumentOutOfRangeException.ThrowIfNegative(length);

		if (length == 0)
		{
			return [];
		}

		T[] array = new T[length];
		for (int i = 0; i < length; i++)
		{
			array[i] = new();
		}
		return array;
	}


	public ShaderSubProgram GetSubProgram(uint blobIndex, TrailingParameterSectionsReader? trailingSections)
	{
		if (m_cachedSubPrograms.TryGetValue((blobIndex, blobIndex), out ShaderSubProgram? subProgram))
		{
			return subProgram;
		}

		subProgram = new ShaderSubProgram();
		ReadEntry(blobIndex, subProgram, readProgramData: true, readParams: true, trailingSections);

		m_cachedSubPrograms.TryAdd((blobIndex, blobIndex), subProgram);
		return subProgram;
	}

	public ShaderSubProgram GetSubProgram(uint blobIndex, uint paramBlobIndex, TrailingParameterSectionsReader? trailingSections)
	{
		if (m_cachedSubPrograms.TryGetValue((blobIndex, paramBlobIndex), out ShaderSubProgram? subProgram))
		{
			return subProgram;
		}

		subProgram = new ShaderSubProgram();
		ReadEntry(blobIndex, subProgram, readProgramData: true, readParams: false, trailingSections);
		ReadEntry(paramBlobIndex, subProgram, readProgramData: false, readParams: true, trailingSections);

		m_cachedSubPrograms.TryAdd((blobIndex, paramBlobIndex), subProgram);
		return subProgram;
	}

	/// <summary>
	/// One entry, read the way the engine reads it. Parameters are the last thing an entry holds, so an entry read
	/// through its parameters is read to its end; bytes left over are a section this reader does not know, and the
	/// parameters read before them cannot be trusted to be all the entry states.
	/// </summary>
	private void ReadEntry(uint index, ShaderSubProgram subProgram, bool readProgramData, bool readParams, TrailingParameterSectionsReader? trailingSections)
	{
		if (index >= Entries.Length)
		{
			throw new InvalidDataException($"a program names blob entry {index}, but the blob holds {Entries.Length}");
		}

		ShaderSubProgramEntry entry = Entries[index];
		byte[] segmentBytes = m_decompressedBlobSegments[entry.Segment];
		using MemoryStream entryMem = new MemoryStream(segmentBytes, entry.Offset, entry.Length, writable: false);
		using AssetReader entryReader = new AssetReader(entryMem, m_shaderCollection);

		try
		{
			subProgram.Read(entryReader, readProgramData, readParams, trailingSections);
		}
		catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException)
		{
			throw new InvalidDataException(
				$"blob entry {index} (version {subProgram.BlobVersion}, {entry.Length} bytes, read as "
				+ $"{(readProgramData ? "code" : string.Empty)}{(readProgramData && readParams ? " and " : string.Empty)}{(readParams ? "parameters" : string.Empty)}) "
				+ $"fails at byte {entryMem.Position}: {exception.Message}; head {Convert.ToHexString(segmentBytes, entry.Offset, Math.Min(64, entry.Length))}",
				exception);
		}
		if (readParams && entryMem.Position != entry.Length)
		{
			throw new InvalidDataException(
				$"blob entry {index} (version {subProgram.BlobVersion}) holds {entry.Length} bytes, but its parameters end at "
				+ $"{entryMem.Position}: the rest is a section no reader here knows");
		}
	}

	public ShaderSubProgramEntry[] Entries { get; set; } = [];

	private AssetCollection m_shaderCollection;
	private List<byte[]> m_decompressedBlobSegments = [];
	private readonly Dictionary<(uint, uint), ShaderSubProgram> m_cachedSubPrograms = new();

	public const string GpuProgramIndexName = "GpuProgramIndex";
}
