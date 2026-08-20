[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'devbridge-binary-cache.ps1'
$pwshPath = (Get-Command pwsh -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Path
$testCount = 0
$root = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-devbridge-cache-' + [Guid]::NewGuid().ToString('N'))

function Assert-CacheTrue {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "DevBridge binary cache assertion failed: $Message"
    }
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Operation
    )

    $output = @(& git @Arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "DevBridge binary cache Git command failed: $Operation (exit code $exitCode)"
    }

    return $output
}

function Write-CacheFixtureFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $path = Join-Path $root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $parent = Split-Path -Parent $path
    [void](New-Item -ItemType Directory -Force -Path $parent)
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
}

function Invoke-CacheIdentity {
    param([Parameter(Mandatory = $true)][string]$PinnedRevision)

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $scriptPath,
        '-DevBridgeRoot', $root,
        '-PinnedRevision', $PinnedRevision,
        '-RunnerOs', 'Windows',
        '-RunnerArch', 'X64',
        '-DotnetSdk', '8.0.424',
        '-Configuration', 'Release',
        '-TargetFramework', 'net8.0',
        '-Json')
    $raw = @(& $pwshPath @arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "DevBridge binary cache identity command failed (exit code $exitCode)"
    }

    $text = ($raw -join [Environment]::NewLine).Trim()
    Assert-CacheTrue (-not [string]::IsNullOrWhiteSpace($text)) 'identity command emitted JSON'
    return $text | ConvertFrom-Json -Depth 10
}

function Commit-CacheFixture {
    param([Parameter(Mandatory = $true)][string]$Message)

    Invoke-GitChecked @('-C', $root, 'add', '--', '.') 'git add fixture'
    Invoke-GitChecked @('-C', $root, 'commit', '--quiet', '-m', $Message) "git commit $Message"
    return ([string](Invoke-GitChecked @('-C', $root, 'rev-parse', 'HEAD') 'git rev-parse fixture')).Trim()
}

try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    foreach ($directory in @(
            'Source/Coordinator',
            'Source/Coordinator.Core',
            'Source/FakeRimWorld')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $root $directory) | Out-Null
    }

    Write-CacheFixtureFile 'global.json' '{"sdk":{"version":"8.0.424"}}'
    Write-CacheFixtureFile 'Source/Directory.Build.props' '<Project><PropertyGroup><Deterministic>true</Deterministic></PropertyGroup></Project>'
    Write-CacheFixtureFile 'Source/Directory.Build.targets' '<Project />'
    Write-CacheFixtureFile 'Source/Coordinator/DevBridge.Coordinator.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Write-CacheFixtureFile 'Source/Coordinator/Program.cs' 'class Coordinator {}'
    Write-CacheFixtureFile 'Source/Coordinator.Core/DevBridge.Coordinator.Core.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Write-CacheFixtureFile 'Source/Coordinator.Core/Core.cs' 'class Core {}'
    Write-CacheFixtureFile 'Source/FakeRimWorld/FakeRimWorld.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Write-CacheFixtureFile 'Source/FakeRimWorld/Program.cs' 'class FakeRimWorld {}'
    Write-CacheFixtureFile 'Source/BridgeTools/packages.lock.json' '{"version":1}'

    Invoke-GitChecked @('-C', $root, 'init', '--quiet') 'git init'
    Invoke-GitChecked @('-C', $root, 'config', 'user.email', 'devbridge-cache@example.invalid') 'git config user.email'
    Invoke-GitChecked @('-C', $root, 'config', 'user.name', 'DevBridge cache tests') 'git config user.name'
    $firstRevision = Commit-CacheFixture 'initial cache inputs'

    $first = Invoke-CacheIdentity $firstRevision
    $second = Invoke-CacheIdentity $firstRevision
    $testCount++
    Assert-CacheTrue ($first.cacheKey -eq $second.cacheKey) 'identical pinned inputs derive the same key'
    $testCount++
    Assert-CacheTrue ($first.cacheKey.Contains($firstRevision.ToLowerInvariant(), [StringComparison]::Ordinal)) `
        'cache key includes the exact pinned revision'
    $testCount++
    Assert-CacheTrue ($first.cacheKey.Contains('windows-x64-dotnet-8.0.424-release-net8.0', [StringComparison]::OrdinalIgnoreCase)) `
        'cache key includes runner and SDK/build assumptions'
    $testCount++
    Assert-CacheTrue ($first.inputCount -ge 9) 'cache identity includes source and build-import inputs'

    Write-CacheFixtureFile 'Source/Coordinator/Program.cs' 'class Coordinator { static int Version = 2; }'
    $secondRevision = Commit-CacheFixture 'source input change'
    $changedSource = Invoke-CacheIdentity $secondRevision
    $testCount++
    Assert-CacheTrue ($first.cacheKey -ne $changedSource.cacheKey) 'source changes invalidate the binary key'

    Write-CacheFixtureFile 'Source/Directory.Build.props' '<Project><PropertyGroup><Deterministic>false</Deterministic></PropertyGroup></Project>'
    $thirdRevision = Commit-CacheFixture 'build import change'
    $changedBuild = Invoke-CacheIdentity $thirdRevision
    $testCount++
    Assert-CacheTrue ($changedSource.cacheKey -ne $changedBuild.cacheKey) 'build-import changes invalidate the binary key'

    $wrongPin = '0000000000000000000000000000000000000000'
    $wrongOutput = @(& $pwshPath -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
        -DevBridgeRoot $root -PinnedRevision $wrongPin -RunnerOs Windows -RunnerArch X64 `
        -DotnetSdk 8.0.424 -Configuration Release -TargetFramework net8.0 -Json 2>$null)
    $wrongExitCode = $LASTEXITCODE
    $testCount++
    Assert-CacheTrue ($wrongExitCode -ne 0) 'a nonmatching pin is a cache miss/failure'

    Write-Output ('DevBridge binary cache tests passed: {0}' -f $testCount)
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$global:LASTEXITCODE = 0
exit 0
