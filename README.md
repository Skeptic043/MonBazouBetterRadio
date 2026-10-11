# Better Radio

Better Radio improves Mon Bazou's custom music with scrolling song names, shared playback between radios, remembered shuffle and background music loading.

## Installation

1. Install [BepInEx 5 for Windows x64](https://github.com/BepInEx/BepInEx/releases/latest). This mod uses BepInEx 5, not BepInEx 6.
2. Download the Better Radio ZIP from its Nexus Mods page.
3. Close the game and extract the ZIP into the Mon Bazou game folder, merging the included `BepInEx` folder.
4. Start the game normally. The configuration file is created on first launch.

Keep the included `NLayer.dll` and license notices with the plugin. The installation folder and DLL retain the name `RadioEnhancements` so existing installations can be updated in place. Replace that folder's files when updating rather than installing a second copy elsewhere.

## Features

- Displays the playing song's filename without its extension. Long names scroll across the radio display.
- Keeps active CD radios on the same song and playback position. Next, previous and shuffle are shared, while power, volume and FM remain individual.
- Adds shuffle to preset 6 in CD mode and remembers the setting across save changes and game restarts.
- Reads nested folders inside the game's `CDPlayer` folder and optional external music folders.
- Prepares songs in the background instead of loading the entire library's audio before playback.
- Plays MP3, Ogg Vorbis and PCM WAV, with AAC, M4A and MP4 audio through Windows Media Foundation when available.

## Controls

Use the normal radio power, source, volume and seek controls. In CD mode, preset **6** toggles shuffle. With shuffle off, songs play sequentially. With shuffle on, songs are selected without repeats within a cycle.

Previous restarts the current song after its first two seconds. Before that, it goes to the preceding song or back through shuffle history. Changing shuffle does not immediately skip the current song. FM presets and temporary volume displays retain their normal behavior.

## Music folders and configuration

The native music folder is always included:

```text
%USERPROFILE%\AppData\LocalLow\Santa Goat\Mon Bazou\CDPlayer
```

To add an external music folder, close the game and edit:

```text
BepInEx\config\com.skeptic043.monbazou.radioenhancements.cfg
```

For example:

```ini
[Music]
MusicFolders = D:\Music
```

Leave `MusicFolders` blank to use only `CDPlayer`. Both locations include subfolders. Multiple additional folders can be separated with `|`, for example `D:\Music|E:\More Music`. Use absolute paths without quotation marks. Music is read directly from those locations.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Music.MusicFolders` | Blank | Additional folders to read alongside `CDPlayer`. |
| `Music.Shuffle` | `false` | Remembered shuffle preference. Preset 6 updates this setting. |
| `Music.UseCustomLoader` | `true` | Enables recursive folders and background playback. Set to `false` and restart to use the original loader. |
| `Loading.LogTimings` | `true` | Writes loading diagnostics to the BepInEx log. |

The remaining `Loading` options apply to the original-loader fallback: `ConcurrentLoads` defaults to `1` and accepts `0` through `16`, `ReuseUnchangedLibrary` defaults to `true`, and `PreloadOnGameLoad` defaults to `true`. The normal custom loader indexes music automatically when loading a game. The pause menu's **LOAD** button rescans the music folders.

Shuffle is a global preference shared across saves. The mod does not save a song position or shuffle history across game sessions.

## Compatibility and limitations

Requires the Windows version of Mon Bazou and BepInEx 5. AAC-family formats require Windows Media Foundation and an available decoder. They are disabled under Wine and Proton. MP4 playback uses audio only. If a file has multiple audio tracks, Windows selects one automatically. Copy-protected files are unsupported. An OGG file must contain Vorbis audio, and a WAV file must use a supported PCM encoding.

The original-loader fallback uses the game's original folder and format handling, so external folders and the additional formats require `UseCustomLoader = true`.

Song titles come from filenames. Embedded title tags are not read.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/MonBazouBetterRadio/issues) with the mod version, relevant settings and the relevant portion of `BepInEx/LogOutput.log`. Redact personal paths and other private information before posting.

[Source code](https://github.com/Skeptic043/MonBazouBetterRadio) · [Changelog](CHANGELOG.md) · [Build instructions](docs/BUILDING.md) · [MIT License](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
