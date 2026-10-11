# Better Radio

Better Radio improves Mon Bazou's built-in custom music player. It shows song filenames, keeps active radios in sync, remembers shuffle, reads external music folders and prepares large libraries without loading every song up front.

## Installation

1. Install the appropriate [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) build for your PC.
2. Download the latest Better Radio ZIP from [GitHub Releases](https://github.com/Skeptic043/MonBazouBetterRadio/releases/latest).
3. Close the game and extract the ZIP into the Mon Bazou game folder.
4. Start the game normally. The configuration file is created on first launch.

Keep the included `NLayer.dll` with the plugin.

## Features

- Shows the playing song's filename. Long names scroll across the radio display.
- Keeps active CD radios on the same song and playback position. Next, previous and shuffle are shared, while power, volume and FM remain individual.
- Adds shuffle to preset 6 in CD mode and remembers the setting across save changes and game restarts.
- Reads subfolders inside the game's `CDPlayer` folder and optional external music folders.
- Prepares the current and next song in the background instead of loading the entire library's audio before playback.
- Plays MP3, Ogg Vorbis and PCM WAV. AAC, M4A and MP4 audio use Windows Media Foundation when available.

## Controls

Use the normal radio power, source, volume and seek controls. In CD mode, preset `6` toggles shuffle.

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

Leave `MusicFolders` blank to use only `CDPlayer`. Both locations include subfolders. Multiple additional folders can be separated with `|`, for example `D:\Music|E:\More Music`. Use absolute paths without quotation marks. Edit `MusicFolders` while the game is closed. Changes made to the file while Mon Bazou is running are not reloaded and may be overwritten.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Music.MusicFolders` | Blank | Additional folders to read alongside `CDPlayer`. |
| `Music.Shuffle` | `false` | Remembered shuffle preference. Preset 6 updates this setting. |
| `Music.UseCustomLoader` | `true` | Enables recursive folders and background playback. Set to `false` and restart to use the original loader. |
| `Loading.LogTimings` | `false` | Enables aggregate loading diagnostics in the BepInEx log. |

## Updating and removing

To update Better Radio, close the game and replace the files in `BepInEx\plugins\RadioEnhancements`.

Better Radio does not add data to Mon Bazou save files. To remove it, close the game and delete `BepInEx\plugins\RadioEnhancements`.

## Compatibility and limitations

Requires BepInEx 5. Better Radio was tested on Windows. AAC, M4A and MP4 audio require Windows Media Foundation and an available decoder, so those formats are disabled under Wine and Proton. MP4 playback uses audio only. If a file has multiple audio tracks, Windows selects one automatically. Copy-protected files are unsupported. An OGG file must contain Vorbis audio, and a WAV file must use a supported PCM encoding.

Mods that replace the built-in custom music loader, radio seeking or preset behavior may conflict with Better Radio.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/MonBazouBetterRadio/issues) with the mod version, relevant settings and the relevant portion of `BepInEx/LogOutput.log`. Redact personal paths and other private information before posting.

[Download Better Radio](https://github.com/Skeptic043/MonBazouBetterRadio/releases/latest) · [Source code](https://github.com/Skeptic043/MonBazouBetterRadio) · [Changelog](CHANGELOG.md) · [Build instructions](docs/BUILDING.md) · [MIT License](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
