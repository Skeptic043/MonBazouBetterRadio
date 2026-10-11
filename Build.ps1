param(
    [string]$GameManagedPath = 'C:\Steam Games\steamapps\common\MonBazou\Mon Bazou_Data\Managed',
    [string]$BepInExPath = "$env:USERPROFILE\.nuget\packages\bepinex.baselib\5.4.20\lib\net35\BepInEx.dll",
    [string]$HarmonyPath = "$env:USERPROFILE\.nuget\packages\harmonyx\2.7.0\lib\net45\0Harmony.dll",
    [string]$CompilerPath = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    [string]$NLayerPath = "$env:USERPROFILE\.nuget\packages\nlayer\1.16.0\lib\netstandard2.0\NLayer.dll"
)
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot 'bin/Release'
$buildDirectory = Join-Path $PSScriptRoot '.local/build'
New-Item -ItemType Directory -Force $outputDirectory, $buildDirectory | Out-Null
$output = Join-Path $outputDirectory 'RadioEnhancements.dll'
# Delete only the exact previous output, so failed builds cannot pass as fresh binaries.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
$references = @($BepInExPath, $HarmonyPath, $NLayerPath)
$references += @('UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll', 'netstandard.dll') | ForEach-Object { Join-Path $GameManagedPath $_ }
foreach ($path in @($CompilerPath) + $references) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing build input: $path" }
}
# NLayer targets netstandard 2.0; the game supplies its compatible 2.1 facade.
# Suppress only the legacy compiler's facade-version unification warning.
$arguments = @('/nologo', '/target:library', '/optimize+', '/warnaserror+', '/nowarn:1701', "/out:$output")
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -Recurse | Sort-Object FullName | ForEach-Object FullName
& $CompilerPath @arguments 2>&1 | Tee-Object -FilePath (Join-Path $buildDirectory 'compiler.log')
if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE." }
if (!(Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Compiler returned without a new DLL.' }
Copy-Item -LiteralPath $NLayerPath -Destination (Join-Path $outputDirectory 'NLayer.dll') -Force
Get-FileHash -LiteralPath $output -Algorithm SHA256
