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

    # This code path must remain compatible with the .NET Framework runtime
    # used by built-in Windows PowerShell 5.1 (also valid under PowerShell 7).
    # A canonical root/path prefix collision must never count as containment.
    Write-ProofFixtureFile 'ps51-compat/nested/IDENTITY.TXT' 'abc'
    $compatRoot = Join-Path $root 'ps51-compat'
    $compatFile = Join-Path $compatRoot 'nested/IDENTITY.TXT'
    $compatSibling = Join-Path $root 'ps51-compat-sibling'
    [void](New-Item -ItemType Directory -Force -Path $compatSibling)
    $outsideFile = Join-Path $compatSibling 'outside.txt'
    [IO.File]::WriteAllText($outsideFile, 'outside', [Text.UTF8Encoding]::new($false))

    $testCount++
    Assert-ValidationTrue -Condition (
        (Test-ValidationPathWithin -RepositoryRoot $compatRoot -Candidate $compatFile) -and
        (Test-ValidationPathWithin -RepositoryRoot $compatRoot -Candidate $compatRoot) -and
        -not (Test-ValidationPathWithin -RepositoryRoot $compatRoot -Candidate $outsideFile)
    ) -Message 'path containment rejects same-prefix sibling and accepts only root or descendants'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationRelativePath -RepositoryRoot $compatRoot -Path $compatFile) -Expected 'nested/identity.txt' -Message 'relative path is normalized and case stable'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationRelativePath -RepositoryRoot $compatRoot -Path $compatRoot) -Expected '.' -Message 'exact repository root has safe relative identity'

    $outsideRejected = $false
    try {
        [void](ConvertTo-ValidationRelativePath -RepositoryRoot $compatRoot -Path (
                Join-Path $compatRoot '../ps51-compat-sibling/outside.txt'))
    }
    catch {
        $outsideRejected = $_.Exception.Message.Contains('escaped the repository root')
    }
    $testCount++
    Assert-ValidationTrue -Condition $outsideRejected -Message 'relative path conversion rejects normalized parent traversal'

    $testCount++
    Assert-ValidationEqual -Actual (Get-ValidationFileSha256 $compatFile) -Expected 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad' -Message 'PowerShell 5.1 compatible SHA-256 matches known lowercase hash vector'

    $testCount++
    Assert-ValidationTrue -Condition (
        Test-ValidationPathWithin -RepositoryRoot ([IO.Path]::GetPathRoot($compatRoot)) -Candidate $compatFile
    ) -Message 'a volume root with a trailing separator remains a valid lexical ancestor'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationWindowsProcessArgument -Value '--no-restore') -Expected '--no-restore' -Message 'safe unquoted argument stays byte-identical'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationWindowsProcessArgument -Value '') -Expected '""' -Message 'Windows process argument preserves an empty token'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationWindowsProcessArgument -Value 'C:\Source Folder\repo.csproj') -Expected ('"' + 'C:\Source Folder\repo.csproj' + '"') -Message 'Windows argument encloses a path with spaces'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationWindowsProcessArgument -Value 'C:\Source Folder\') -Expected ('"' + 'C:\Source Folder\\' + '"') -Message 'Windows quoted trailing slash is doubled before closing quote'

    $testCount++
    Assert-ValidationEqual -Actual (ConvertTo-ValidationWindowsProcessArgument -Value 'a"b') -Expected ('"' + 'a\"b' + '"') -Message 'Windows embedded quote is escaped without shell interpretation'

    $controlRejected = $false
    try {
        [void](ConvertTo-ValidationWindowsProcessArgument -Value ("valid" + [char]10 + "injected"))
    }
    catch {
        $controlRejected = $_.Exception.Message.Contains('VALIDATION_ARGUMENT_CONTROL_CHARACTER')
    }
    $testCount++
    Assert-ValidationTrue -Condition $controlRejected -Message 'process command lines reject control-character injection'

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

    # Offline build membership tests are fixture-only. No dotnet process is run.
    $toolingStage = Get-ValidationProofStageDefinition 'offline-tooling-build'
    $testCount++
    Assert-ValidationTrue -Condition (-not $toolingStage.Reusable -and
        $toolingStage.Toolchain -eq 'dotnet') -Message 'offline build must never reuse mutable output proof'

    $offlineRoot = Join-Path $root 'offline-tooling-contract'
    $solutionText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../RimLiaison.sln'))
    Write-ProofFixtureFile -RelativePath 'offline-tooling-contract/RimLiaison.sln' -Value $solutionText
    $solutionProjectLines = @([IO.File]::ReadAllLines((Join-Path $offlineRoot 'RimLiaison.sln')) |
        Where-Object { $_ -match '^Project\("\{[^}]+\}"\) = "[^"]+", "[^"]+\.csproj"' })
    foreach ($entry in $solutionProjectLines) {
        if ($entry -match '^Project\("\{[^}]+\}"\) = "[^"]+", "([^"]+\.csproj)"') {
            $projectRelative = $Matches[1].Replace('\', '/')
            Write-ProofFixtureFile -RelativePath ("offline-tooling-contract/" + $projectRelative) -Value '<Project Sdk="Microsoft.NET.Sdk" />'
        }
    }

    $offlineProjects = @(Get-OfflineToolingProjectPaths -RepositoryRoot $offlineRoot)
    $testCount++
    Assert-ValidationTrue -Condition ($offlineProjects.Count -eq 14 -and
        -not ($offlineProjects -contains 'src/DevBridgeRuntime/Mod/DevBridge2.csproj') -and
        ($offlineProjects -contains 'tests/RimLiaison.Tests/RimLiaison.Tests.csproj') -and
        ($offlineProjects -contains 'src/DevBridgeRuntime/BridgeTools/DevBridge2.BridgeTools.csproj')) -Message 'exactly fourteen tooling projects and no game Mod are selected'

    function Assert-OfflineToolingRefusal {
        param([Parameter(Mandatory = $true)][string]$ExpectedCode)
        $reason = $null
        try {
            [void]@(Get-OfflineToolingProjectPaths -RepositoryRoot $offlineRoot)
        }
        catch {
            $reason = $_.Exception.Message
        }
        Assert-ValidationTrue -Condition ($null -ne $reason -and $reason.Contains($ExpectedCode)) -Message "unexpected result for $ExpectedCode"
    }

    $contractSolution = Join-Path $offlineRoot 'RimLiaison.sln'
    $unexpectedProject = 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "NewUnreviewed", "src\New\Unreviewed.csproj", "{11111111-1111-1111-1111-111111111111}"'
    [IO.File]::WriteAllText($contractSolution, $solutionText + [Environment]::NewLine + $unexpectedProject, [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-OfflineToolingRefusal 'OFFLINE_SOLUTION_MEMBERSHIP_DRIFT'
    [IO.File]::WriteAllText($contractSolution, $solutionText, [Text.UTF8Encoding]::new($false))

    $duplicateProject = [string]$solutionProjectLines[0]
    [IO.File]::WriteAllText(
        $contractSolution,
        $solutionText + [Environment]::NewLine + $duplicateProject,
        [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-OfflineToolingRefusal 'OFFLINE_SOLUTION_DUPLICATE_PROJECT'
    [IO.File]::WriteAllText($contractSolution, $solutionText, [Text.UTF8Encoding]::new($false))

    $missingProject = Join-Path $offlineRoot 'tests/RimContext.Tests/RimContext.Tests.csproj'
    Remove-Item -LiteralPath $missingProject
    $testCount++
    Assert-OfflineToolingRefusal 'OFFLINE_TOOLING_PROJECT_MISSING'
    [IO.File]::WriteAllText($missingProject, '<Project />', [Text.UTF8Encoding]::new($false))

    $consumer = Join-Path $offlineRoot 'src/RimLiaison.Cli/RimLiaison.Cli.csproj'
    [IO.File]::WriteAllText($consumer,
        '<Project><ItemGroup><ProjectReference Include="..\DevBridgeRuntime\Mod\DevBridge2.csproj" /></ItemGroup></Project>',
        [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-OfflineToolingRefusal 'OFFLINE_TOOLING_REFERENCE_OUTSIDE_CLOSURE'
    [IO.File]::WriteAllText($consumer, '<Project />', [Text.UTF8Encoding]::new($false))
    $restoredProjects = @(Get-OfflineToolingProjectPaths -RepositoryRoot $offlineRoot)
    $testCount++
    Assert-ValidationTrue -Condition ($restoredProjects.Count -eq 14) -Message 'restored fixture returns the exact tooling closure'

    # Offline build output evidence is a separate gate after 14 successful
    # command exits. These fixtures never run dotnet or a RimWorld binary.
    $lockRelative = 'src/DevBridgeRuntime/BridgeTools/packages.lock.json'
    $fixtureLockRelative = 'offline-tooling-contract/' + $lockRelative
    Write-ProofFixtureFile -RelativePath $fixtureLockRelative -Value '{"version":1}'
    $fixtureLock = Join-Path $offlineRoot $lockRelative
    $originalLockHash = Get-ValidationFileSha256 $fixtureLock
    $testOutputs = @(
        'tests/RimContext.Tests/bin/Release/net8.0/RimContext.Tests.dll',
        'tests/RimContext.Tests/bin/Release/net8.0/RimContext.Tests.runtimeconfig.json',
        'tests/RimError.Core.Tests/bin/Release/net8.0/RimError.Core.Tests.dll',
        'tests/RimLiaison.Tests/bin/Release/net8.0-windows/RimLiaison.Tests.dll',
        'tests/RimLiaison.Tests/bin/Release/net8.0-windows/RimLiaison.Tests.runtimeconfig.json'
    )
    foreach ($relative in $testOutputs) {
        Write-ProofFixtureFile -RelativePath ("offline-tooling-contract/" + $relative) -Value 'compiled-fixture'
    }

    function Assert-OfflineEvidenceRefusal {
        param(
            [Parameter(Mandatory = $true)][string]$ExpectedCode,
            [Parameter(Mandatory = $true)][string]$ExpectedLockSha256
        )
        $reason = $null
        try {
            [void](Assert-OfflineToolingBuildEvidence -RepositoryRoot $offlineRoot -ExpectedLockSha256 $ExpectedLockSha256)
        }
        catch {
            $reason = [string]$_.Exception.Message
        }
        Assert-ValidationTrue -Condition ($null -ne $reason -and $reason.Contains($ExpectedCode)) -Message "build evidence must fail closed on $ExpectedCode"
    }

    $testCount++
    Assert-ValidationTrue -Condition ([bool](Assert-OfflineToolingBuildEvidence -RepositoryRoot $offlineRoot -ExpectedLockSha256 $originalLockHash)) -Message 'complete three-suite outputs and unchanged locked restore allow build evidence'

    foreach ($relative in $testOutputs) {
        $output = Join-Path $offlineRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        Remove-Item -LiteralPath $output
        $testCount++
        Assert-OfflineEvidenceRefusal -ExpectedCode 'OFFLINE_TOOLING_OUTPUT_MISSING' -ExpectedLockSha256 $originalLockHash
        [IO.File]::WriteAllText($output, 'compiled-fixture', [Text.UTF8Encoding]::new($false))
    }

    $emptyOutput = Join-Path $offlineRoot $testOutputs[0].Replace('/', [IO.Path]::DirectorySeparatorChar)
    [IO.File]::WriteAllText($emptyOutput, '', [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-OfflineEvidenceRefusal -ExpectedCode 'OFFLINE_TOOLING_OUTPUT_INVALID' -ExpectedLockSha256 $originalLockHash
    [IO.File]::WriteAllText($emptyOutput, 'compiled-fixture', [Text.UTF8Encoding]::new($false))

    [IO.File]::WriteAllText($fixtureLock, '{"version":2}', [Text.UTF8Encoding]::new($false))
    $testCount++
    Assert-OfflineEvidenceRefusal -ExpectedCode 'OFFLINE_TOOLING_LOCK_CHANGED' -ExpectedLockSha256 $originalLockHash
    [IO.File]::WriteAllText($fixtureLock, '{"version":1}', [Text.UTF8Encoding]::new($false))

    Remove-Item -LiteralPath $fixtureLock
    $testCount++
    Assert-OfflineEvidenceRefusal -ExpectedCode 'OFFLINE_TOOLING_LOCK_MISSING' -ExpectedLockSha256 $originalLockHash
    [IO.File]::WriteAllText($fixtureLock, '{"version":1}', [Text.UTF8Encoding]::new($false))

    $testCount++
    Assert-ValidationTrue -Condition ([bool](Assert-OfflineToolingBuildEvidence -RepositoryRoot $offlineRoot -ExpectedLockSha256 $originalLockHash)) -Message 'restored output-and-lock fixtures regain evidence readiness'

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
