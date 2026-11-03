param([string]$DependencyDirectory,[switch]$HostOnly)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    if (!$HostOnly) {
        $deps=Join-Path $repo 'Dependencies'
        $files=@('HacknetPathfinder.exe','FNA.dll','BepInEx.Core.dll','BepInEx.Hacknet.dll','0Harmony.dll','Mono.Cecil.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll','PathfinderAPI.dll')
        if($DependencyDirectory){
            $source=(Resolve-Path -LiteralPath $DependencyDirectory).Path
            New-Item -ItemType Directory -Path $deps -Force|Out-Null
            foreach($name in $files){Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $deps $name) -Force}
        }
        foreach($name in $files){if(!(Test-Path -LiteralPath (Join-Path $deps $name))){throw "Missing development reference: $name. See docs/build.md."}}
    }
    $projects=@('Host','Tests')
    if(!$HostOnly){$projects+=@('Plugin','RuntimeTests','TestScenario')}
    foreach($part in $projects){
        $project="HacknetPathfinderBridge/$part/HacknetPathfinderBridge.$part.csproj"
        & dotnet restore $project --configfile (Join-Path $repo 'NuGet.Config')
        if($LASTEXITCODE-ne 0){throw "Restore failed: $part"}
        & dotnet build $project -c Release --no-restore
        if($LASTEXITCODE-ne 0){throw "Build failed: $part"}
    }
    & dotnet 'HacknetPathfinderBridge/Tests/bin/Release/net10.0/HacknetPathfinderBridge.Tests.dll'
    if($LASTEXITCODE-ne 0){throw 'Host tests failed'}
    if(!$HostOnly){
        & 'HacknetPathfinderBridge/RuntimeTests/bin/Release/net472/HacknetPathfinderBridge.RuntimeTests.exe'
        if($LASTEXITCODE-ne 0){throw 'Runtime tests failed'}
    }
} finally {Pop-Location}
