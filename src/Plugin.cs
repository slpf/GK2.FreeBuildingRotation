using System;
using System.IO;
using System.Reflection;
using GK2.FreeBuildingRotation;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

[assembly: AssemblyProduct(ModInfo.Name)]
[assembly: AssemblyTitle(ModInfo.Name)]
[assembly: AssemblyDescription(ModInfo.Description)]
[assembly: AssemblyCopyright(ModInfo.Copyright)]
[assembly: AssemblyVersion(ModInfo.Version)]
[assembly: AssemblyFileVersion(ModInfo.Version)]
[assembly: AssemblyInformationalVersion(ModInfo.Version)]

namespace GK2.FreeBuildingRotation;

[BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : BaseUnityPlugin
{
    private Harmony? harmony;
    internal static RotationService? Service { get; private set; }

    private void Awake()
    {
        MigrateLegacyConfig();
        ConfigEntry<bool> enabled = Config.Bind("General", "Enabled", true,
            "Enable additional orientations. Restart the game after changing this setting.");
        ConfigEntry<bool> debug = Config.Bind("General", "DebugLogging", false,
            "Log compatibility decisions, orientation, footprint and access points.");
        ConfigEntry<string> excluded = Config.Bind("Compatibility", "ExcludedIds", string.Empty,
            "Comma-separated WGO IDs or asset IDs to exclude. Restart required.");
        
        if (!enabled.Value)
        {
            Logger.LogInfo("Free Building Rotation disabled.");
            return;
        }

        try
        {
            Service = new RotationService(Logger, debug, excluded.Value);
            harmony = new Harmony(ModInfo.Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Free Building Rotation {ModInfo.Version} loaded. Additional orientations are enabled for compatible buildings.");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf();
            Service = null;
            Logger.LogError($"Free Building Rotation could not initialize: {error}");
        }
    }

    private void MigrateLegacyConfig()
    {
        try
        {
            string legacyPath = Path.Combine(Paths.ConfigPath, "gk2.freerotation.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(legacyPath))
                return;
            File.Copy(legacyPath, Config.ConfigFilePath, false);
            Config.Reload();
            Logger.LogInfo("Imported configuration from gk2.freerotation.cfg.");
        }
        catch (Exception error)
        {
            Logger.LogWarning($"Could not import the previous configuration: {error.Message}");
        }
    }
}
