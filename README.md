# SAPAccess

A screen reader accessibility mod for [Super Auto Pets](https://store.steampowered.com/app/1714040/Super_Auto_Pets/) using [BepInEx 6 IL2CPP](https://github.com/BepInEx/BepInEx) and [NVDA](https://www.nvaccess.org/).

SAPAccess adds full keyboard navigation and NVDA speech output to Super Auto Pets, making the game playable for blind and visually impaired users.

## Features

- **Virtual focus system** with keyboard-driven navigation across all menus, shop, team, and battle screens
- **NVDA speech and braille output** for all game elements including pets, food, abilities, stats, and UI prompts
- **Battle narration** with event-by-event announcements (attacks, faints, abilities, summons)
- **Shop management** with dedicated keys for rolling, freezing, buying, selling, and merging
- **Team management** with pet placement, repositioning, and targeting
- **Menu accessibility** for Customize, Pack Shop, Versus, Replay, Achievements, and more
- **Dialog handling** for alerts, pickers, subscription prompts, deck viewers, and choosers
- **Configurable settings** for verbosity, speech delay, braille, and end-turn confirmation

## Requirements

- [Super Auto Pets](https://store.steampowered.com/app/1714040/Super_Auto_Pets/) (Steam, Windows)
- [BepInEx 6 IL2CPP](https://github.com/BepInEx/BepInEx/releases) (bleeding edge build 753 or compatible)
- [NVDA](https://www.nvaccess.org/download/) screen reader (running in the background)
- Windows 10/11 (64-bit)

## Installation

### For Users

1. **Install BepInEx 6** into the Super Auto Pets game directory:
   - Download `BepInEx-Unity.IL2CPP-win-x64` from [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)
   - Extract the contents into the game folder (`Steam/steamapps/common/Super Auto Pets/`)
   - Launch the game once and close it so BepInEx generates its files

2. **Install SAPAccess**:
   - Download `SAPAccess.dll` from the [Releases](https://github.com/Khalil220/SAPAccess/releases) page
   - Place it in `Super Auto Pets/BepInEx/plugins/SAPAccess/`
   - Copy `nvdaControllerClient64.dll` to the game root directory (`Super Auto Pets/`)

3. **Launch the game** with NVDA running. You should hear "SAPAccess loaded" on startup.

### Troubleshooting

- Check `BepInEx/LogOutput.log` in the game directory for errors
- Make sure NVDA is running before launching the game
- If speech doesn't work, verify that `nvdaControllerClient64.dll` is in the game root directory (not in the plugins folder)

## Keybindings

### Navigation (All Screens)

| Key | Action |
|---|---|
| Left / Right Arrow | Navigate between items |
| Up / Down Arrow | Scroll item details or switch groups |
| Tab | Cycle to next group |
| Enter / Space | Activate focused element |
| Escape | Go back, cancel, or stop speech |
| Home / End | Jump to first / last item in group |
| F1 | Context-sensitive help |
| F2 | Toggle end-turn confirmation |
| F3 | Sound volume (Shift+F3 to decrease) |
| F4 | Music volume (Shift+F4 to decrease) |
| F5 | Ambiance volume (Shift+F5 to decrease) |
| F6 | Battle music volume (Shift+F6 to decrease) |
| F7 | Menu music volume (Shift+F7 to decrease) |

### Shop Phase

| Key | Action |
|---|---|
| R | Roll shop |
| E | End turn |
| F | Freeze / unfreeze item |
| X | Sell focused team pet |
| M | Merge shop pet onto matching team pet |
| G | Jump to shop |
| B | Jump to team |
| S | Shop summary |
| A | Gold |
| L | Lives |
| T | Turn info |
| Q | Turn timer |
| Shift + Left/Right | Reposition team pet |

### Battle Phase

| Key | Action |
|---|---|
| P | Toggle auto-play |
| Enter / Space | Advance to next battle event (manual mode) |

## Configuration

Settings are stored in `BepInEx/config/com.sapaccess.mod.cfg` and can be edited while the game is closed.

| Setting | Default | Description |
|---|---|---|
| `Verbosity` | Normal | Announcement detail level (Minimal, Normal, Verbose) |
| `AnnounceBattleDetails` | true | Narrate individual battle events |
| `AnnounceShopOnTurnStart` | true | Auto-read shop contents on new turn |
| `BrailleOutput` | true | Send focus text to braille display |
| `SpeechDelay` | 0.1 | Minimum seconds between battle announcements |
| `ConfirmEndTurn` | true | Prompt before ending turn with unspent gold |

## Building from Source

### Prerequisites

- [.NET 6.0 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)
- [BepInEx 6 IL2CPP](https://github.com/BepInEx/BepInEx/releases) installed in the game directory
- Super Auto Pets launched at least once with BepInEx (to generate interop assemblies)

### Setup

1. **Clone the repository:**
   ```bash
   git clone https://github.com/Khalil220/SAPAccess.git
   cd SAPAccess
   ```

2. **Copy interop assemblies** from the game directory:
   ```bash
   ./tools/copy-interop.sh
   ```
   This copies BepInEx-generated interop DLLs from the game into the `interop/` directory. The project references these for compilation. If they're not available, the project falls back to minimal stubs in `stubs/` that allow compilation but not full functionality.

3. **Build:**
   ```bash
   dotnet build src/SAPAccess/SAPAccess.csproj -c Release
   ```

4. **Build and deploy** (copies the built DLL to the game directory):
   ```bash
   ./tools/deploy.sh
   ```
   The deploy script uses the default Steam path. Set the `GAME_DIR` environment variable to override:
   ```bash
   GAME_DIR="/path/to/Super Auto Pets" ./tools/deploy.sh
   ```

### Project Structure

```
SAPAccess/
├── src/SAPAccess/
│   ├── Plugin.cs                 # BepInEx entry point
│   ├── Config/ModConfig.cs       # BepInEx configuration bindings
│   ├── NVDA/
│   │   ├── NvdaClient.cs         # P/Invoke wrapper for NVDA controller
│   │   └── ScreenReader.cs       # High-level speech/braille output
│   ├── Navigation/
│   │   ├── FocusManager.cs       # Virtual focus cursor and group system
│   │   ├── KeyboardHandler.cs    # Input processing (MonoBehaviour)
│   │   └── MenuNavigator.cs      # Page scanning and UI state management
│   ├── GameState/
│   │   ├── GamePhaseTracker.cs   # Tracks game phase (menu, shop, battle)
│   │   ├── ShopStateReader.cs    # Reads shop data (gold, pets, food)
│   │   ├── TeamStateReader.cs    # Reads team composition
│   │   └── BattleStateReader.cs  # Reads battle state and events
│   ├── Announcements/
│   │   ├── ShopAnnouncer.cs      # Shop actions (roll, buy, sell, freeze)
│   │   ├── TeamAnnouncer.cs      # Team summaries and changes
│   │   ├── BattleAnnouncer.cs    # Battle event narration (MonoBehaviour)
│   │   └── MenuAnnouncer.cs      # Menu and lobby announcements
│   └── Patches/
│       ├── HangarPatches.cs      # Harmony patches for shop/hangar events
│       ├── BattlePatches.cs      # Harmony patches for battle events
│       ├── MenuPatches.cs        # Harmony patches for menu navigation
│       └── UIPatches.cs          # Harmony patches for UI elements
├── interop/                      # BepInEx-generated interop DLLs (not committed)
├── stubs/                        # Minimal compilation stubs (fallback)
├── tools/
│   ├── deploy.sh                 # Build and deploy to game directory
│   └── copy-interop.sh           # Copy interop DLLs from game
└── README.md
```

### Architecture

**Virtual Focus System:** The mod creates a virtual focus cursor independent of the game's UI. `FocusManager` organizes elements into named groups (e.g., "Shop", "Team", "Actions") that the user navigates with keyboard arrows and Tab. Each `FocusElement` has a label, optional detail text, info rows for scrollable content, and an activation callback.

**Page Scanning:** `MenuNavigator` detects page changes via Harmony patches on the game's `PageManager.Open` method. When a new page opens, it scans the page's Unity components to build focus groups. Specialized scanners exist for PackShop, Customize (with internal sub-page management), PetCustomizer, VersusCreator, Achievements, and more.

**Game State Tracking:** `GamePhaseTracker` monitors phase transitions (MainMenu, ModeSelect, Shop, Battle) and coordinates system behavior. State readers extract data from IL2CPP game objects using BepInEx interop.

**Dialog Polling:** The mod polls for overlay dialogs (alerts, pickers, choosers, subscription carts, deck viewers, sidebar) each frame since these don't trigger page change events. When detected, they temporarily replace focus groups and restore them on close.

**IL2CPP Considerations:**
- Custom MonoBehaviours must be registered with `ClassInjector.RegisterTypeInIl2Cpp<T>()` before use
- The game uses legacy `UnityEngine.Input` (not the new InputSystem)
- `BepInEx.Logging.Logger` must be fully qualified in files that also use `UnityEngine`

## Contributing

Contributions are welcome. Here's how to get started:

1. **Fork and clone** the repository
2. **Set up the build environment** following the [Building from Source](#building-from-source) instructions
3. **Generate an IL2CPP dump** for reference. The dump provides class definitions, field offsets, and method signatures for the game's types. Use [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) on the game's `GameAssembly.dll` to produce `dump.cs`.
4. **Make your changes** and test in-game with `./tools/deploy.sh`
5. **Submit a pull request** with a clear description of the changes

### Development Tips

- Read `BepInEx/LogOutput.log` after each test run. The mod logs all speech output, page changes, scan results, and errors.
- The game's types are in the `Spacewood.Unity` and `Spacewood.Core` namespaces. Use the IL2CPP dump (`dump.cs`) as a reference for field offsets and method signatures.
- Wrap all IL2CPP object access in try/catch blocks. IL2CPP references can become stale and throw `NullReferenceException` or `ObjectDisposedException`.
- Use `TryCast<T>()` to safely downcast IL2CPP objects (e.g., `page.TryCast<Spacewood.Unity.PackShop>()`).
- Test with NVDA running. Speech output is logged even without NVDA, but the actual user experience can only be verified with the screen reader active.

### Areas for Contribution

- Additional game mode support (new packs, events, seasonal content)
- Localization and multi-language screen reader support
- Support for other screen readers (JAWS, Narrator)
- Improved battle narration detail and pacing
- UI sound cues for navigation feedback

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).

## Acknowledgments

- [BepInEx](https://github.com/BepInEx/BepInEx) for the IL2CPP modding framework
- [NVDA](https://www.nvaccess.org/) for the open-source screen reader
- [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for IL2CPP type analysis
- [Team Wood Games](https://teamwoodgames.com/) for Super Auto Pets
