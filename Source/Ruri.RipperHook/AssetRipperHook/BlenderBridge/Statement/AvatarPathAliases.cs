namespace Ruri.RipperHook.BlenderBridge.Statements;

public static class AvatarPathAliases
{
    public static Dictionary<string, string> Resolve(IEnumerable<string> avatarPaths, IReadOnlyList<string> rigPaths)
    {
        HashSet<string> source = new(avatarPaths.Where(path => !string.IsNullOrEmpty(path)), StringComparer.Ordinal);
        HashSet<string> target = new(rigPaths, StringComparer.Ordinal);
        Dictionary<uint, string> suffixes = UnitySkinning.SuffixTable(rigPaths);
        Dictionary<string, string?> resolved = source.ToDictionary(path => path,
            path => target.Contains(path) ? path : suffixes.GetValueOrDefault(UnitySkinning.EntryCrc(path)), StringComparer.Ordinal);
        static string Parent(string path) => path.LastIndexOf('/') is int slash && slash >= 0 ? path[..slash] : "";
        foreach (string path in source.OrderBy(path => path.Count(c => c == '/')).ThenBy(path => path, StringComparer.Ordinal))
        {
            if (resolved[path] is not null || !resolved.TryGetValue(Parent(path), out string? parent) || parent is null) continue;
            string direct = parent + "/" + path[(path.LastIndexOf('/') + 1)..];
            if (target.Contains(direct)) { resolved[path] = direct; continue; }
            string[] children = target.Where(child => Parent(child) == parent).ToArray();
            HashSet<string>? candidates = null;
            foreach (string descendant in source.Where(descendant => descendant.StartsWith(path + "/", StringComparison.Ordinal)))
            {
                string suffix = descendant[path.Length..];
                HashSet<string> matches = new(children.Where(child => target.Contains(child + suffix)), StringComparer.Ordinal);
                if (matches.Count == 0) continue;
                if (candidates is null) candidates = matches;
                else candidates.IntersectWith(matches);
            }
            if (candidates?.Count == 1 && !resolved.Values.Contains(candidates.Single())) resolved[path] = candidates.Single();
        }
        return resolved.Where(pair => pair.Value is not null && pair.Key != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }
}
