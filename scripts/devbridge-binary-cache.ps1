[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DevBridgeRoot,
    [Parameter(Mandatory = $true)][string]$PinnedRevision,
    [Parameter(Mandatory = $true)][string]$RunnerOs,
    [Parameter(Mandatory = $true)][string]$RunnerArch,
    [Parameter(Mandatory = $true)][string]$DotnetSdk,
    [string]$Configuration = 'Release',
    [string]$TargetFramework = 'net8.0',
    [string]$GitHubOutputPath,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Test-CacheInputPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ($RelativePath -match '(^|/)(bin|obj)(/|$)') {
        return $false
    }

    return $RelativePath -eq 'global.json' -or
        $RelativePath.StartsWith('Source/', [StringComparison]::OrdinalIgnoreCase) -or
        $RelativePath -match '(^|/)Directory\.[^/]+$' -or
        $RelativePath -match '(^|/)(NuGet\.config|nuget\.config|packages\.lock\.json)$'
}

function Require-CacheInput {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "DEVBRIDGE_BINARY_CACHE_INPUT_MISSING:$RelativePath"
    }
}

function Assert-SafeToken {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z0-9._-]+$') {
        throw "DEVBRIDGE_BINARY_CACHE_TOKEN_INVALID:$Name"
    }
}

try {
    $root = [IO.Path]::GetFullPath($DevBridgeRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "DEVBRIDGE_BINARY_CACHE_ROOT_MISSING:$root"
    }

    if ($PinnedRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'DEVBRIDGE_BINARY_CACHE_PIN_INVALID'
    }
    $pinned = $PinnedRevision.ToLowerInvariant()
    Assert-SafeToken 'runner-os' $RunnerOs
    Assert-SafeToken 'runner-arch' $RunnerArch
    Assert-SafeToken 'dotnet-sdk' $DotnetSdk
    Assert-SafeToken 'configuration' $Configuration
    Assert-SafeToken 'target-framework' $TargetFramework

    $headOutput = @(& git -c "safe.directory=$root" -C $root rev-parse HEAD 2>$null)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0 -or $headOutput.Count -eq 0) {
        throw 'DEVBRIDGE_BINARY_CACHE_CHECKOUT_INVALID'
    }
    $head = ([string]$headOutput[0]).Trim().ToLowerInvariant()
    if ($head -ne $pinned) {
        throw "DEVBRIDGE_BINARY_CACHE_PIN_MISMATCH:expected=$pinned;actual=$head"
    }

    $statusOutput = @(& git -c "safe.directory=$root" -C $root status --porcelain --untracked-files=all 2>$null)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        throw 'DEVBRIDGE_BINARY_CACHE_STATUS_UNAVAILABLE'
    }
    if ($statusOutput.Count -gt 0 -and
        -not [string]::IsNullOrWhiteSpace((($statusOutput -join "`n").Trim()))) {
        throw 'DEVBRIDGE_BINARY_CACHE_CHECKOUT_DIRTY'
    }

    $requiredInputs = @(
        'global.json',
        'Source/Directory.Build.props',
        'Source/Directory.Build.targets',
        'Source/Coordinator/DevBridge.Coordinator.csproj',
        'Source/Coordinator.Core/DevBridge.Coordinator.Core.csproj',
        'Source/FakeRimWorld/FakeRimWorld.csproj')
    foreach ($requiredInput in $requiredInputs) {
        Require-CacheInput $root $requiredInput
    }

    $inputFiles = @(
        Get-ChildItem -LiteralPath $root -File -Recurse -Force |
            ForEach-Object {
                $relative = Get-RelativePath $root $_.FullName
                if (Test-CacheInputPath $relative) {
                    [pscustomobject]@{
                        RelativePath = $relative
                        FullPath = $_.FullName
                    }
                }
            } |
            Sort-Object RelativePath)
    if ($inputFiles.Count -eq 0) {
        throw 'DEVBRIDGE_BINARY_CACHE_INPUTS_EMPTY'
    }

    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $encoding = [Text.UTF8Encoding]::new($false)
    try {
        foreach ($inputFile in $inputFiles) {
            $hasher.AppendData($encoding.GetBytes($inputFile.RelativePath + "`0"))
            $hasher.AppendData([IO.File]::ReadAllBytes($inputFile.FullPath))
        }
        $inputHash = ([Convert]::ToHexString($hasher.GetHashAndReset())).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }

    $cacheKey = 'rimliaison-devbridge2-binaries-v1-' +
        $RunnerOs.ToLowerInvariant() + '-' +
        $RunnerArch.ToLowerInvariant() + '-dotnet-' +
        $DotnetSdk + '-' +
        $Configuration + '-' +
        $TargetFramework + '-' +
        $pinned + '-' +
        $inputHash
    $identity = [ordered]@{
        schemaVersion = 'rimliaison-devbridge-binary-cache/v1'
        cacheKey = $cacheKey
        pinnedRevision = $pinned
        runnerOs = $RunnerOs
        runnerArch = $RunnerArch
        dotnetSdk = $DotnetSdk
        configuration = $Configuration
        targetFramework = $TargetFramework
        inputHash = $inputHash
        inputCount = $inputFiles.Count
        requiredOutputs = @(
            'Source/Coordinator/bin/Release/net8.0/DevBridge.Coordinator.exe',
            'Source/FakeRimWorld/bin/Release/net8.0/DevBridge.FakeRimWorld.exe')
    }

    if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
        $output = @(
            'cache_key=' + $cacheKey,
            'cache_input_hash=' + $inputHash,
            'cache_input_count=' + $inputFiles.Count)
        [IO.File]::AppendAllText(
            $GitHubOutputPath,
            (($output -join [Environment]::NewLine) + [Environment]::NewLine),
            [Text.UTF8Encoding]::new($false))
    }

    $global:LASTEXITCODE = 0
    if ($Json) {
        Write-Output ($identity | ConvertTo-Json -Depth 8 -Compress)
    } else {
        Write-Output ("DevBridge binary cache identity: key={0}; inputs={1}" -f $cacheKey, $inputFiles.Count)
    }
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
