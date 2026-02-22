using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using SAPAccess.Announcements;
using SAPAccess.Config;
using SAPAccess.GameState;
using SAPAccess.Navigation;
using SAPAccess.NVDA;
using UnityEngine;

namespace SAPAccess;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.sapaccess.mod";
    public const string PluginName = "SAPAccess";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource PluginLog = null!;

    private GameObject? _modObject;

    public override void Load()
    {
        PluginLog = Log;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

        // Initialize configuration
        var config = new ModConfig(Config);

        // Initialize core systems
        var focusManager = new FocusManager();
        _ = GamePhaseTracker.Instance;
        _ = ShopStateReader.Instance;
        _ = TeamStateReader.Instance;
        _ = BattleStateReader.Instance;

        // Initialize announcers
        var shopAnnouncer = new ShopAnnouncer();
        var teamAnnouncer = new TeamAnnouncer();
        var menuAnnouncer = new MenuAnnouncer();

        // Register custom MonoBehaviour types with Il2Cpp
        ClassInjector.RegisterTypeInIl2Cpp<KeyboardHandler>();
        ClassInjector.RegisterTypeInIl2Cpp<BattleAnnouncer>();
        ClassInjector.RegisterTypeInIl2Cpp<MenuNavigator>();

        // Create persistent GameObject for MonoBehaviours
        _modObject = new GameObject("SAPAccess");
        Object.DontDestroyOnLoad(_modObject);
        _modObject.hideFlags = HideFlags.HideAndDontSave;

        // Add MonoBehaviour components
        _modObject.AddComponent<KeyboardHandler>();
        _modObject.AddComponent<BattleAnnouncer>();
        _modObject.AddComponent<MenuNavigator>();

        // Test NVDA connection
        if (NvdaClient.IsAvailable)
        {
            if (NvdaClient.IsRunning())
            {
                Log.LogInfo("NVDA is running. Speech output enabled.");
                ScreenReader.Instance.Say("SAPAccess loaded.");
            }
            else
            {
                Log.LogWarning("NVDA DLL found but NVDA is not running. Speech will be logged only.");
            }
        }
        else
        {
            Log.LogWarning("NVDA DLL not found. Speech will be logged only.");
        }

        // Register Harmony patches
        var harmony = new HarmonyLib.Harmony(PluginGuid);
        harmony.PatchAll(typeof(Patches.HangarPatches).Assembly);
        Log.LogInfo("Harmony patches applied.");

        Log.LogInfo($"{PluginName} loaded successfully.");
    }
}
