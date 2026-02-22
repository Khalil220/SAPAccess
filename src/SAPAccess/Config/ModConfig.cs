using BepInEx.Configuration;

namespace SAPAccess.Config;

/// <summary>
/// BepInEx configuration entries for SAPAccess.
/// </summary>
public class ModConfig
{
    public static ModConfig? Instance { get; private set; }

    public ConfigEntry<Verbosity> VerbosityLevel { get; }
    public ConfigEntry<bool> AnnounceBattleDetails { get; }
    public ConfigEntry<bool> AnnounceShopOnTurnStart { get; }
    public ConfigEntry<bool> BrailleOutput { get; }
    public ConfigEntry<float> SpeechDelay { get; }

    public ModConfig(ConfigFile config)
    {
        Instance = this;

        VerbosityLevel = config.Bind(
            "Speech", "Verbosity", Verbosity.Normal,
            "How much detail to include in announcements. " +
            "Minimal = just essentials, Normal = standard detail, Verbose = everything.");

        AnnounceBattleDetails = config.Bind(
            "Speech", "AnnounceBattleDetails", true,
            "Announce individual attacks, abilities, and summons during battle.");

        AnnounceShopOnTurnStart = config.Bind(
            "Speech", "AnnounceShopOnTurnStart", true,
            "Automatically read the shop contents when a new turn starts.");

        BrailleOutput = config.Bind(
            "Speech", "BrailleOutput", true,
            "Send focus text to braille display in addition to speech.");

        SpeechDelay = config.Bind(
            "Speech", "SpeechDelay", 0.1f,
            "Minimum delay in seconds between speech announcements during battle.");
    }
}

public enum Verbosity
{
    Minimal,
    Normal,
    Verbose
}
