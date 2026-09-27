using System.Collections;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SerializationLogic;
using Newtonsoft.Json;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// Offline R2 diagnostic. Enabled only by RURI_R2_DIAGNOSTIC_OUT and kept out of
/// the normal export path. It serializes object identities and PPtr-shaped
/// fields without converting candidates into execution order.
/// </summary>
internal static class R2RiggingDiagnosticDump
{
    private static readonly HashSet<string> SeedCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "cab-f859e4fe0ae8ddbe9e3aac716c534e11",
        "cab-6ab277218f2b1eb9dcf26eaf1aefc45a",
    };
    private static readonly string[] Interesting =
    [
        "Script", "Component", "Layer", "Constraint", "Rig", "Foot", "Lock",
        "Float", "Vector", "Weight", "Root", "Mid", "Tip", "Target", "Hint",
        "Lod", "Enable", "GameObject", "Parent", "Source", "Job"
    ];

    public static void TryWrite(GameBundle bundle)
    {
        string? output = Environment.GetEnvironmentVariable("RURI_R2_DIAGNOSTIC_OUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        bool fullGraph = Environment.GetEnvironmentVariable("RURI_R2_DIAGNOSTIC_FULL") == "1";
        try
        {
            var assets = new List<Dictionary<string, object?>>();
            var serializedFiles = new List<Dictionary<string, object?>>();
            foreach (AssetCollection collection in bundle.FetchAssetCollections())
            {
                serializedFiles.Add(DescribeSerializedFile(collection));
                foreach (IUnityObjectBase asset in collection)
                {
                    string name = asset.GetBestName();
                    string path = asset.OriginalPath ?? string.Empty;
                    string type = asset.ClassName;
                    string text = $"{name} {path} {type} {asset.GetType().Name}";
                    if (!fullGraph && !SeedCollections.Contains(collection.Name)
                        && !text.Contains("typhoea", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("RigBuilder", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("HGPrepare", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("HGTwoBone", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("FootLock", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("BipedIK", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    assets.Add(new Dictionary<string, object?>
                    {
                        ["collection"] = collection.Name,
                        ["path_id"] = asset.PathID,
                        ["class_name"] = type,
                        ["runtime_type"] = asset.GetType().FullName,
                        ["best_name"] = name,
                        ["original_path"] = asset.OriginalPath,
                        ["asset_bundle"] = asset.AssetBundleName,
                        ["property_names"] = asset.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                            .Where(property => property.GetIndexParameters().Length == 0)
                            .Select(property => property.Name).ToArray(),
                        ["properties"] = DescribeAsset(asset, collection),
                        ["dependencies"] = DescribeDependencies(asset, collection),
                    });
                }
            }
            var result = new Dictionary<string, object?>
            {
                ["schema"] = fullGraph ? "ruri.endfield.r2-rigging-diagnostic.v2-full" : "ruri.endfield.r2-rigging-diagnostic.v1",
                ["status"] = "ok",
                ["asset_count"] = assets.Count,
                ["serialized_files"] = serializedFiles,
                ["diagnostics"] = new object[]
                {
                    new { level = "INFO", code = "r2.diagnostic.written", message = "Reflection identity/PPtr dump written." },
                    new { level = "INFO", code = "r2.diagnostic.scope", message = fullGraph ? "Full loaded-object graph scope; serialized identities, fields, and PPtr edges are retained." : "Seed collections plus named closure scope; serialized identities and fields are retained." },
                },
                ["assets"] = assets,
            };
            string full = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonConvert.SerializeObject(result, Formatting.Indented));
            Console.Error.WriteLine($"[Ruri.CLI] R2 diagnostic: {assets.Count} matching objects -> {full}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Ruri.CLI][WARNING] R2 diagnostic failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> DescribeSerializedFile(AssetCollection collection)
    {
        var dependencies = new List<object?>();
        if (collection is SerializedAssetCollection serialized)
        {
            IReadOnlyList<FileIdentifier> identifiers = GetSerializedFileDependencies(serialized);
            for (int i = 0; i < identifiers.Count; i++)
            {
                FileIdentifier identifier = identifiers[i];
                AssetCollection? resolved = TryResolveDependency(collection, i + 1, identifier, out bool byIdentifier);
                dependencies.Add(new Dictionary<string, object?>
                {
                    ["file_id"] = i + 1,
                    ["asset_path"] = identifier.AssetPath.ToString(),
                    ["guid"] = identifier.Guid.ToString(),
                    ["type"] = identifier.Type.ToString(),
                    ["path_name"] = identifier.PathName,
                    ["path_name_origin"] = identifier.PathNameOrigin,
                    ["resolved_collection"] = resolved?.Name,
                    ["resolved"] = resolved is not null,
                    ["resolved_by_identifier"] = byIdentifier,
                });
            }
        }
        return new Dictionary<string, object?>
        {
            ["collection"] = collection.Name,
            ["file_path"] = collection.FilePath,
            ["dependency_count"] = dependencies.Count,
            ["dependencies"] = dependencies,
        };
    }

    private static object DescribeObject(object value, AssetCollection collection, int depth)
    {
        if (depth >= 3) return "<depth-limit>";
        if (value is IUnityObjectBase asset)
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "object",
                ["path_id"] = asset.PathID,
                ["class_name"] = asset.ClassName,
                ["name"] = asset.GetBestName(),
                ["original_path"] = asset.OriginalPath,
            };
        }
        if (TryDescribePPtr(value, collection, out object? pointerDescription))
            return pointerDescription!;
        if (value is string || value.GetType().IsPrimitive || value is decimal)
            return value;
        if (value is IEnumerable enumerable)
        {
            var items = new List<object?>();
            foreach (object? item in enumerable)
                items.Add(item is null ? null : DescribeObject(item, collection, depth + 1));
            return items;
        }
        var result = new Dictionary<string, object?>();
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead) continue;
            if (!Interesting.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                object? item = property.GetValue(value);
                result[property.Name] = item is null ? null : DescribeObject(item, collection, depth + 1);
            }
            catch (Exception ex)
            {
                result[property.Name] = $"<read-error:{ex.GetType().Name}>";
            }
        }
        return result;
    }

    private static object DescribeAsset(IUnityObjectBase asset, AssetCollection collection)
    {
        var result = new Dictionary<string, object?>
        {
            ["kind"] = "object",
            ["path_id"] = asset.PathID,
            ["class_name"] = asset.ClassName,
            ["name"] = asset.GetBestName(),
            ["original_path"] = asset.OriginalPath,
        };
        foreach (PropertyInfo property in asset.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                continue;
            if (!Interesting.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                object? item = property.GetValue(asset);
                result[property.Name] = item is null ? null : DescribeObject(item, collection, 0);
            }
            catch (Exception ex)
            {
                result[property.Name] = $"<read-error:{ex.GetType().Name}>";
            }
        }
        if (asset is IMonoBehaviour monoBehaviour)
        {
            result["structure_load"] = DescribeMonoBehaviourStructure(monoBehaviour, collection);
        }
        return result;
    }

    private static object DescribeMonoBehaviourStructure(IMonoBehaviour monoBehaviour, AssetCollection collection)
    {
        object? before = monoBehaviour.Structure;
        var result = new Dictionary<string, object?>
        {
            ["state_before"] = before?.GetType().FullName,
        };
        try
        {
            IMonoScript? resolvedScript = monoBehaviour.ScriptP;
            if (resolvedScript is null)
            {
                foreach ((string field, PPtr pointer) in monoBehaviour.FetchDependencies())
                {
                    if (!field.Contains("Script", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ResolvePointerTarget(collection, pointer) is IMonoScript externalScript)
                    {
                        resolvedScript = externalScript;
                        break;
                    }
                }
            }
            result["script_metadata"] = DescribeScriptMetadata(resolvedScript);
        }
        catch (Exception ex)
        {
            result["script_metadata_error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        if (before is UnloadedStructure unloaded && monoBehaviour.ScriptP is IMonoScript script)
        {
            try
            {
                SerializableType? behaviourType = script.GetBehaviourType(unloaded.AssemblyManager, out string? failureReason);
                result["type_lookup"] = new Dictionary<string, object?>
                {
                    ["found"] = behaviourType is not null,
                    ["failure_reason"] = failureReason,
                    ["type"] = behaviourType?.FullName,
                    ["field_count"] = behaviourType?.Fields.Count,
                };
            }
            catch (Exception ex)
            {
                result["type_lookup"] = new Dictionary<string, object?>
                {
                    ["found"] = false,
                    ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }
        try
        {
            SerializableStructure? structure = monoBehaviour.LoadStructure();
            if (structure is null)
            {
                result["state_after"] = "null";
                result["field_count"] = 0;
                return result;
            }
            result["state_after"] = "loaded";
            result["type"] = structure.Type.FullName;
            result["field_count"] = structure.Fields.Length;
            try
            {
                result["fields"] = DescribeStructureFields(structure, collection, 0);
            }
            catch (Exception ex)
            {
                result["field_error"] = $"{ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine($"[Ruri.CLI][WARNING] R2 structure field dump failed for {monoBehaviour.ScriptP?.GetBestName()}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            result["state_after"] = "exception";
            result["error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    private static object? DescribeScriptMetadata(IMonoScript? script)
    {
        if (script is null)
            return null;
        var result = new Dictionary<string, object?>
        {
            ["collection"] = script.Collection.Name,
            ["path_id"] = script.PathID,
            ["name"] = script.GetBestName(),
            ["namespace"] = script.Namespace,
            ["class_name"] = script.ClassName_R,
            ["assembly_name"] = script.AssemblyName,
            ["execution_order"] = script.ExecutionOrder,
        };
        try
        {
            var hash = script.GetPropertiesHash();
            result["properties_hash_bytes"] = new[] { hash.Bytes__0, hash.Bytes__1, hash.Bytes__2, hash.Bytes__3 };
        }
        catch (Exception ex)
        {
            result["properties_hash_error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    private static object DescribeStructureFields(SerializableStructure structure, AssetCollection collection, int depth)
    {
        var fields = new List<object?>();
        for (int i = 0; i < structure.Type.Fields.Count; i++)
        {
            SerializableType.Field field = structure.Type.Fields[i];
            var item = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = field.Name,
                ["type"] = field.Type.FullName,
                ["primitive_type"] = field.Type.Type.ToString(),
                ["array_depth"] = field.ArrayDepth,
                ["align"] = field.Align,
            };
            if (depth < 24)
            {
                item["value"] = DescribeSerializableValue(structure.Fields[i], field, collection, depth + 1);
            }
            else
            {
                item["value"] = "<depth-limit>";
            }
            fields.Add(item);
        }
        return fields;
    }

    private static object? DescribeSerializableValue(SerializableValue value, SerializableType.Field field, AssetCollection collection, int depth)
    {
        object? content = value.CValue;
        if (content is SerializableStructure nested)
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "structure",
                ["type"] = nested.Type.FullName,
                ["fields"] = DescribeStructureFields(nested, collection, depth),
            };
        }
        if (content is IPPtr pointer)
        {
            return DescribePPtr(pointer.FileID, pointer.PathID, collection);
        }
        if (content is string || content is null || content.GetType().IsPrimitive || content is decimal)
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "primitive",
                ["p_value"] = value.PValue,
                ["value"] = content,
                ["type"] = field.Type.FullName,
            };
        }
        if (content is IEnumerable enumerable)
        {
            var items = new List<object?>();
            foreach (object? item in enumerable)
            {
                if (item is SerializableValue serializableValue)
                    items.Add(DescribeSerializableValue(serializableValue, field, collection, depth + 1));
                else
                    items.Add(item is null ? null : DescribeObject(item, collection, depth + 1));
            }
            return new Dictionary<string, object?>
            {
                ["kind"] = "array",
                ["count"] = items.Count,
                ["items"] = items,
            };
        }
        return new Dictionary<string, object?>
        {
            ["kind"] = "value",
            ["runtime_type"] = content.GetType().FullName,
            ["text"] = content.ToString(),
            ["p_value"] = value.PValue,
        };
    }

    private static object DescribeDependencies(IUnityObjectBase asset, AssetCollection collection)
    {
        var result = new List<object>();
        foreach ((string field, PPtr pointer) in asset.FetchDependencies())
        {
            IUnityObjectBase? target = ResolvePointerTarget(collection, pointer);
            result.Add(new Dictionary<string, object?>
            {
                ["field"] = field,
                ["file_id"] = pointer.FileID,
                ["path_id"] = pointer.PathID,
                ["resolved"] = target is null ? null : DescribeTarget(target),
            });
        }
        return result;
    }

    private static bool TryDescribePPtr(object value, AssetCollection collection, out object? description)
    {
        description = null;
        Type type = value.GetType();
        PropertyInfo? fileIdProperty = type.GetProperty("FileID", BindingFlags.Instance | BindingFlags.Public);
        PropertyInfo? pathIdProperty = type.GetProperty("PathID", BindingFlags.Instance | BindingFlags.Public);
        if (fileIdProperty is null || pathIdProperty is null || !fileIdProperty.CanRead || !pathIdProperty.CanRead)
            return false;
        try
        {
            object? fileValue = fileIdProperty.GetValue(value);
            object? pathValue = pathIdProperty.GetValue(value);
            if (fileValue is null || pathValue is null)
                return false;
            int fileId = Convert.ToInt32(fileValue, System.Globalization.CultureInfo.InvariantCulture);
            long pathId = Convert.ToInt64(pathValue, System.Globalization.CultureInfo.InvariantCulture);
            description = DescribePPtr(fileId, pathId, collection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> DescribePPtr(int fileId, long pathId, AssetCollection collection)
    {
        AssetCollection? dependency = null;
        bool resolvedByIdentifier = false;
        FileIdentifier? identifier = null;
        if (fileId > 0 && TryGetFileIdentifier(collection, fileId, out FileIdentifier external))
        {
            identifier = external;
            dependency = TryResolveDependency(collection, fileId, external, out resolvedByIdentifier);
        }
        IUnityObjectBase? target = pathId == 0 ? null : dependency?.TryGetAsset(pathId) ?? collection.TryGetAsset(new PPtr(fileId, pathId));
        var result = new Dictionary<string, object?>
        {
            ["kind"] = "pptr",
            ["file_id"] = fileId,
            ["path_id"] = pathId,
            ["resolved"] = target is null ? null : DescribeTarget(target),
        };
        if (fileId > 0)
        {
            result["external"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileId,
                ["dependency_collection"] = dependency?.Name,
                ["dependency_present"] = dependency is not null,
                ["resolved_by_identifier"] = resolvedByIdentifier,
                ["asset_path"] = identifier?.AssetPath.ToString(),
                ["guid"] = identifier?.Guid.ToString(),
                ["type"] = identifier?.Type.ToString(),
                ["path_name"] = identifier?.PathName,
                ["path_name_origin"] = identifier?.PathNameOrigin,
                ["path_id_preserved"] = pathId,
            };
        }
        return result;
    }

    private static Dictionary<string, object?> DescribeTarget(IUnityObjectBase target)
    {
        return new Dictionary<string, object?>
        {
            ["collection"] = target.Collection.Name,
            ["path_id"] = target.PathID,
            ["class_name"] = target.ClassName,
            ["name"] = target.GetBestName(),
            ["original_path"] = target.OriginalPath,
        };
    }

    private static IUnityObjectBase? ResolvePointerTarget(AssetCollection collection, PPtr pointer)
    {
        if (pointer.PathID == 0) return null;
        if (pointer.FileID > 0 && TryGetFileIdentifier(collection, pointer.FileID, out FileIdentifier identifier))
        {
            AssetCollection? dependency = TryResolveDependency(collection, pointer.FileID, identifier, out _);
            return dependency?.TryGetAsset(pointer.PathID) ?? collection.TryGetAsset(pointer);
        }
        return collection.TryGetAsset(pointer);
    }

    private static bool TryGetFileIdentifier(AssetCollection collection, int fileId, out FileIdentifier identifier)
    {
        identifier = default;
        if (fileId <= 0 || collection is not SerializedAssetCollection serialized)
            return false;
        IReadOnlyList<FileIdentifier> identifiers = GetSerializedFileDependencies(serialized);
        if (fileId > identifiers.Count)
            return false;
        identifier = identifiers[fileId - 1];
        return true;
    }

    // The preserved header snapshot is an optional dependency capability, not
    // part of upstream AssetRipper's public ABI. Never invent missing file IDs.
    private static IReadOnlyList<FileIdentifier> GetSerializedFileDependencies(SerializedAssetCollection collection)
    {
        PropertyInfo? property = typeof(SerializedAssetCollection).GetProperty("SerializedFileDependencies");
        return property?.GetValue(collection) as IReadOnlyList<FileIdentifier>
            ?? throw new NotSupportedException("R2 diagnostics require an AssetRipper dependency snapshot that preserves serialized-file identifiers. Normal import is unaffected.");
    }

    private static AssetCollection? TryResolveDependency(AssetCollection collection, int fileId, FileIdentifier identifier, out bool resolvedByIdentifier)
    {
        resolvedByIdentifier = false;
        if (fileId > 0 && fileId < collection.Dependencies.Count && collection.Dependencies[fileId] is AssetCollection direct)
            return direct;

        string pathName = identifier.PathName ?? string.Empty;
        if (pathName.Length == 0)
            return null;
        foreach (AssetCollection candidate in collection.Bundle.FetchAssetCollections())
        {
            string name = candidate.Name ?? string.Empty;
            if (name.Equals(pathName, StringComparison.OrdinalIgnoreCase)
                || name.Equals($"0000_{pathName}", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(pathName, StringComparison.OrdinalIgnoreCase))
            {
                resolvedByIdentifier = true;
                return candidate;
            }
        }
        return null;
    }
}
