using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

var file = Path.GetFullPath(args[0]);
var folder = Path.GetDirectoryName(file)!;
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var dependency = Path.Combine(folder, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
foreach (var name in new[] {
    "Ruri.RipperHook.AssetRipperGameHook.Endfield.EndfieldBundleDecoder",
    "Ruri.RipperHook.AssetRipperGameHook.Endfield.EndfieldVfs",
    "Ruri.RipperHook.AssetRipperGameHook.Endfield.EndfieldHookCommon",
    "Ruri.RipperHook.Bridge.LegacyClipCurveBlob"
}) {
    if (assembly.GetType(name, false) != null) throw new Exception("Private type leaked: " + name);
}
if (assembly.GetManifestResourceNames().Contains("RuriTypeTree.tpk")) throw new Exception("Private type tree leaked");
var bridge = assembly.GetType("Ruri.RipperHook.Bridge.RipperBlenderBridge", true)!;
var blob = assembly.GetType("Ruri.RipperHook.Bridge.ClipCurveBlob", true)!;
if (blob.GetField("DecodeOverride")!.GetValue(null) != null) throw new Exception("Private decoder is active");
var solve = assembly.GetType("Ruri.RipperHook.Humanoid.HumanoidClipGenericizer", true)!;
if (solve.GetProperty("UsePrimarySkeletonProfile")!.GetValue(null) != null) throw new Exception("Private profile is active");
Console.WriteLine("PUBLIC_LEGACY52_ABI_AND_ISOLATION_PASS " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
