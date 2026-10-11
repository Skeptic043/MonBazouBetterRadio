# Building Better Radio

Build on Windows with a local Mon Bazou installation. The repository does not include game assemblies or BepInEx binaries.

## Dependencies

`Build.ps1` uses the .NET Framework C# compiler included with Windows and compiles against the game's Unity assemblies. It expects these NuGet packages in the normal user package cache:

| Package | Version | Assembly directory |
| --- | --- | --- |
| `BepInEx.BaseLib` | `5.4.20` | `lib/net35` |
| `HarmonyX` | `2.7.0` | `lib/net45` |
| `NLayer` | `1.16.0` | `lib/netstandard2.0` |
| `MonoMod.Utils` | `21.12.13.1` | `lib/net452` |
| `MonoMod.RuntimeDetour` | `21.12.13.1` | `lib/net452` |
| `Mono.Cecil` | `0.11.4` | `lib/net40` |

The last three packages are used by the native-hook inspection tests. Restore these versions through NuGet or supply their assembly paths using the script parameters. Runtime installation uses BepInEx 5.4.23.5. NLayer is the only additional DLL included with the mod.

## Build and test

Run from the repository root in PowerShell:

```powershell
./Build.ps1 -GameManagedPath 'C:\Games\MonBazou\Mon Bazou_Data\Managed'
./Test.ps1 -CheckGameHooks -GameManagedPath 'C:\Games\MonBazou\Mon Bazou_Data\Managed'
```

`Build.ps1` accepts `BepInExPath`, `HarmonyPath`, `NLayerPath` and `CompilerPath` overrides. `Test.ps1` also accepts the hook-test dependency paths. The output is `bin/Release/RadioEnhancements.dll` with `NLayer.dll` beside it. The historical assembly name and plugin ID preserve update and configuration continuity.

Tests cover library identity, displays, shuffle, loading, transport, decoders and source lifecycle. `-CheckGameHooks` inspects the installed game and compiled plugin. These checks do not start the game or establish audible playback. Generated test executables and logs go into the ignored `.local` directory.

See [audio fixtures](../tests/Audio/README.md) for synthetic fixture provenance and decoder test requirements.

## Packaging

Run `./Package.ps1` after building. It creates the candidate archive in `artifacts`, rejects an existing output and verifies a fresh extraction against the allowlist and source file hashes. Use `-OutputPath` for another archive path.

The player ZIP contains the compiled plugin, NLayer, player documentation and the required license notices. It excludes source tests, game assemblies, configurations, music and generated test output. Distribute the complete plugin folder so the dependency and notices remain beside the DLL.

Player downloads are distributed through GitHub Releases and Nexus Mods. The repository also hosts source and documentation.
