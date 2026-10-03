using System.Runtime.CompilerServices;
using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

/// <summary>
/// The IL2CPP model of the install a session reads. A process holds one model: one the import already built is that
/// install's and is answered as it is; otherwise it is built from the install's own player image and metadata, with the
/// import's instruction-set registration and through the same reader hooks a game applies to the import.
/// </summary>
public static class Il2CppInstallContext
{
    public static ApplicationAnalysisContext For(string gameRoot)
    {
        if (Cpp2IlApi.CurrentAppContext is { } built)
        {
            return built;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        RuntimeHelpers.RunClassConstructor(typeof(IL2CppManager).TypeHandle);
        if (!PlatformChecker.CheckPlatform([gameRoot], LocalFileSystem.Instance, out PlatformGameStructure? platform, out _)
            || platform.Il2CppGameAssemblyPath is not { } image
            || platform.Il2CppMetaDataPath is not { } metadata)
        {
            throw new InvalidOperationException($"{gameRoot} holds no IL2CPP player");
        }

        UnityVersion version = platform.Version ?? Cpp2IlApi.DetermineUnityVersion(platform.UnityPlayerPath, platform.GameDataPath);
        Cpp2IlApi.InitializeLibCpp2Il(image, metadata, version, false);
        return Cpp2IlApi.CurrentAppContext ?? throw new InvalidOperationException($"no IL2CPP model was built for {gameRoot}");
    }
}
