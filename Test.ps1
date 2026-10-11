param(
    [string]$CompilerPath = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    [switch]$CheckGameHooks,
    [switch]$PatchSmoke,
    [string]$GameManagedPath = 'C:\Steam Games\steamapps\common\MonBazou\Mon Bazou_Data\Managed',
    [string]$BepInExPath = "$env:USERPROFILE\.nuget\packages\bepinex.baselib\5.4.20\lib\net35\BepInEx.dll",
    [string]$HarmonyPath = "$env:USERPROFILE\.nuget\packages\harmonyx\2.7.0\lib\net45\0Harmony.dll",
    [string]$MonoModUtilsPath = "$env:USERPROFILE\.nuget\packages\monomod.utils\21.12.13.1\lib\net452\MonoMod.Utils.dll",
    [string]$RuntimeDetourPath = "$env:USERPROFILE\.nuget\packages\monomod.runtimedetour\21.12.13.1\lib\net452\MonoMod.RuntimeDetour.dll",
    [string]$CecilPath = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.4\lib\net40\Mono.Cecil.dll",
    [string]$NLayerPath = "$env:USERPROFILE\.nuget\packages\nlayer\1.16.0\lib\netstandard2.0\NLayer.dll"
)
$ErrorActionPreference = 'Stop'
$validation = Join-Path $PSScriptRoot '.local/validation'
New-Item -ItemType Directory -Force $validation | Out-Null
$output = Join-Path $validation 'CoreTests.exe'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
$sources = @('src/MusicLibraryTracker.cs', 'src/TrackDisplayController.cs', 'src/DisplayOwnership.cs', 'src/PlaybackPolicies.cs', 'tests/PlaybackPolicyTests.cs', 'tests/CoreTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$output" @sources 2>&1 | Tee-Object -FilePath (Join-Path $validation 'test-compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Test compiler failed with exit code $LASTEXITCODE." }
if (!(Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Test compiler did not create the executable.' }
& $output 2>&1 | Tee-Object -FilePath (Join-Path $validation 'core-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
$loadingOutput = Join-Path $validation 'LoadingTests.exe'
if (Test-Path -LiteralPath $loadingOutput) { Remove-Item -LiteralPath $loadingOutput }
$loadingSources = @('src/LoadingCoordinator.cs', 'src/LoadingSnapshot.cs', 'tests/LoadingTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$loadingOutput" @loadingSources 2>&1 | Tee-Object -FilePath (Join-Path $validation 'loading-test-compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Loading test compiler failed with exit code $LASTEXITCODE." }
& $loadingOutput 2>&1 | Tee-Object -FilePath (Join-Path $validation 'loading-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Loading tests failed with exit code $LASTEXITCODE." }
$startupOutput = Join-Path $validation 'StartupLoadingTests.exe'
if (Test-Path -LiteralPath $startupOutput) { Remove-Item -LiteralPath $startupOutput }
$startupSources = @('src/StartupLoadingSequence.cs', 'tests/StartupLoadingTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$startupOutput" @startupSources 2>&1 | Tee-Object -FilePath (Join-Path $validation 'startup-test-compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Startup loading compiler failed with exit code $LASTEXITCODE." }
& $startupOutput 2>&1 | Tee-Object -FilePath (Join-Path $validation 'startup-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Startup loading tests failed with exit code $LASTEXITCODE." }
$guardOutput = Join-Path $validation 'LoadingPlaybackGuardTests.exe'
if (Test-Path -LiteralPath $guardOutput) { Remove-Item -LiteralPath $guardOutput }
$guardSources = @('src/LoadingPlaybackGuard.cs', 'src/LoadingCoordinator.cs', 'src/LoadingSnapshot.cs', 'tests/LoadingPlaybackGuardTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$guardReferences = @($HarmonyPath) + (@('UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AudioModule.dll', 'netstandard.dll') | ForEach-Object { Join-Path $GameManagedPath $_ })
$guardReferenceArguments = $guardReferences | ForEach-Object { "/reference:$_" }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$guardOutput" @guardReferenceArguments @guardSources 2>&1 | Tee-Object -FilePath (Join-Path $validation 'guard-test-compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Loading playback guard compiler failed with exit code $LASTEXITCODE." }
& $guardOutput $GameManagedPath $HarmonyPath 2>&1 | Tee-Object -FilePath (Join-Path $validation 'guard-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Loading playback guard tests failed with exit code $LASTEXITCODE." }
$syncOutput = Join-Path $validation 'CdSyncTests.exe'
if (Test-Path -LiteralPath $syncOutput) { Remove-Item -LiteralPath $syncOutput }
$syncSources = @('src/PlaybackPolicies.cs', 'src/CdSyncTransport.cs', 'src/CdSyncSourceLease.cs', 'tests/CdSyncTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$syncOutput" @syncSources 2>&1 | Tee-Object -FilePath (Join-Path $validation 'sync-test-compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Shared CD test compiler failed with exit code $LASTEXITCODE." }
& $syncOutput 2>&1 | Tee-Object -FilePath (Join-Path $validation 'sync-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Shared CD tests failed with exit code $LASTEXITCODE." }
$customAdapterOutput = Join-Path $validation 'CustomCdSessionTests.exe'
if (Test-Path -LiteralPath $customAdapterOutput) { Remove-Item -LiteralPath $customAdapterOutput }
$customAdapterSources = @('src/CustomCdSession.cs', 'src/CdSyncSourceLease.cs', 'tests/CustomCdSessionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ "/out:$customAdapterOutput" @customAdapterSources
if ($LASTEXITCODE -ne 0) { throw "Custom CD adapter test compile failed: $LASTEXITCODE" }
& $customAdapterOutput 2>&1 | Tee-Object -FilePath (Join-Path $validation 'custom-adapter-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Custom CD adapter tests failed: $LASTEXITCODE" }
$customOutput = Join-Path $validation 'CustomMusicTests.exe'
if (Test-Path -LiteralPath $customOutput) { Remove-Item -LiteralPath $customOutput }
$nlayer = $NLayerPath
$customSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/Audio') -Filter '*.cs' | Sort-Object FullName | ForEach-Object FullName)
$customSources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter 'CustomMusic*.cs' | Sort-Object FullName | ForEach-Object FullName)
$customSources += @('tests/CustomMusicTests.cs', 'tests/Audio/CustomAudioDouble.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $CompilerPath /nologo /target:exe /warnaserror+ /nowarn:1701 "/out:$customOutput" "/reference:$nlayer" "/reference:$(Join-Path $GameManagedPath 'netstandard.dll')" @customSources
if ($LASTEXITCODE -ne 0) { throw "Custom music test compile failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $nlayer -Destination (Join-Path $validation 'NLayer.dll') -Force
& $customOutput (Join-Path $PSScriptRoot 'tests/Audio') 2>&1 | Tee-Object -FilePath (Join-Path $validation 'custom-music-tests.log')
if ($LASTEXITCODE -ne 0) { throw "Custom music tests failed: $LASTEXITCODE" }
if ($CheckGameHooks) {
    $hookOutput = Join-Path $validation 'HookSurfaceTests.exe'
    if (Test-Path -LiteralPath $hookOutput) { Remove-Item -LiteralPath $hookOutput }
    & $CompilerPath /nologo /target:exe /warnaserror+ "/out:$hookOutput" "/reference:$HarmonyPath" (Join-Path $PSScriptRoot 'tests/HookSurfaceTests.cs') (Join-Path $PSScriptRoot 'tests/PlaybackRefreshTests.cs') (Join-Path $PSScriptRoot 'tests/CdSyncHookTests.cs') (Join-Path $PSScriptRoot 'tests/HookCleanupTests.cs') (Join-Path $PSScriptRoot 'tests/ShufflePreferenceTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Hook harness compiler failed with exit code $LASTEXITCODE." }
    $smokeArguments = @()
    if ($PatchSmoke) { $smokeArguments += '--patch-smoke' }
    & $hookOutput (Join-Path $PSScriptRoot 'bin/Release/RadioEnhancements.dll') $GameManagedPath $BepInExPath $HarmonyPath $MonoModUtilsPath $CecilPath $RuntimeDetourPath @smokeArguments 2>&1 | Tee-Object -FilePath (Join-Path $validation 'hook-surfaces.log')
    if ($LASTEXITCODE -ne 0) { throw "Hook surface checks failed with exit code $LASTEXITCODE." }
    Get-FileHash -LiteralPath (Join-Path $GameManagedPath 'Assembly-CSharp.dll') -Algorithm SHA256 | Format-List | Out-File (Join-Path $validation 'game-assembly-hash.txt')
}
