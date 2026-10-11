# Synthetic audio fixtures

All audio is generated noise or sine waves. No player music or commercial recordings are included. `Generate-Fixtures.ps1` uses FFmpeg 8.1.1 to reproduce the AAC, M4A and MP4 fixtures. Add `-IncludeExistingFormats` to regenerate MP3 and Ogg Vorbis too. FFmpeg is only a fixture generation tool and is not needed to build, run the tests with the checked-in fixtures, or play music in the mod.

- `stereo-noise.mp3` uses five seconds of 44100 Hz white noise with seed 173, stereo, libmp3lame at 128 kbps.
- `sine-stereo.ogg` uses five seconds of a 330 Hz tone at 44100 Hz, stereo, libvorbis quality 3.
- `sine-stereo.aac` and `sine-stereo.m4a` use the same tone with AAC at 128 kbps in ADTS and MP4 containers.
- `sine-alac.m4a` uses the same tone encoded as Apple Lossless.
- `video-audio.mp4` contains a tiny black H.264 video and a 330 Hz AAC track.
- `video-two-audio.mp4` adds separate 330 Hz and 880 Hz AAC tracks, in that container order, to verify that only one audio stream is played. Media Foundation chooses the stream automatically, and its source-reader order can differ from container order. No track selector is provided.
- `video-only.mp4` has no audio and must be rejected.
- `video-long-tail.mp4` has twelve seconds of video and five seconds of audio. Playback must advance when the selected audio ends.

WAV fixtures are synthesized during the test run. Container metadata can make regenerated files differ byte-for-byte even when their content and generation recipe match.
