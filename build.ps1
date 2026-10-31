# Builds the whole solution (Interstellar.dll, InterstellarFuelSwitch.dll, Interstellar_Redist.dll, PhotonSail.dll)
# with the .NET SDK's MSBuild, without Visual Studio or the .NET Framework developer packs.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1            # build
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Deploy    # build and copy the DLLs into the game (KSP must be closed)
#
# Needs: .NET SDK (dotnet), and the KSP install referenced by the HintPaths in the .csproj files.
# Reference assemblies for .NET Framework 4.7.2 and 4.8 come from the NuGet cache; if they are missing,
# any SDK-style project targeting net472/net48 restores them (or run: dotnet new classlib -f net48; dotnet restore).
param(
    [switch]$Deploy,
    [string]$Ksp = 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program',
    [string]$Backup = 'C:\Games\ClaudeKSP\save_backups\WarpPlugin_1.29.6_ckan'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# One folder holding both framework versions: FNPlugin targets 4.8 (Waterfall.dll is built for 4.8), the rest 4.7.2.
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
$refRoot = Join-Path $root '.refasm'
$refFw = Join-Path $refRoot '.NETFramework'
New-Item -ItemType Directory -Force $refFw | Out-Null
foreach ($v in @('472', '48')) {
    $src = Get-ChildItem (Join-Path $nuget "microsoft.netframework.referenceassemblies.net$v") -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $src) { throw "reference assemblies for net$v not found in $nuget" }
    $fwDir = Get-ChildItem (Join-Path $src.FullName 'build\.NETFramework') -Directory | Select-Object -First 1
    $link = Join-Path $refFw $fwDir.Name
    if (-not (Test-Path $link)) { cmd /c mklink /J "$link" "$($fwDir.FullName)" | Out-Null }
}

dotnet msbuild (Join-Path $root 'WarpPlugin.sln') -p:Configuration=Release "-p:TargetFrameworkRootPath=$refRoot" -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if ($Deploy) {
    if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { throw 'KSP is running: the DLLs are locked' }
    $gd = Join-Path $Ksp 'GameData'
    $gameWarp = Join-Path $gd 'WarpPlugin'

    # First deploy: keep the CKAN-installed WarpPlugin (release 1.29.6) to roll back to.
    $backup = $Backup
    if ((Test-Path $gameWarp) -and -not (Test-Path $backup)) {
        robocopy $gameWarp $backup /E /NFL /NDL /NJH /NJS /NP | Out-Null
        Write-Host "backup of the installed WarpPlugin: $backup"
    }

    # Configs, models, localization: copy what changed, never delete (the game keeps its own @thumbs there).
    robocopy (Join-Path $root 'GameData\WarpPlugin') $gameWarp /E /NFL /NDL /NJH /NJS /NP /XD '@thumbs' | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }

    # The committed GameData DLL is the old upstream binary: put the fresh build over it.
    Copy-Item (Join-Path $root 'FNPlugin\bin\Release\Interstellar.dll') (Join-Path $gameWarp 'Plugins\') -Force
    Copy-Item (Join-Path $root 'FNPlugin\bin\Release\Interstellar.pdb') (Join-Path $gameWarp 'Plugins\') -Force
    # Interstellar.dll needs the Interstellar_Redist.dll built with it (fields added after 1.29.6, e.g.
    # ResourceBuffers.MaxProcessingDeltaTime): an old one fails part compilation with MissingFieldException.
    if (Test-Path (Join-Path $gd 'Interstellar_Redist.dll')) {
        $redistBackup = Join-Path $backup '..\Interstellar_Redist.dll.1.29.6'
        if (-not (Test-Path $redistBackup)) { Copy-Item (Join-Path $gd 'Interstellar_Redist.dll') $redistBackup }
    }
    Copy-Item (Join-Path $root 'Redist\bin\Release\Interstellar_Redist.dll') $gd -Force

    # Module Manager must re-run the patches.
    Remove-Item (Join-Path $gd 'ModuleManager.ConfigCache'), (Join-Path $gd 'ModuleManager.ConfigSHA'), (Join-Path $gd 'ModuleManager.Physics'), (Join-Path $gd 'ModuleManager.TechTree') -ErrorAction SilentlyContinue
    Write-Host "deployed to $gameWarp"
}
