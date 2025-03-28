#if ML
using System;
using System.IO;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using UnityExplorer;
using UnityExplorer.Config;
using UnityExplorer.Loader.ML;

#if IL2CPP
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
#else
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.MONO)]
#endif

[assembly: MelonInfo(typeof(ExplorerMelonMod), ExplorerCore.NAME, ExplorerCore.VERSION, ExplorerCore.AUTHOR)]
[assembly: MelonGame(null, null)]
[assembly: MelonColor(0,58,150,221)]

namespace UnityExplorer
{
    public class ExplorerMelonMod : MelonMod, IExplorerLoader
    {
        public string ExplorerFolderName => ExplorerCore.DEFAULT_EXPLORER_FOLDER_NAME;
        public string ExplorerFolderDestination => MelonEnvironment.ModsDirectory;

        public string UnhollowedModulesFolder => Path.Combine(
            Path.GetDirectoryName(MelonEnvironment.ModsDirectory)!,
            Path.Combine("MelonLoader", "Il2CppAssemblies"));

        public ConfigHandler ConfigHandler => _configHandler;
        public MelonLoaderConfigHandler _configHandler;

        public Action<object> OnLogMessage => LoggerInstance.Msg;
        public Action<object> OnLogWarning => LoggerInstance.Warning;
        public Action<object> OnLogError   => LoggerInstance.Error;

        public override void OnInitializeMelon()
        {
            _configHandler = new MelonLoaderConfigHandler();
            ExplorerCore.Init(this);
        }
    }
}
#endif