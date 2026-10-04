using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruri.RipperHook.Core.TypeTree;

public sealed class TypeTreeManifest
{
    public const string BlobName = "ruri.versions";

    public const int CurrentFormatVersion = 2;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("lineages")]
    public List<LineageEntry> Lineages { get; set; } = new();

    public sealed class LineageEntry
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        /// <summary>
        /// The blob holding the lineage's trees as the engine spells them -- Unity's own node and type
        /// names, the vocabulary AssetRipper's type-tree reader speaks. The blob under <see cref="Key"/>
        /// carries the names AssetRipper generates its classes under, which this hook's interpreter
        /// matches fields by; a lineage with one vocabulary names its own blob here.
        /// </summary>
        [JsonPropertyName("unityNames")]
        public string UnityNames { get; set; } = "";

        [JsonPropertyName("versions")]
        public List<VersionEntry> Versions { get; set; } = new();
    }

    public sealed class VersionEntry
    {
        [JsonPropertyName("v")]
        public string Key { get; set; } = "";

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";
    }

    private Dictionary<string, LineageEntry>? byKey;

    public void Invalidate() => byKey = null;

    public LineageEntry? Find(string lineage)
    {
        byKey ??= BuildIndex();
        return byKey.TryGetValue(lineage, out LineageEntry? entry) ? entry : null;
    }

    public int GetOrdinal(string lineage, string version)
    {
        LineageEntry? entry = Find(lineage);
        if (entry is null)
        {
            return -1;
        }

        for (int i = 0; i < entry.Versions.Count; i++)
        {
            if (string.Equals(entry.Versions[i].Key, version, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    public string? GetEngine(string lineage, string version)
    {
        LineageEntry? entry = Find(lineage);
        if (entry is null)
        {
            return null;
        }

        foreach (VersionEntry candidate in entry.Versions)
        {
            if (string.Equals(candidate.Key, version, StringComparison.Ordinal))
            {
                return candidate.Engine;
            }
        }
        return null;
    }

    private Dictionary<string, LineageEntry> BuildIndex()
    {
        Dictionary<string, LineageEntry> index = new(Lineages.Count, StringComparer.Ordinal);
        foreach (LineageEntry entry in Lineages)
        {
            index[entry.Key] = entry;
        }
        return index;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TypeTreeManifest FromJson(string json)
    {
        TypeTreeManifest manifest = JsonSerializer.Deserialize<TypeTreeManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("[TypeTreeManifest] Empty manifest.");

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"[TypeTreeManifest] Manifest format {manifest.FormatVersion} != expected {CurrentFormatVersion}; rebuild the tpk with Ruri.Tpk.");
        }

        return manifest;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
