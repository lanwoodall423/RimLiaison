[CmdletBinding()]
param(
    [string]$PlanJson,
    [Alias('Stage')][string[]]$StageId = @(),
    [string]$RepositoryRoot,
    [string]$ProofRoot,
    [string]$DevBridgeRoot,
    [string]$GitBaseRevision,
    [string]$GitHeadRevision,
    [switch]$DevBridgeBinaryCacheHit,
    [switch]$NoProofReuse,
    [string]$StageReportPath,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-proof.ps1')

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
} else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$DevBridgeRoot = if ([string]::IsNullOrWhiteSpace($DevBridgeRoot)) {
    $sibling = Join-Path ([IO.Directory]::GetParent($repositoryRoot).FullName) 'DevBridge2'
    if (Test-Path -LiteralPath $sibling -PathType Container) { $sibling } else { $null }
} else {
    [IO.Path]::GetFullPath($DevBridgeRoot)
}
$proofDirectory = Get-ValidationProofDirectory $repositoryRoot $ProofRoot

function Limit-ValidationText {
    param(
        [AllowNull()][string]$Text,
        [int]$Limit = 1024
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $value = $Text.Trim()
    if ($value.Length -le $Limit) {
        return $value
    }

    return $value.Substring(0, $Limit) + "`n...[truncated]"
}

function Invoke-ValidationProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [int]$TimeoutMilliseconds = 600000
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) {
        [void]$start.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            return [pscustomobject]@{
                ExitCode = 2
                TimedOut = $false
                Stdout = ''
                Stderr = 'process did not start'
            }
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit($TimeoutMilliseconds)
        if (-not $completed) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            try { [void]$process.WaitForExit(5000) } catch { }
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $outputBytes = [Text.Encoding]::UTF8.GetByteCount([string]$stdout) +
            [Text.Encoding]::UTF8.GetByteCount([string]$stderr)
        $truncated = $outputBytes -gt 1048576
        return [pscustomobject]@{
            ExitCode = if ($truncated) { 1 } elseif ($completed -and $process.HasExited) { $process.ExitCode } else { 124 }
            TimedOut = -not $completed
            Truncated = $truncated
            Stdout = [string]$stdout
            Stderr = if ($truncated) { 'validation process output exceeded the 1 MiB bound' } else { [string]$stderr }
        }
    }
    catch {
            return [pscustomobject]@{
                ExitCode = 2
                TimedOut = $false
                Truncated = $false
                Stdout = ''
                Stderr = $_.Exception.Message
            }
    }
    finally {
        $process.Dispose()
    }
}

function New-ValidationCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [int]$TimeoutMilliseconds = 600000
    )

    return [pscustomobject]@{
        FileName = $FileName
        Arguments = $Arguments
        WorkingDirectory = $WorkingDirectory
        TimeoutMilliseconds = $TimeoutMilliseconds
    }
}

function Get-ValidationStageCommands {
    param(
        [Parameter(Mandatory = $true)][string]$StageId,
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$ExternalRoot,
        [switch]$RestoreAlreadyRun,
        [switch]$DevBridgeBinaryCacheHit
    )

    $rootDotnet = Join-Path $Root 'RimLiaison.sln'
    $commands = [Collections.Generic.List[object]]::new()
    $testProject = $null
    $testMode = $null
    switch ($StageId) {
        'rimcontext' {
            $testProject = Join-Path $Root 'tests/RimContext.Tests/RimContext.Tests.csproj'
            $testMode = 'run'
        }
        'rimerror' {
            $testProject = Join-Path $Root 'tests/RimError.Core.Tests/RimError.Core.Tests.csproj'
            $testMode = 'test'
        }
        'rimliaison' {
            $testProject = Join-Path $Root 'tests/RimLiaison.Tests/RimLiaison.Tests.csproj'
            $testMode = 'run'
        }
        'format' {
            if (-not $RestoreAlreadyRun) {
                [void]$commands.Add((New-ValidationCommand 'dotnet' @('restore', $rootDotnet, '--nologo') $Root))
            }
            [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                        'format', $rootDotnet, '--verify-no-changes', '--no-restore') $Root 900000))
            return $commands.ToArray()
        }
        'planner-tests' {
            [void]$commands.Add((New-ValidationCommand 'pwsh' @(
                        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts/ci-plan.tests.ps1')) $Root 300000))
            return $commands.ToArray()
        }
        'diff-check' {
            $gitArguments = @('-c', "safe.directory=$Root", '-C', $Root, 'diff', '--check')
            if (-not [string]::IsNullOrWhiteSpace($GitBaseRevision) -and
                -not [string]::IsNullOrWhiteSpace($GitHeadRevision)) {
                $gitArguments = @('-c', "safe.directory=$Root", '-C', $Root, 'diff', '--check', $GitBaseRevision, $GitHeadRevision, '--')
            }
            [void]$commands.Add((New-ValidationCommand 'git' $gitArguments $Root 120000))
            return $commands.ToArray()
        }
        'cross-stack' {
            if ([string]::IsNullOrWhiteSpace($ExternalRoot)) {
                throw 'VALIDATION_DEVBRIDGE_ROOT_MISSING'
            }
            $devRoot = [IO.Path]::GetFullPath($ExternalRoot)
            $requiredDevBridgeOutputs = @(
                'Source/Coordinator/bin/Release/net8.0/DevBridge.Coordinator.exe',
                'Source/FakeRimWorld/bin/Release/net8.0/DevBridge.FakeRimWorld.exe')
            if ($DevBridgeBinaryCacheHit) {
                foreach ($relativeOutput in $requiredDevBridgeOutputs) {
                    $outputPath = Join-Path $devRoot ($relativeOutput.Replace('/', [IO.Path]::DirectorySeparatorChar))
                    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
                        throw "VALIDATION_DEVBRIDGE_BINARY_CACHE_OUTPUT_MISSING:$relativeOutput"
                    }
                }
            } else {
                [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                            'restore', (Join-Path $devRoot 'Source/Coordinator/DevBridge.Coordinator.csproj'), '--locked-mode', '--nologo') $devRoot 600000))
                [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                            'restore', (Join-Path $devRoot 'Source/FakeRimWorld/FakeRimWorld.csproj'), '--locked-mode', '--nologo') $devRoot 600000))
                [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                            'build', (Join-Path $devRoot 'Source/Coordinator/DevBridge.Coordinator.csproj'), '--configuration', 'Release', '--no-restore', '--nologo') $devRoot 900000))
                [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                            'build', (Join-Path $devRoot 'Source/FakeRimWorld/FakeRimWorld.csproj'), '--configuration', 'Release', '--no-restore', '--nologo') $devRoot 900000))
            }
            [void]$commands.Add((New-ValidationCommand 'dotnet' @('restore', $rootDotnet, '--nologo') $Root 600000))
            foreach ($project in @(
                'src/RimContext.Cli/RimContext.Cli.csproj',
                'src/RimError.Cli/RimError.Cli.csproj',
                'src/RimLiaison.Cli/RimLiaison.Cli.csproj',
                'tests/RimLiaison.Tests/RimLiaison.Tests.csproj')) {
                [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                            'build', (Join-Path $Root $project), '--configuration', 'Release', '--no-restore', '--nologo') $Root 900000))
            }
            [void]$commands.Add((New-ValidationCommand 'pwsh' @(
                        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                        (Join-Path $Root 'scripts/cross-stack-contract.tests.ps1'),
                        '-RimLiaisonRoot', $Root,
                        '-DevBridgeRoot', $devRoot,
                        '-Json') $Root 1200000))
            return $commands.ToArray()
        }
        default {
            throw "VALIDATION_STAGE_UNKNOWN:$StageId"
        }
    }

    if (-not $RestoreAlreadyRun) {
        [void]$commands.Add((New-ValidationCommand 'dotnet' @('restore', $rootDotnet, '--nologo') $Root 600000))
    }

    [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                'build', $testProject, '--configuration', 'Release', '--no-restore', '--nologo') $Root 900000))
    $testTimeoutMilliseconds = if ($StageId -eq 'rimliaison') { 1800000 } else { 900000 }
    if ($testMode -eq 'test') {
        [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                    'test', $testProject, '--configuration', 'Release', '--no-build', '--no-restore', '--nologo') $Root $testTimeoutMilliseconds))
    } else {
        [void]$commands.Add((New-ValidationCommand 'dotnet' @(
                    'run', '--project', $testProject, '--configuration', 'Release', '--no-build', '--no-restore') $Root $testTimeoutMilliseconds))
    }

    return $commands.ToArray()
}

function Get-ValidationPlanStages {
    if (-not [string]::IsNullOrWhiteSpace($PlanJson)) {
        try {
            $plan = $PlanJson | ConvertFrom-Json -Depth 20
            if ($null -eq $plan.selectedValidation) {
                throw 'plan selectedValidation is missing'
            }
            return @($plan.selectedValidation | ForEach-Object { [string]$_ })
        }
        catch {
            throw "VALIDATION_PLAN_INVALID:$($_.Exception.Message)"
        }
    }

    if (@($StageId).Count -gt 0) {
        return @($StageId | ForEach-Object { [string]$_ })
    }

    throw 'VALIDATION_PLAN_REQUIRED'
}

function Write-ValidationReport {
    param(
        [Parameter(Mandatory = $true)]$Report
    )

    $jsonReport = $Report | ConvertTo-Json -Depth 12 -Compress
    if ($Json) {
        [Console]::Out.WriteLine($jsonReport)
    } else {
        [Console]::Out.WriteLine(("validation status={0}; selected={1}; executed={2}; reused={3}" -f
                $Report.status, $Report.selected, $Report.executed, $Report.reused))
    }
}

function Write-ValidationStageReport {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Action,
        [AllowNull()][string]$ProofId,
        [AllowNull()][string]$Output
    )

    if ([string]::IsNullOrWhiteSpace($StageReportPath)) {
        return
    }

    try {
        $path = [IO.Path]::GetFullPath($StageReportPath)
        $parent = Split-Path -Parent $path
        [void](New-Item -ItemType Directory -Force -Path $parent)
        $outputText = if ([string]::IsNullOrWhiteSpace($Output)) { $null } else { $Output.Trim() }
        if ($null -ne $outputText -and
            [Text.Encoding]::UTF8.GetByteCount($outputText) -le 1048576) {
            [IO.File]::WriteAllText($path, $outputText, [Text.UTF8Encoding]::new($false))
            return
        }

        $report = [ordered]@{
            schemaVersion = 'rimliaison-validation-proof-report/v1'
            status = 'pass'
            stage = $Stage
            action = $Action
            proofId = $ProofId
        }
        [IO.File]::WriteAllText(
            $path,
            ($report | ConvertTo-Json -Compress),
            [Text.UTF8Encoding]::new($false))
    }
    catch {
        # A report artifact is optional and cannot change validation status.
    }
}

$selectedStages = @()
$stageReports = [Collections.Generic.List[object]]::new()
$status = 'pass'
$failure = $null

try {
    $selectedStages = @(Get-ValidationPlanStages |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [string]$_ } |
        Select-Object -Unique)
    if ($selectedStages.Count -eq 0) {
        throw 'VALIDATION_PLAN_EMPTY'
    }

    $restoreAlreadyRun = $false
    foreach ($stage in $selectedStages) {
        $definition = Get-ValidationProofStageDefinition $stage
        $fingerprint = Get-ValidationStageFingerprint `
            -RepositoryRoot $repositoryRoot `
            -StageId $stage `
            -SelectedTestIds @($stage) `
            -DevBridgeRoot $DevBridgeRoot `
            -GitBaseRevision $GitBaseRevision `
            -GitHeadRevision $GitHeadRevision

        $reused = $false
        $proof = $null
        if (-not $NoProofReuse -and $fingerprint.Complete) {
            $proof = Get-ValidationProofRecord `
                -RepositoryRoot $repositoryRoot `
                -Fingerprint $fingerprint `
                -StageId $stage `
                -SelectedTestIds @($stage) `
                -ProofRoot $proofDirectory
            $reused = $null -ne $proof
        }

        if ($reused) {
            Write-ValidationStageReport `
                -Stage $stage `
                -Action 'reused' `
                -ProofId ([string]$fingerprint.ProofId) `
                -Output $null
            [void]$stageReports.Add([pscustomobject]@{
                    id = $stage
                    status = 'pass'
                    action = 'reused'
                    proofId = [string]$fingerprint.ProofId
                    inputCount = [int]$fingerprint.InputCount
                })
            continue
        }

        $commands = @()
        try {
            $commands = @(Get-ValidationStageCommands `
                    -StageId $stage `
                    -Root $repositoryRoot `
                    -ExternalRoot $DevBridgeRoot `
                    -RestoreAlreadyRun:$restoreAlreadyRun `
                    -DevBridgeBinaryCacheHit:$DevBridgeBinaryCacheHit)
        }
        catch {
            $status = 'fail'
            $failure = [pscustomobject]@{ stage = $stage; errorCode = 'VALIDATION_COMMANDS_INVALID'; error = (Limit-ValidationText $_.Exception.Message) }
            [void]$stageReports.Add([pscustomobject]@{
                    id = $stage
                    status = 'fail'
                    action = 'executed'
                    proofId = $null
                    inputCount = [int]$fingerprint.InputCount
                    errorCode = 'VALIDATION_COMMANDS_INVALID'
                })
            break
        }

        $stagePassed = $true
        $stageFailure = $null
        $lastResult = $null
        foreach ($command in $commands) {
            $result = Invoke-ValidationProcess `
                -FileName $command.FileName `
                -Arguments $command.Arguments `
                -WorkingDirectory $command.WorkingDirectory `
                -TimeoutMilliseconds $command.TimeoutMilliseconds
            $lastResult = $result
            if ([int]$result.ExitCode -ne 0) {
                $stagePassed = $false
                $errorCode = if ([bool]$result.Truncated) {
                    'VALIDATION_OUTPUT_TRUNCATED'
                } elseif ([bool]$result.TimedOut) {
                    'VALIDATION_TIMEOUT'
                } else {
                    'VALIDATION_STAGE_FAILED'
                }
                $stageFailure = [pscustomobject]@{
                    stage = $stage
                    errorCode = $errorCode
                    exitCode = [int]$result.ExitCode
                    command = [string]$command.FileName
                    error = Limit-ValidationText ((@($result.Stderr, $result.Stdout) |
                            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) -join "`n")
                }
                break
            }

            if ($command.FileName -eq 'dotnet' -and
                @($command.Arguments).Count -gt 0 -and
                [string]$command.Arguments[0] -eq 'restore') {
                $restoreAlreadyRun = $true
            }
        }

        if (-not $stagePassed) {
            $status = 'fail'
            $failure = $stageFailure
            Write-ValidationStageReport `
                -Stage $stage `
                -Action 'executed' `
                -ProofId $null `
                -Output ([string]$lastResult.Stdout)
            [void]$stageReports.Add([pscustomobject]@{
                    id = $stage
                    status = 'fail'
                    action = 'executed'
                    proofId = $null
                    inputCount = [int]$fingerprint.InputCount
                    errorCode = [string]$stageFailure.errorCode
                })
            break
        }

        # A proof is created only after every command in the stage completed
        # successfully. Timeout, cancellation, truncation, and infrastructure
        # failures therefore cannot become reusable evidence.
        if (-not $NoProofReuse -and $fingerprint.Complete) {
            $record = New-ValidationProofRecord `
                -Fingerprint $fingerprint `
                -StageId $stage `
                -SelectedTestIds @($stage) `
                -Status 'pass'
            [void](Save-ValidationProofRecord `
                    -RepositoryRoot $repositoryRoot `
                    -Record $record `
                    -ProofRoot $proofDirectory)
        }

        $proofIdForReport = if ($fingerprint.Complete) { [string]$fingerprint.ProofId } else { $null }
        $outputForReport = if ($null -eq $lastResult) { $null } else { [string]$lastResult.Stdout }
        Write-ValidationStageReport `
            -Stage $stage `
            -Action 'executed' `
            -ProofId $proofIdForReport `
            -Output $outputForReport

        [void]$stageReports.Add([pscustomobject]@{
                id = $stage
                status = 'pass'
                action = 'executed'
                proofId = if ($fingerprint.Complete) { [string]$fingerprint.ProofId } else { $null }
                inputCount = [int]$fingerprint.InputCount
            })
    }
}
catch {
    $status = 'fail'
    $failure = [pscustomobject]@{
        stage = $null
        errorCode = if ($_.Exception.Message -match '^([A-Z][A-Z0-9_]+)') { $Matches[1] } else { 'VALIDATION_RUN_FAILED' }
        error = Limit-ValidationText $_.Exception.Message
    }
}

$executedCount = @($stageReports | Where-Object { $_.action -eq 'executed' }).Count
$reusedCount = @($stageReports | Where-Object { $_.action -eq 'reused' }).Count
$report = [ordered]@{
    schemaVersion = 'rimliaison-validation-run/v1'
    status = $status
    selected = [int]$selectedStages.Count
    executed = [int]$executedCount
    reused = [int]$reusedCount
    proofReuse = [ordered]@{
        enabled = -not [bool]$NoProofReuse
        directory = if ($NoProofReuse) { $null } else { '.rimdev/validation-proofs' }
        maxRecords = 64
        maxBytes = 1048576
    }
    stages = @($stageReports | Select-Object -First 32)
    failure = $failure
}
Write-ValidationReport $report
if ($status -eq 'pass') { exit 0 }
exit 1
