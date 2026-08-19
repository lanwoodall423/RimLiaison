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
        [string]$OutputPath
    )

    if ($PSBoundParameters.ContainsKey('Paths')) {
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $raw = @(& $plannerPath -Json -ChangedPath $Paths -GitHubOutputPath $OutputPath)
        } else {
            $raw = @(& $plannerPath -Json -ChangedPath $Paths)
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($Base) -or -not [string]::IsNullOrWhiteSpace($Head)) {
        $raw = @(& $plannerPath -Json -BaseRevision $Base -HeadRevision $Head)
    } else {
        $raw = @(& $plannerPath -Json)
    }

    $text = ($raw -join [Environment]::NewLine).Trim()
    Assert-True (-not [string]::IsNullOrWhiteSpace($text)) 'planner emitted JSON'
    return $text | ConvertFrom-Json -Depth 10
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

$plan = Invoke-Plan -Paths @('src/RimLiaison.Cli/CliExitCodes.cs', 'src/RimContext.Core/Context.cs')
$testsRun++
$reversedPlan = Invoke-Plan -Paths @('src/RimContext.Core/Context.cs', 'src/RimLiaison.Cli/CliExitCodes.cs')
Assert-Equal ($plan | ConvertTo-Json -Depth 10 -Compress) ($reversedPlan | ConvertTo-Json -Depth 10 -Compress) 'Path order does not affect the plan'

$plan = Invoke-Plan -Base 'not-a-real-base-revision' -Head 'not-a-real-head-revision'
$testsRun++
Assert-Equal $plan.category 'full-uncertain' 'Unavailable base information falls back conservatively'
Assert-True (-not $plan.certain) 'Unavailable base information is reported as uncertain'
Assert-True ($plan.runAllInternal -and $plan.runCrossStack) 'Unavailable base information cannot skip validation'

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

Write-Output ('CI planner tests passed: {0}' -f $testsRun)
