# Changelog

## 1.0.0

Initial release.

- Shows the playing song's filename. Long names scroll across the radio display.
- Keeps active CD radios on the same song and playback position. Next, previous and shuffle are shared, while power, volume and FM remain individual.
- Adds shuffle to preset 6 in CD mode and remembers the setting across save changes and game restarts.
- Reads subfolders inside the game's `CDPlayer` folder and optional external music folders.
- Prepares the current and next song in the background instead of loading the entire library's audio before playback.
- Plays MP3, Ogg Vorbis and PCM WAV. AAC, M4A and MP4 audio use Windows Media Foundation when available.
- Includes optional loading diagnostics, disabled by default.
