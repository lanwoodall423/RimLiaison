[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$plannerPath = Join-Path $PSScriptRoot 'ci-plan.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "CI planner assertion failed: $Message"
    }
}

function Assert-Equal {
    param(
        [AllowNull()]$Actual,
        [AllowNull()]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ([string]$Actual -ne [string]$Expected) {
        throw "CI planner assertion failed: $Message; expected '$Expected', received '$Actual'"
    }
}

function Invoke-Plan {
    param(
        [AllowEmptyCollection()][string[]]$Paths,
        [string]$Base,
        [string]$Head,
        [string]$OutputPath,
        [string]$RepositoryRoot
    )

    $arguments = @{ Json = $true }
    if ($PSBoundParameters.ContainsKey('Paths')) {
        $arguments['ChangedPath'] = $Paths
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $arguments['GitHubOutputPath'] = $OutputPath
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($Base) -or -not [string]::IsNullOrWhiteSpace($Head)) {
        $arguments['BaseRevision'] = $Base
        $arguments['HeadRevision'] = $Head
    }

    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $arguments['RepositoryRoot'] = $RepositoryRoot
    }

    $raw = @(& $plannerPath @arguments)
    $text = ($raw -join [Environment]::NewLine).Trim()
    Assert-True (-not [string]::IsNullOrWhiteSpace($text)) 'planner emitted JSON'
    return $text | ConvertFrom-Json -Depth 10
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Operation
    )

    $output = @(& git @Arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "CI planner test Git command failed: $Operation (exit code $exitCode)"
    }

    return $output
}

$testsRun = 0

$plan = Invoke-Plan -Paths @('src/RimContext.Core/Semantics/ProjectSemanticIndexer.cs')
$testsRun++
Assert-Equal $plan.category 'rimcontext-and-rimliaison' 'RimContext implementation category includes the consumer and composition gate'
Assert-True $plan.runRimContext 'RimContext implementation selects RimContext tests'
Assert-True $plan.runRimLiaison 'RimContext implementation selects RimLiaison consumer tests'
Assert-True (-not $plan.runRimError) 'RimContext implementation does not select unrelated RimError tests'
Assert-True $plan.runCrossStack 'RimContext CLI/core output is composition-sensitive'

$plan = Invoke-Plan -Paths @('src/RimError.Core/DiagnosticParser.cs')
$testsRun++
Assert-True $plan.runRimError 'RimError implementation selects RimError tests'
Assert-True $plan.runRimLiaison 'RimError implementation selects RimLiaison consumer tests'
Assert-True (-not $plan.runRimContext) 'RimError implementation does not select unrelated RimContext tests'
Assert-True $plan.runCrossStack 'RimError CLI/core output is composition-sensitive'

$plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/CliExitCodes.cs')
$testsRun++
Assert-Equal $plan.category 'rimliaison-only' 'RimLiaison-only implementation remains selective'
Assert-True $plan.runRimLiaison 'RimLiaison-only implementation selects RimLiaison tests'
Assert-True (-not $plan.runRimContext) 'RimLiaison-only implementation does not select RimContext tests'
Assert-True (-not $plan.runRimError) 'RimLiaison-only implementation does not select RimError tests'
Assert-True (-not $plan.runCrossStack) 'Non-integration RimLiaison-only implementation does not select composition'

$plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/DevBridge/DevBridgeCapabilityAdapter.cs')
$testsRun++
Assert-Equal $plan.category 'rimliaison-and-composition' 'DevBridge adapter selects the composition gate'
Assert-True $plan.runRimLiaison 'DevBridge adapter selects RimLiaison tests'
Assert-True $plan.runCrossStack 'DevBridge adapter selects composition'
Assert-True (-not $plan.runAllInternal) 'DevBridge adapter does not select unrelated component suites'

$plan = Invoke-Plan -Paths @('contracts/cross-stack-compatibility.json')
$testsRun++
Assert-Equal $plan.category 'composition-only' 'Contract-only changes select composition only'
Assert-True $plan.runCrossStack 'Contract changes select composition'
Assert-True (-not $plan.runFormat) 'Contract-only changes do not require source formatting'
Assert-True (-not $plan.runAllInternal) 'Contract-only changes do not select all internal suites'

$plan = Invoke-Plan -Paths @('Directory.Build.props')
$testsRun++
Assert-Equal $plan.category 'all-internal' 'Shared build configuration selects all internal suites'
Assert-True $plan.runAllInternal 'Shared build configuration selects all internal suites'
Assert-True $plan.runRimContext 'Shared build configuration selects RimContext tests'
Assert-True $plan.runRimError 'Shared build configuration selects RimError tests'
Assert-True $plan.runRimLiaison 'Shared build configuration selects RimLiaison tests'
Assert-True $plan.runCrossStack 'Shared build configuration selects composition'

$plan = Invoke-Plan -Paths @('README.md', 'docs/validation.md')
$testsRun++
Assert-Equal $plan.category 'documentation-only' 'Documentation-only changes are cheap'
Assert-True (-not $plan.runRimContext -and -not $plan.runRimError -and -not $plan.runRimLiaison) 'Documentation-only changes skip internal suites'
Assert-True (-not $plan.runFormat -and -not $plan.runCrossStack) 'Documentation-only changes skip expensive work'

$plan = Invoke-Plan -Paths @('fixtures/notes.md')
$testsRun++
Assert-True $plan.runRimError 'Fixture documentation is test input, not documentation-only'
Assert-True (-not $plan.runCrossStack) 'Non-composition fixture input does not select composition'

$plan = Invoke-Plan -Paths @('TestCatalog/rimtest.catalog.json')
$testsRun++
Assert-Equal $plan.category 'rimliaison-only' 'Catalog changes select catalog validation'
Assert-True $plan.runRimLiaison 'Catalog changes select RimLiaison validation'
Assert-True (-not $plan.runRimContext -and -not $plan.runRimError) 'Catalog changes omit unrelated internal suites'
Assert-True (-not $plan.runCrossStack -and -not $plan.runFormat) 'Normal catalog changes omit composition and source formatting'

$plan = Invoke-Plan -Paths @('tests/RimError.Core.Tests/DiagnosticJsonTests.cs')
$testsRun++
Assert-True $plan.runRimError 'RimError test changes select RimError validation'
Assert-True (-not $plan.runRimContext -and -not $plan.runRimLiaison) 'RimError test changes omit unrelated suites'

$plan = Invoke-Plan -Paths @('tests/fixtures/cross-stack/FixtureMod/Source/FixtureMarker.cs')
$testsRun++
Assert-Equal $plan.category 'composition-only' 'Cross-stack fixtures select only composition validation'
Assert-True $plan.runCrossStack 'Cross-stack fixtures select composition'
Assert-True (-not $plan.runRimContext -and -not $plan.runRimError -and -not $plan.runRimLiaison) 'Cross-stack fixtures omit deterministic internal suites'

$plan = Invoke-Plan -Paths @('global.json')
$testsRun++
Assert-Equal $plan.category 'all-internal' 'Global SDK configuration selects complete validation'
Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Global SDK configuration selects all gates'

$plan = Invoke-Plan -Paths @('src/RimContext.Core/RimContext.Core.csproj')
$testsRun++
Assert-Equal $plan.category 'all-internal' 'Project files select complete validation'
Assert-True ($plan.runAllInternal -and $plan.runRimContext -and $plan.runRimError -and $plan.runRimLiaison) 'Project files select all internal suites'

$plan = Invoke-Plan -Paths @('rimliaison.cmd')
$testsRun++
Assert-Equal $plan.category 'all-internal' 'Agent-facing wrappers select complete validation'
Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Agent-facing wrappers select all gates'

$plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/Execution/ArtifactFreshnessTransaction.cs')
$testsRun++
Assert-Equal $plan.category 'rimliaison-and-composition' 'Artifact freshness boundaries select composition'
Assert-True $plan.runRimLiaison 'Artifact freshness changes select RimLiaison validation'
Assert-True $plan.runCrossStack 'Artifact freshness changes select composition'
Assert-True (-not $plan.runAllInternal -and -not $plan.runRimContext -and -not $plan.runRimError) 'Artifact freshness changes omit unrelated suites'

$plan = Invoke-Plan -Paths @('src/new-component/Unknown.cs')
$testsRun++
Assert-Equal $plan.category 'full-uncertain' 'Ambiguous source paths fall back conservatively'
Assert-True ($plan.certain -eq $false) 'Ambiguous source paths are reported as uncertain'
Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Ambiguous source paths select all validation'

$plan = Invoke-Plan -Paths @('src/RimContext.Core/Context.cs', 'src/RimError.Core/Error.cs')
$testsRun++
Assert-Equal $plan.category 'mixed' 'Multiple component changes remain deterministic and composable'
Assert-True ($plan.runRimContext -and $plan.runRimError -and $plan.runRimLiaison) 'Mixed component changes select all affected internal suites'
Assert-True (-not $plan.runAllInternal) 'Mixed component changes do not imply unrelated all-internal fallback'

$repositoryRoot = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-ci-plan-git-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $repositoryRoot | Out-Null
try {
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'init', '--quiet') -Operation 'git init'
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'config', 'user.email', 'ci-plan@example.invalid') -Operation 'git config user.email'
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'config', 'user.name', 'CI planner tests') -Operation 'git config user.name'
    Set-Content -LiteralPath (Join-Path $repositoryRoot 'old.cs') -Value 'class Old {}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $repositoryRoot 'deleted.cs') -Value 'class Deleted {}' -Encoding utf8
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'add', '--', '.') -Operation 'git add initial fixture'
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'commit', '--quiet', '-m', 'initial') -Operation 'git commit initial fixture'
    $baseRevision = ([string](Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'rev-parse', 'HEAD') -Operation 'git rev-parse initial fixture')).Trim()

    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'mv', 'old.cs', 'renamed.cs') -Operation 'git mv fixture'
    Remove-Item -LiteralPath (Join-Path $repositoryRoot 'deleted.cs') -Force
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'add', '--', '.') -Operation 'git add renamed fixture'
    Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'commit', '--quiet', '-m', 'rename-and-delete') -Operation 'git commit renamed fixture'
    $headRevision = ([string](Invoke-GitChecked -Arguments @('-C', $repositoryRoot, 'rev-parse', 'HEAD') -Operation 'git rev-parse renamed fixture')).Trim()

    $plan = Invoke-Plan -Base $baseRevision -Head $headRevision -RepositoryRoot $repositoryRoot
    $testsRun++
    Assert-Equal $plan.category 'full-uncertain' 'Git renames and deletions escalate conservatively'
    Assert-True (-not $plan.certain) 'Git renames and deletions are not classified as certain'
    Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Git renames and deletions select all gates'
    Assert-Equal $plan.changeStatusCounts.renamed 1 'Git rename status is retained in the plan'
    Assert-Equal $plan.changeStatusCounts.deleted 1 'Git deletion status is retained in the plan'
}
finally {
    if (Test-Path -LiteralPath $repositoryRoot) {
        Remove-Item -LiteralPath $repositoryRoot -Recurse -Force
    }
}

$plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/CliExitCodes.cs', 'src/RimContext.Core/Context.cs')
$testsRun++
$reversedPlan = Invoke-Plan -Paths @('src/RimContext.Core/Context.cs', 'src/RimLiaison.Cli/CliExitCodes.cs')
Assert-Equal ($plan | ConvertTo-Json -Depth 10 -Compress) ($reversedPlan | ConvertTo-Json -Depth 10 -Compress) 'Path order does not affect the plan'

$plan = Invoke-Plan -Base 'not-a-real-base-revision' -Head 'not-a-real-head-revision'
$testsRun++
Assert-Equal $plan.category 'full-uncertain' 'Unavailable base information falls back conservatively'
Assert-True (-not $plan.certain) 'Unavailable base information is reported as uncertain'
Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Unavailable base information cannot skip validation'
$testsRun++
Assert-Equal $LASTEXITCODE 0 'Handled native revision failure does not leak into the caller exit state'

$outputPath = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-ci-plan-' + [Guid]::NewGuid().ToString('N') + '.out')
try {
    $plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/CliExitCodes.cs') -OutputPath $outputPath
    $outputLines = @(Get-Content -LiteralPath $outputPath)
    $testsRun++
    Assert-True (@($outputLines | Where-Object { $_ -eq 'run_rimliaison=true' }).Count -eq 1) 'GitHub output variables contain selected suite'
    Assert-True (@($outputLines | Where-Object { $_ -eq 'run_rimcontext=false' }).Count -eq 1) 'GitHub output variables contain skipped suite'
    Assert-True (@($outputLines | Where-Object { $_ -eq 'run_cross_stack=false' }).Count -eq 1) 'GitHub output variables contain skipped composition gate'
    Assert-True (@($outputLines | Where-Object { $_ -like 'plan_json=*' }).Count -eq 1) 'GitHub output variables contain bounded machine-readable plan'
}
finally {
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }
}

$pwshPath = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Path
& $pwshPath -NoProfile -ExecutionPolicy Bypass -Command 'function Assert-True { param([bool]$Condition); if (-not $Condition) { throw "intentional assertion failure" } }; Assert-True $false' 2>$null
$assertionExitCode = $LASTEXITCODE
$testsRun++
Assert-True ($assertionExitCode -ne 0) 'Genuine assertion failure retains a nonzero exit code'

$workflowPath = Join-Path $PSScriptRoot '..\.github\workflows\ci.yml'
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$testsRun++
Assert-True ($workflowText.Contains('run: pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-plan.tests.ps1', [StringComparison]::Ordinal)) `
    'The single CI plan job invokes ci-plan.tests.ps1 through a fresh pwsh -File process'
$testsRun++
Assert-True ($workflowText.Contains('run: pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validation-proofs.tests.ps1', [StringComparison]::Ordinal)) `
    'The single CI plan job invokes validation-proofs.tests.ps1 through a fresh pwsh -File process'
$testsRun++
Assert-True ($workflowText.Contains('run: pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\devbridge-binary-cache.tests.ps1', [StringComparison]::Ordinal)) `
    'The single CI plan job invokes the exact binary-cache identity tests'
$testsRun++
Assert-True (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\.github\workflows\cross-stack-contract.yml'))) `
    'The standalone cross-stack workflow is removed after consolidation'
$testsRun++
Assert-Equal ([regex]::Matches($workflowText, '(?m)^  plan:\s*$').Count) 1 `
    'The consolidated workflow has exactly one authoritative plan job'

$deterministicStart = $workflowText.IndexOf("  deterministic:", [StringComparison]::Ordinal)
$crossStackStart = $workflowText.IndexOf("  cross-stack-contract:", [StringComparison]::Ordinal)
Assert-True ($deterministicStart -ge 0 -and $crossStackStart -gt $deterministicStart) `
    'The consolidated workflow contains deterministic and cross-stack fan-out jobs'
$deterministicBlock = $workflowText.Substring($deterministicStart, $crossStackStart - $deterministicStart)
$crossStackBlock = $workflowText.Substring($crossStackStart)
$testsRun++
Assert-True (-not $deterministicBlock.Contains('planner-tests', [StringComparison]::Ordinal)) `
    'Planner tests are not rerun in deterministic validation'
$testsRun++
Assert-True ($crossStackBlock.Contains("needs: plan", [StringComparison]::Ordinal) -and
    $crossStackBlock.Contains("if: needs.plan.outputs.run_cross_stack == 'true'", [StringComparison]::Ordinal)) `
    'Cross-stack validation is conditional on the authoritative plan'
$testsRun++
Assert-True (-not $crossStackBlock.Contains('RimLiaison.Tests', [StringComparison]::Ordinal) -and
    -not $crossStackBlock.Contains('RimContext.Tests', [StringComparison]::Ordinal) -and
    -not $crossStackBlock.Contains('RimError.Core.Tests', [StringComparison]::Ordinal)) `
    'Cross-stack validation does not rerun deterministic suites'

$binaryCacheMatch = [regex]::Match(
    $workflowText,
    '(?ms)- name: Cache exact pinned DevBridge2 binaries\r?\n(?<block>.*?)(?=\r?\n      - name:|\z)')
Assert-True $binaryCacheMatch.Success 'Exact DevBridge2 binary cache step is present'
$binaryCacheBlock = $binaryCacheMatch.Groups['block'].Value
$testsRun++
Assert-True ($binaryCacheBlock.Contains('steps.devbridge-binary-key.outputs.cache_key', [StringComparison]::Ordinal)) `
    'Binary cache uses the derived exact identity'
$testsRun++
Assert-True ($binaryCacheBlock.Contains('Coordinator/bin/Release/net8.0', [StringComparison]::Ordinal) -and
    $binaryCacheBlock.Contains('FakeRimWorld/bin/Release/net8.0', [StringComparison]::Ordinal)) `
    'Binary cache covers both immutable DevBridge2 fixture outputs'
$testsRun++
Assert-True (-not $binaryCacheBlock.Contains('restore-keys:', [StringComparison]::Ordinal)) `
    'Binary cache has no loose restore key'
$testsRun++
Assert-True ($workflowText.Contains('DevBridgeBinaryCacheHit = $true', [StringComparison]::Ordinal)) `
    'Cross-stack execution receives the exact binary-cache hit parameter'

$global:LASTEXITCODE = 0
Write-Output ('CI planner tests passed: {0}' -f $testsRun)
exit 0
