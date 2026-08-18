[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'

function Write-Result {
    param([Parameter(Mandatory = $true)]$Value, [int]$ExitCode = 0, [string]$PersistPath)
    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    if (-not [string]::IsNullOrWhiteSpace($PersistPath)) {
        [IO.File]::WriteAllText($PersistPath, $json, [Text.UTF8Encoding]::new($false))
    }
    [Console]::Out.WriteLine($json)
    exit $ExitCode
}

function Get-ArgumentValue {
    param([Parameter(Mandatory = $true)][string[]]$Values, [Parameter(Mandatory = $true)][string]$Name)
    $index = [Array]::IndexOf($Values, $Name)
    if ($index -lt 0 -or $index + 1 -ge $Values.Count) { return $null }
    return [string]$Values[$index + 1]
}

try {
    $root = Get-ArgumentValue $Arguments '--root'
    if ([string]::IsNullOrWhiteSpace($root)) { throw 'fake DevBridge requires --root' }
    $root = [IO.Path]::GetFullPath($root)
    $bridgeIndex = [Array]::IndexOf($Arguments, 'bridge')
    if ($bridgeIndex -ge 0 -and $bridgeIndex + 1 -lt $Arguments.Count -and
        $Arguments[$bridgeIndex + 1] -eq 'tools') {
        $capabilities = [ordered]@{
            schemaVersion = 'rimbridge-tools/v1'
            success = $true
            result = [ordered]@{
                tools = @([ordered]@{
                    id = 'rimworld/inspect_fixture'
                    title = 'Inspect deterministic fixture'
                    summary = 'Read-only synthetic capability for the cross-stack gate.'
                    category = 'testing'
                    providerId = 'RimBridgeServer'
                    source = 'cross-stack-fixture'
                    parameters = @()
                    readOnly = $true
                })
            }
            errorCode = $null
            error = $null
        }
        Write-Result $capabilities 0 (Join-Path $root '.cross-stack-capabilities.json')
    }
    $logsIndex = [Array]::IndexOf($Arguments, 'logs')
    if ($logsIndex -ge 0 -and $logsIndex + 1 -lt $Arguments.Count -and
        $Arguments[$logsIndex + 1] -eq 'query') {
        $statePath = Join-Path $root '.cross-stack-state.json'
        $state = $null
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 12
        }
        $generationText = Get-ArgumentValue $Arguments '--generation'
        $generation = if ($generationText -match '^\d+$') { [int]$generationText } else { 0 }
        $available = $null -ne $state -and $generation -gt 0 -and
            [int]$state.generation -eq $generation
        Write-Result ([ordered]@{
            schemaVersion = 'devbridge-logs-query/v1'
            contract = 'devbridge-logs-query/v1'
            success = $true
            generation = $generation
            sinceLaunch = $Arguments -contains '--since-launch'
            available = $available
            rawBytes = 0
            semanticBytes = 0
            truncated = $false
            records = @()
            errorCode = if ($available) { $null } else { 'PLAYER_LOG_UNAVAILABLE' }
            error = if ($available) { $null } else { 'The fake host has no diagnostic records.' }
        })
    }
    $testIndex = [Array]::IndexOf($Arguments, 'test')
    if ($testIndex -lt 0 -or $testIndex + 3 -ge $Arguments.Count -or
        $Arguments[$testIndex + 1] -ne 'recipe') {
        throw 'fake DevBridge only implements test recipe operations'
    }
    $operation = [string]$Arguments[$testIndex + 2]
    $recipeId = [string]$Arguments[$testIndex + 3]
    $statePath = Join-Path $root '.cross-stack-state.json'
    $state = $null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 12
    }
    $workflow = Get-ArgumentValue $Arguments '--workflow-id'
    if ([string]::IsNullOrWhiteSpace($workflow) -and $null -ne $state) {
        $workflow = [string]$state.workflowId
    }
    if ([string]::IsNullOrWhiteSpace($workflow)) {
        $workflow = 'workflow-cross-stack-contract-v1'
    }

    switch ($operation) {
        'show' {
            $recipe = [ordered]@{
                schemaVersion = 'devbridge-test-recipe/v1'
                id = $recipeId
                description = 'Deterministic cross-stack contract fixture recipe.'
                projects = @('frontier')
                inputs = [ordered]@{ fixture = 'cross-stack' }
                requiresReady = $true
                success = [ordered]@{ quicktestReady = $true }
                budget = [ordered]@{
                    timeoutSeconds = 30
                    maxRimWorldLaunches = 1
                    maxRecipeAttempts = 1
                    maxCoordinatorRefreshes = 1
                }
            }
            Write-Result ([ordered]@{
                schemaVersion = 'devbridge-test-recipe-show/v1'
                recipe = $recipe
                errorCode = $null
                error = $null
                exitCode = 0
            })
        }
        'plan' {
            Write-Result ([ordered]@{
                schemaVersion = 'devbridge-test-recipe-plan/v1'
                recipe = $recipeId
                alreadySatisfied = $false
                estimatedRimWorldLaunches = 1
                nextAction = $null
                blockedBy = @()
                steps = @([ordered]@{
                    action = 'execute'
                    reasonCode = 'CROSS_STACK_FIXTURE'
                    condition = 'deterministic fake host is ready'
                    recipe = $recipeId
                })
                errorCode = $null
                error = $null
                exitCode = 0
            })
        }
        'run' {
            if ($null -eq $state) { throw 'fake DevBridge recipe run has no transaction state' }
            $operationId = [string]$state.operationId
            $operation = [ordered]@{
                tool = 'rimworld/inspect_fixture'
                success = $true
                errorCode = $null
                failedAssertionPointers = @()
                operationId = $operationId
                workflowId = $workflow
                generation = [int]$state.generation
                launchId = [string]$state.launchId
            }
            $runResult = [ordered]@{
                schemaVersion = 'devbridge-test-recipe-run/v1'
                success = $true
                recipe = $recipeId
                runId = [string]$state.runId
                workflowId = $workflow
                generation = [int]$state.generation
                leaseId = [string]$state.leaseId
                evidence = 'evidence-cross-stack-contract-v1'
                evidenceId = 'evidence-cross-stack-contract-v1'
                failureFingerprint = $null
                finalNextAction = $null
                restartRequired = $false
                launchesConsumed = 1
                operations = @($operation)
                errorCode = $null
                error = $null
                exitCode = 0
            }
            Write-Result $runResult 0 (Join-Path $root '.cross-stack-recipe-run.json')
        }
        default {
            throw "unsupported fake recipe operation: $operation"
        }
    }
}
catch {
    Write-Result ([ordered]@{
        schemaVersion = 'devbridge-test-recipe-run/v1'
        success = $false
        recipe = $null
        runId = $null
        workflowId = $null
        generation = $null
        leaseId = $null
        evidence = $null
        evidenceId = $null
        failureFingerprint = $null
        finalNextAction = 'inspect-cross-stack-contract'
        restartRequired = $null
        launchesConsumed = $null
        operations = @()
        errorCode = 'CROSS_STACK_FAKE_DEVBRIDGE_FAILED'
        error = $_.Exception.Message
        exitCode = 1
    } ) 1
}
