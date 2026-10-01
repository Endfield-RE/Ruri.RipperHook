using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>What a scripted asset is: the class of the script it runs, which is what the game itself loads it as.
/// One archive files a scripted asset together with the ones it owns (a volume profile with its components), all
/// of them MonoBehaviours, so the script is what tells them apart.</summary>
public static class ScriptedAssets
{
    public static string ScriptClass(this IMonoBehaviour behaviour) =>
        behaviour.ScriptP is { } script ? script.ClassName_R.ToString() : string.Empty;
}
