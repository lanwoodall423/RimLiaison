[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'validation-proof.ps1'
. $modulePath

function Assert-ValidationTrue {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Validation proof assertion failed: $Message"
    }
}

function Assert-ValidationEqual {
    param(
        $Actual,
        $Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ([string]$Actual -ne [string]$Expected) {
        throw "Validation proof assertion failed: $Message; expected '$Expected', received '$Actual'"
    }
}

$testCount = 0
$root = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-validation-proof-' + [Guid]::NewGuid().ToString('N'))
$proofRoot = Join-Path $root '.rimdev/validation-proofs'

function Write-ProofFixtureFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $path = Join-Path $root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $parent = Split-Path -Parent $path
    [void](New-Item -ItemType Directory -Force -Path $parent)
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
}

function Get-ProofFixtureFingerprint {
    return Get-ValidationStageFingerprint `
        -RepositoryRoot $root `
        -StageId 'rimliaison' `
        -SelectedTestIds @('rimliaison-tests')
}

function Assert-ProofMissAfterMutation {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $original = [IO.File]::ReadAllText($path)
    try {
        [IO.File]::WriteAllText($path, $original + "`nmutation", [Text.UTF8Encoding]::new($false))
        $mutated = Get-ProofFixtureFingerprint
        Assert-ValidationTrue (-not $mutated.Complete -or
            $null -eq (Get-ValidationProofRecord $root $mutated 'rimliaison' @('rimliaison-tests') $proofRoot)) `
            "relevant input '$RelativePath' invalidates the proof"
    }
    finally {
        [IO.File]::WriteAllText($path, $original, [Text.UTF8Encoding]::new($false))
    }
}

try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null

    # This fixture deliberately contains the complete known closure used by
    # the deterministic RimLiaison suite. The proof layer may miss when a
    # closure cannot be enumerated; it never turns an unknown closure into a hit.
    foreach ($directory in @(
            'src/RimLiaison.Cli',
            'src/RimContext.Core',
            'src/RimError.Core',
            'tests/RimLiaison.Tests',
            'tests/fixtures',
            'templates',
            'TestCatalog',
            'contracts',
            'scripts')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $root $directory) | Out-Null
    }

    foreach ($path in @(
            'RimLiaison.sln',
            'global.json',
            'Directory.Build.props',
            'packages.lock.json',
            'src/RimLiaison.Cli/RimLiaison.Cli.csproj',
            'src/RimContext.Core/RimContext.Core.csproj',
            'src/RimError.Core/RimError.Core.csproj',
            'tests/RimLiaison.Tests/RimLiaison.Tests.csproj',
            'scripts/validation-proof.ps1',
            'scripts/ci-validate.ps1')) {
        if ($path -like 'scripts/*') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ([IO.Path]::GetFileName($path))) -Destination (Join-Path $root $path) -Force
        } else {
            Write-ProofFixtureFile $path 'fixture'
        }
    }

    foreach ($path in @(
            'src/RimLiaison.Cli/Program.cs',
            'src/RimContext.Core/Dependency.cs',
            'src/RimError.Core/Dependency.cs',
            'tests/RimLiaison.Tests/Program.cs',
            'tests/fixtures/input.txt',
            'templates/AGENTS.md',
            'TestCatalog/rimtest.catalog.json',
            'TestCatalog/devbridge-test-recipe-list.json',
            'contracts/cross-stack-compatibility.json')) {
        Write-ProofFixtureFile $path 'fixture-v1'
    }
    Write-ProofFixtureFile 'docs/unrelated.txt' 'unrelated-v1'

    $baseline = Get-ProofFixtureFingerprint
    $testCount++
    Assert-ValidationTrue $baseline.Complete 'complete deterministic closure produces a fingerprint'
    Assert-ValidationTrue ($baseline.InputCount -gt 0) 'fingerprint records input count'

    $record = New-ValidationProofRecord `
        -Fingerprint $baseline `
        -StageId 'rimliaison' `
        -SelectedTestIds @('rimliaison-tests') `
        -Status 'pass'
    Assert-ValidationTrue (Save-ValidationProofRecord $root $record $proofRoot) 'PASS proof is stored'
    $testCount++

    $hit = Get-ValidationProofRecord $root $baseline 'rimliaison' @('rimliaison-tests') $proofRoot
    $testCount++
    Assert-ValidationTrue ($null -ne $hit) 'same inputs reuse the PASS proof'

    foreach ($relevantPath in @(
            'src/RimLiaison.Cli/Program.cs',
            'src/RimContext.Core/Dependency.cs',
            'src/RimError.Core/Dependency.cs',
            'src/RimLiaison.Cli/RimLiaison.Cli.csproj',
            'global.json',
            'Directory.Build.props',
            'packages.lock.json',
            'TestCatalog/rimtest.catalog.json',
            'TestCatalog/devbridge-test-recipe-list.json',
            'contracts/cross-stack-compatibility.json',
            'scripts/ci-validate.ps1',
            'scripts/validation-proof.ps1')) {
        Assert-ProofMissAfterMutation $relevantPath
        $testCount++
    }

    $unrelated = Join-Path $root 'docs/unrelated.txt'
    $unrelatedOriginal = [IO.File]::ReadAllText($unrelated)
    [IO.File]::WriteAllText($unrelated, $unrelatedOriginal + "`nunrelated-mutation", [Text.UTF8Encoding]::new($false))
    try {
        $unrelatedFingerprint = Get-ProofFixtureFingerprint
        $testCount++
        Assert-ValidationTrue ($null -ne (Get-ValidationProofRecord $root $unrelatedFingerprint 'rimliaison' @('rimliaison-tests') $proofRoot)) `
            'unrelated input outside the proven closure preserves reuse'
    }
    finally {
        [IO.File]::WriteAllText($unrelated, $unrelatedOriginal, [Text.UTF8Encoding]::new($false))
    }

    $corruptPath = Join-Path $proofRoot ([string]$baseline.ProofId + '.json')
    [IO.File]::WriteAllText($corruptPath, '{not-json', [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-ValidationTrue ($null -eq (Get-ValidationProofRecord $root $baseline 'rimliaison' @('rimliaison-tests') $proofRoot)) `
        'corrupt proof is a cache miss'

    foreach ($badStatus in @('fail', 'cancelled', 'incomplete', 'infrastructure')) {
        $badRecord = New-ValidationProofRecord `
            -Fingerprint $baseline `
            -StageId 'rimliaison' `
            -SelectedTestIds @('rimliaison-tests') `
            -Status $badStatus
        $badRecordPath = Join-Path $proofRoot ($badStatus + '.json')
        ($badRecord | ConvertTo-Json -Depth 8 -Compress) | Set-Content -LiteralPath $badRecordPath -Encoding utf8
        $testCount++
        Assert-ValidationTrue (-not (Test-ValidationProofRecord $badRecord $baseline 'rimliaison' @('rimliaison-tests'))) `
            "previous $badStatus result is not reusable"
    }

    $incomplete = $record | Select-Object *
    $incomplete.closureComplete = $false
    $testCount++
    Assert-ValidationTrue (-not (Test-ValidationProofRecord $incomplete $baseline 'rimliaison' @('rimliaison-tests'))) `
        'incomplete result is not reusable'

    $schemaChanged = $record | Select-Object *
    $schemaChanged.schemaVersion = 'rimliaison-validation-proof/v999'
    $testCount++
    Assert-ValidationTrue (-not (Test-ValidationProofRecord $schemaChanged $baseline 'rimliaison' @('rimliaison-tests'))) `
        'proof schema changes invalidate reuse'

    $live = Get-ValidationProofStageDefinition 'live-stack-smoke'
    $artifact = Get-ValidationProofStageDefinition 'artifact-freshness'
    $testCount += 2
    Assert-ValidationTrue (-not $live.Reusable) 'live stack stage is never offline-reusable'
    Assert-ValidationTrue (-not $artifact.Reusable) 'artifact freshness stage is never offline-reusable'

    # Exercise bounded deterministic eviction with valid compact records.
    $boundedRoot = Join-Path $root 'bounded-cache'
    New-Item -ItemType Directory -Force -Path $boundedRoot | Out-Null
    for ($index = 0; $index -lt 5; $index++) {
        $proof = $record | Select-Object *
        $proof.proofId = ('{0:x64}' -f ($index + 1))
        $proof.createdUtc = ([DateTime]::UtcNow.AddSeconds($index)).ToString('o')
        $proofPath = Join-Path $boundedRoot ($proof.proofId + '.json')
        ($proof | ConvertTo-Json -Depth 8 -Compress) | Set-Content -LiteralPath $proofPath -Encoding utf8
    }
    Prune-ValidationProofCache $boundedRoot 2 65536
    $testCount++
    Assert-ValidationTrue (@(Get-ChildItem -LiteralPath $boundedRoot -Filter '*.json').Count -le 2) 'proof record count is bounded with deterministic eviction'

    Write-Output ('Validation proof tests passed: {0}' -f $testCount)
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit 0
