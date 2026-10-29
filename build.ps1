# Builds SuperflightCoop.dll against the game's own .NET 3.5 (Mono 2.0) assemblies and BepInEx 5, with the
# Roslyn csc from Visual Studio Build Tools. No NuGet, no SDK project: the game folder is the reference set.
#
#   .\build.ps1 -GameDir <folder with superflight.exe and BepInEx\> [-Deploy <folder>[,<folder>]]
#
# -Deploy copies the DLL into <folder>\BepInEx\plugins\SuperflightCoop\ for every folder given.
param(
    [string]$GameDir = $env:SUPERFLIGHT_GAMEDIR,
    [string[]]$Deploy = @(),
    [string]$Csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw "pass -GameDir <folder with superflight.exe and BepInEx> or set SUPERFLIGHT_GAMEDIR" }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = Join-Path $GameDir 'superflight_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
foreach ($p in $Csc, $managed, $core) { if (-not (Test-Path $p)) { throw "missing: $p" } }

$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.PhysicsModule.dll",
    "$managed\UnityEngine.IMGUIModule.dll", "$managed\UnityEngine.ParticleSystemModule.dll", "$managed\UnityEngine.ScreenCaptureModule.dll", "$managed\UnityEngine.InputModule.dll", "$managed\UnityEngine.TextRenderingModule.dll",
    "$managed\Assembly-CSharp.dll", "$managed\Assembly-CSharp-firstpass.dll",
    "$core\BepInEx.dll", "$core\0Harmony.dll"
)
$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$dll = Join-Path $out 'SuperflightCoop.dll'
$version = (& cmd /c "git -C ""$root"" rev-parse --short HEAD 2>nul"); if (-not $version) { $version = 'dev' }
$verFile = Join-Path $out 'BuildInfo.cs'
"namespace SuperflightCoop { internal static class BuildInfo { public const string Commit = ""$version""; } }" | Set-Content $verFile -Encoding ascii

$args = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/debug:portable', '/langversion:7.3',
          '/nowarn:1701,1702', "/out:$dll") + ($refs | ForEach-Object { "/r:$_" }) + $sources + $verFile
& $Csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
"built $dll ($((Get-Item $dll).Length) bytes, commit $version)"

foreach ($d in $Deploy) {
    $dst = Join-Path $d 'BepInEx\plugins\SuperflightCoop'
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item $dll $dst -Force
    Copy-Item (Join-Path $out 'SuperflightCoop.pdb') $dst -Force -ErrorAction SilentlyContinue
    "deployed to $dst"
}
