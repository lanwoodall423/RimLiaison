[CmdletBinding()]
param(
    [string]$RimLiaisonRoot,
    [string]$DevBridgeRoot,
    [switch]$KeepWorkspace,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pinned-devbridge-worktree.ps1')
$scriptRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$parentRoot = (Get-Item -LiteralPath $scriptRoot).Parent.FullName
$RimLiaisonRoot = if ([string]::IsNullOrWhiteSpace($RimLiaisonRoot)) { $scriptRoot } else { [IO.Path]::GetFullPath($RimLiaisonRoot) }
$DevBridgeRoot = if ([string]::IsNullOrWhiteSpace($DevBridgeRoot)) { Join-Path $parentRoot 'DevBridge2' } else { [IO.Path]::GetFullPath($DevBridgeRoot) }

$manifestPath = Join-Path $RimLiaisonRoot 'contracts\cross-stack-compatibility.json'
$manifest = $null
$workspaceRoot = $null
$auxiliaryRoot = $null
$devBridgeResolution = $null
$report = $null
$exitCode = 0

function Limit-Text {
    param([AllowNull()][string]$Text, [int]$Limit = 2048)
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $value = $Text.Trim()
    if ($value.Length -le $Limit) { return $value }
    return $value.Substring(0, $Limit) + "`n...[truncated]"
}

function Require-Path {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Name, [switch]$Directory)
    $kind = if ($Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $Path -PathType $kind)) {
        throw "CROSS_STACK_PREREQUISITE_MISSING: $Name is missing: $Path"
    }
}

function Get-PathResult {
    param([Parameter(Mandatory = $true)]$Object, [Parameter(Mandatory = $true)][string]$Path)
    $current = $Object
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) {
            return [pscustomobject]@{ Exists = $false; Value = $null }
        }
        if ($current -is [Collections.IDictionary]) {
            if (-not $current.Contains($segment)) {
                return [pscustomobject]@{ Exists = $false; Value = $null }
            }
            $current = $current[$segment]
            continue
        }
        if ($null -eq $current.PSObject.Properties[$segment]) {
            return [pscustomobject]@{ Exists = $false; Value = $null }
        }
        $current = $current.PSObject.Properties[$segment].Value
    }
    return [pscustomobject]@{ Exists = $true; Value = $current }
}

function Get-PathValue {
    param([Parameter(Mandatory = $true)]$Object, [Parameter(Mandatory = $true)][string]$Path)
    return (Get-PathResult $Object $Path).Value
}

function Assert-Contract {
    param([Parameter(Mandatory = $true)]$Object, [Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)]$Contract)
    $schema = Get-PathResult $Object 'schemaVersion'
    if (-not $schema.Exists -or [string]$schema.Value -ne [string]$Contract.schemaVersion) {
        throw "CONTRACT_SCHEMA_MISMATCH: $Name expected $($Contract.schemaVersion), received $($schema.Value)"
    }
    foreach ($required in @($Contract.required)) {
        $field = Get-PathResult $Object ([string]$required)
        if (-not $field.Exists) {
            throw "CONTRACT_FIELD_MISSING: $Name.$required"
        }
    }
}

function Assert-Equal {
    param($Actual, $Expected, [Parameter(Mandatory = $true)][string]$Message)
    if ([string]$Actual -ne [string]$Expected) {
        throw "CONTRACT_VALUE_MISMATCH: $Message expected '$Expected', received '$Actual'"
    }
}

function Assert-True {
    param([bool]$Value, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Value) { throw "CONTRACT_ASSERTION_FAILED: $Message" }
}

function Invoke-ProcessBounded {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [int]$TimeoutMilliseconds = 120000
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
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "process did not start: $FileName" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit($TimeoutMilliseconds)
        if (-not $completed) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            try { $process.WaitForExit(5000) } catch { }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = if ($completed -and $process.HasExited) { $process.ExitCode } else { 124 }
            TimedOut = -not $completed
            Stdout = [string]$stdout
            Stderr = [string]$stderr
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = 2
            TimedOut = $false
            Stdout = ''
            Stderr = $_.Exception.Message
            StartError = $_.Exception.Message
        }
    }
    finally {
        $process.Dispose()
    }
}

function Convert-ProcessJson {
    param([Parameter(Mandatory = $true)]$ProcessResult, [Parameter(Mandatory = $true)][string]$Name, [int]$ExpectedExitCode = 0)
    $maxBytes = [int]$manifest.limits.maxResultBytes
    $output = ([string]$ProcessResult.Stdout).Trim()
    $bytes = [Text.Encoding]::UTF8.GetByteCount($output)
    if ($bytes -gt $maxBytes) {
        throw "CROSS_STACK_OUTPUT_LIMIT: $Name emitted $bytes bytes; limit is $maxBytes"
    }
    if ([int]$ProcessResult.ExitCode -ne $ExpectedExitCode) {
        $failureOutput = Limit-Text ((@($ProcessResult.Stderr, $ProcessResult.Stdout) |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) -join "`n")
        throw "CROSS_STACK_PROCESS_FAILED: $Name exited $($ProcessResult.ExitCode) instead of ${ExpectedExitCode}: $failureOutput"
    }
    if ([string]::IsNullOrWhiteSpace($output)) {
        throw "CROSS_STACK_RESPONSE_MISSING: $Name returned no JSON"
    }
    try {
        return $output | ConvertFrom-Json -Depth 40
    }
    catch {
        throw "CROSS_STACK_RESPONSE_INVALID: $Name returned invalid JSON: $($_.Exception.Message)"
    }
}

function Invoke-JsonProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$ExpectedExitCode = 0
    )
    $process = Invoke-ProcessBounded $FileName $Arguments $WorkingDirectory
    return Convert-ProcessJson $process $Name $ExpectedExitCode
}

function Get-GitHead {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Name)
    # The local workspace may contain checkouts owned by the desktop
    # user while this harness runs under a sandbox account.  Scope the Git
    # safe-directory exception to this read-only probe; do not mutate global
    # Git configuration and still require an actual checkout and exact pin.
    $head = & git -c "safe.directory=$Root" -C $Root rev-parse HEAD 2>$null
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string](@($head)[0]))) {
        throw "CROSS_STACK_PIN_INVALID: $Name is not a Git checkout: $Root"
    }
    return ([string](@($head)[0])).Trim().ToLowerInvariant()
}

function Copy-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

try {
    Require-Path $manifestPath 'cross-stack compatibility manifest'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 30
    Assert-Equal $manifest.schemaVersion 'rimtest-cross-stack-compatibility/v1' 'compatibility manifest schema'
    Assert-Equal $manifest.components.rimContext.source 'internal' 'RimContext is an internal component'
    Assert-Equal $manifest.components.rimError.source 'internal' 'RimError is an internal component'
    Assert-True ($null -ne $manifest.repositories.rimLiaison) 'RimLiaison repository metadata exists'
    Assert-True ($null -ne $manifest.repositories.devBridge2) 'DevBridge2 compatibility pin exists'
    Assert-True ([string]$manifest.repositories.devBridge2.revision -match '^[0-9a-fA-F]{40}$') 'DevBridge2 uses a full pinned SHA'
    $devBridgeResolution = Resolve-PinnedDevBridgeWorktree `
        -RimLiaisonRoot $RimLiaisonRoot `
        -DevBridgeRoot $DevBridgeRoot `
        -ManifestPath $manifestPath
    $DevBridgeRoot = [string]$devBridgeResolution.resolvedRoot
    $heads = [ordered]@{
        rimLiaison = Get-GitHead $RimLiaisonRoot 'RimLiaison'
        devBridge2 = Get-GitHead $DevBridgeRoot 'DevBridge2'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
        Assert-Equal $heads.rimLiaison $env:GITHUB_SHA.ToLowerInvariant() 'RimLiaison checkout matches workflow SHA'
    }
    Assert-Equal $heads.devBridge2 ([string]$manifest.repositories.devBridge2.revision).ToLowerInvariant() 'DevBridge2 checkout matches pinned SHA'

    $rimctxExe = Join-Path $RimLiaisonRoot 'src\RimContext.Cli\bin\Release\net8.0\rimctx.exe'
    $rimliaisonExe = Join-Path $RimLiaisonRoot 'src\RimLiaison.Cli\bin\Release\net8.0\rimliaison.exe'
    $rimerrorExe = Join-Path $RimLiaisonRoot 'src\RimError.Cli\bin\Release\net8.0\rimerror.exe'
    Require-Path $rimctxExe 'RimContext Release CLI'
    Require-Path $rimliaisonExe 'RimLiaison Release CLI'
    Require-Path $rimerrorExe 'RimError Release CLI'
    Require-Path (Join-Path $DevBridgeRoot 'Source\Coordinator\bin\Release\net8.0\DevBridge.Coordinator.exe') 'DevBridge2 coordinator Release build'
    Require-Path (Join-Path $DevBridgeRoot 'Source\FakeRimWorld\bin\Release\net8.0\DevBridge.FakeRimWorld.exe') 'DevBridge2 fake process host Release build'

    $fixtureSource = Join-Path $RimLiaisonRoot 'tests\fixtures\cross-stack'
    Require-Path $fixtureSource 'cross-stack fixture' -Directory
    $workspaceRoot = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-cross-stack-' + [Guid]::NewGuid().ToString('N'))
    $auxiliaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-cross-stack-state-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workspaceRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $auxiliaryRoot | Out-Null
    Copy-Item -Path (Join-Path $fixtureSource '*') -Destination $workspaceRoot -Recurse -Force
    $catalogPath = Join-Path $workspaceRoot 'catalog.json'
    $changedPath = 'FixtureMod/Source/FixtureMarker.cs'
    $sourcePath = Join-Path $workspaceRoot ($changedPath.Replace('/', '\'))
    $trackedArtifactPath = 'deployed/CrossStack.Fixture.dll'
    $trackedArtifactFullPath = Join-Path $workspaceRoot ($trackedArtifactPath.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $trackedArtifactFullPath) | Out-Null
    [IO.File]::WriteAllBytes(
        $trackedArtifactFullPath,
        [Text.Encoding]::UTF8.GetBytes('cross-stack-old-tracked-artifact/v1'))
    & git -C $workspaceRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_INIT_FAILED' }
    & git -C $workspaceRoot config user.name 'RimLiaison Cross-Stack Fixture'
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_CONFIG_FAILED' }
    & git -C $workspaceRoot config user.email 'rimliaison-fixture.invalid@example.invalid'
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_CONFIG_FAILED' }
    & git -C $workspaceRoot add --all
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_ADD_FAILED' }
    # Git global excludes commonly ignore DLLs; this fixture intentionally
    # requires the deployed artifact to be tracked so transaction snapshots
    # can prove an owner-only mutation.
    & git -C $workspaceRoot add --force -- $trackedArtifactPath
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_ARTIFACT_ADD_FAILED' }
    & git -C $workspaceRoot commit --quiet -m 'cross-stack fixture baseline'
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_GIT_COMMIT_FAILED' }
    $startingHead = ([string](& git -C $workspaceRoot rev-parse HEAD)).Trim()
    $startingArtifactSha256 = (Get-FileHash -LiteralPath $trackedArtifactFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $activeTransactionBefore = Test-Path -LiteralPath (Join-Path $workspaceRoot '.rimtest-transaction')
    Assert-True (-not $activeTransactionBefore) 'no unrelated active transaction owns the fixture target'
    $sourceText = [IO.File]::ReadAllText($sourcePath)
    Assert-True ($sourceText.Contains('cross-stack-fixture/v1', [StringComparison]::Ordinal)) 'fixture source has deterministic initial marker'
    [IO.File]::WriteAllText(
        $sourcePath,
        $sourceText.Replace('cross-stack-fixture/v1', 'cross-stack-fixture/v2', [StringComparison]::Ordinal),
        [Text.UTF8Encoding]::new($false))
    $startingSourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $startingStatus = @(& git -C $workspaceRoot status --short)
    Assert-Equal $startingStatus.Count 1 'controlled source edit is the only starting worktree mutation'
    Assert-True ([string]$startingStatus[0] -match 'FixtureMod/Source/FixtureMarker\.cs$') 'starting worktree mutation is the controlled source edit'

    $rimctxStore = Join-Path $auxiliaryRoot 'rimctx\index.sqlite'
    $index = Invoke-JsonProcess $rimctxExe @('index', '--root', $workspaceRoot, '--store', $rimctxStore, '--json') $RimLiaisonRoot 'RimContext index'
    Assert-Equal $index.status 'ok' 'RimContext index status'
    $affected = Invoke-JsonProcess $rimctxExe @('affected', $changedPath, '--root', $workspaceRoot, '--store', $rimctxStore, '--json', '--max-bytes', '4096') $RimLiaisonRoot 'RimContext affected'
    Assert-Contract $affected 'RimContext affected' $manifest.contracts.rimContextAffected
    Assert-Equal $affected.status 'ok' 'RimContext affected status'
    $directImpacts = @($affected.data.direct)
    Assert-True (@($directImpacts | Where-Object { [string]$_.kind -eq 'csharp_type' -and [string]$_.name -eq 'CrossStack.FixtureMarker' }).Count -gt 0) 'RimContext affected includes the changed fixture type'

    $wrapperArguments = @(
        'affected', $changedPath, '--json',
        '--catalog', $catalogPath,
        '--rimcontext', $rimctxExe,
        '--rimcontext-root', $workspaceRoot,
        '--rimcontext-store', $rimctxStore
    )
    $rimliaisonWrapper = Join-Path $RimLiaisonRoot 'rimliaison.cmd'
    $rimtestWrapper = Join-Path $RimLiaisonRoot 'rimtest.cmd'
    Require-Path $rimliaisonWrapper 'canonical rimliaison wrapper'
    Require-Path $rimtestWrapper 'legacy rimtest wrapper'
    $canonicalWrapper = Invoke-ProcessBounded $env:ComSpec (@('/d', '/c', $rimliaisonWrapper) + $wrapperArguments) $RimLiaisonRoot
    $legacyWrapper = Invoke-ProcessBounded $env:ComSpec (@('/d', '/c', $rimtestWrapper) + $wrapperArguments) $RimLiaisonRoot
    $canonicalSelection = Convert-ProcessJson $canonicalWrapper 'rimliaison affected wrapper'
    $legacySelection = Convert-ProcessJson $legacyWrapper 'rimtest affected compatibility wrapper'
    Assert-Equal ($canonicalSelection | ConvertTo-Json -Depth 40 -Compress) ($legacySelection | ConvertTo-Json -Depth 40 -Compress) 'canonical and legacy affected wrappers produce equivalent JSON'

    $fakeRoot = Join-Path $auxiliaryRoot 'fake-devbridge'
    New-Item -ItemType Directory -Force -Path (Join-Path $fakeRoot 'scripts'), (Join-Path $fakeRoot 'DevelopmentProjects') | Out-Null
    Copy-Item -LiteralPath (Join-Path $RimLiaisonRoot 'scripts\cross-stack-fake-mod-development.ps1') -Destination (Join-Path $fakeRoot 'scripts\mod-test.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $RimLiaisonRoot 'scripts\cross-stack-fake-devbridge.ps1') -Destination (Join-Path $fakeRoot 'scripts\cross-stack-fake-devbridge.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $RimLiaisonRoot 'scripts\cross-stack-fake-devbridge.cmd') -Destination (Join-Path $fakeRoot 'DevBridge.cmd') -Force
    $descriptor = [ordered]@{
        schemaVersion = 'devbridge-mod-development/v1'
        project = 'frontier'
        sourceProject = 'FixtureMod/CrossStack.Fixture.csproj'
        configuration = 'Release'
        expectedAssembly = 'CrossStack.Fixture.dll'
        deploymentTarget = 'deployed/CrossStack.Fixture.dll'
        testRecipe = 'cross-stack-fixture'
    }
    Copy-JsonFile (Join-Path $fakeRoot 'DevelopmentProjects\frontier.json') $descriptor

    $show = Invoke-JsonProcess (Join-Path $fakeRoot 'DevBridge.cmd') @('--root', $fakeRoot, 'test', 'recipe', 'show', 'cross-stack-fixture', '--json') $fakeRoot 'DevBridge recipe show'
    Assert-Equal $show.schemaVersion $manifest.contracts.devBridgeRecipeRun.schemaVersion.Replace('run', 'show') 'DevBridge show schema'
    Assert-Equal $show.recipe.id 'cross-stack-fixture' 'DevBridge show recipe id'
    $plan = Invoke-JsonProcess (Join-Path $fakeRoot 'DevBridge.cmd') @('--root', $fakeRoot, 'test', 'recipe', 'plan', 'cross-stack-fixture', '--json') $fakeRoot 'DevBridge recipe plan'
    Assert-Equal $plan.schemaVersion 'devbridge-test-recipe-plan/v1' 'DevBridge plan schema'
    Assert-True (@($plan.steps).Count -eq 1) 'DevBridge plan contains one bounded step'

    $capabilityResult = Invoke-JsonProcess (Join-Path $fakeRoot 'DevBridge.cmd') @(
        '--root', $fakeRoot, 'bridge', 'tools', '--json'
    ) $fakeRoot 'DevBridge capability response'
    Assert-Contract $capabilityResult 'DevBridge capability response' $manifest.contracts.devBridgeCapabilities
    Assert-True ([bool]$capabilityResult.success) 'DevBridge capability response succeeds'
    Assert-True (@($capabilityResult.result.tools).Count -eq 1) 'DevBridge capability response contains one fixture tool'

    $rimliaison = Invoke-ProcessBounded $rimliaisonExe @(
        'affected', '--run', '--json',
        '--catalog', $catalogPath,
        '--rimcontext', $rimctxExe,
        '--rimcontext-root', $workspaceRoot,
        '--rimcontext-store', $rimctxStore,
        '--devbridge', (Join-Path $fakeRoot 'DevBridge.cmd'),
        '--devbridge-root', $fakeRoot,
        '--devbridge-project', 'frontier'
    ) $RimLiaisonRoot
    $suite = Convert-ProcessJson $rimliaison 'RimLiaison affected --run'
    Assert-Contract $suite 'RimLiaison suite result' $manifest.contracts.rimTestSuite
    Assert-Equal $suite.status 'pass' 'RimLiaison synthetic workflow status'
    Assert-Equal $suite.suite 'affected' 'RimLiaison synthetic suite id'
    Assert-Equal $suite.passed 1 'RimLiaison selected one affected test'
    Assert-Equal $suite.failed 0 'RimLiaison reported no failed tests'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$suite.workflowId)) 'workflow identity reaches RimLiaison result'
    $workflowId = [string]$suite.workflowId
    $freshness = $suite.artifactFreshness
    $operationId = [string](@($freshness.operationIds)[0])
    $transactionPath = Join-Path $fakeRoot '.cross-stack-mod-development.json'
    Require-Path $transactionPath 'DevBridge mod-development response fixture'
    $transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json -Depth 40
    Assert-Contract $transaction 'DevBridge mod-development response' $manifest.contracts.devBridgeModDevelopment
    Assert-True ([bool]$transaction.success) 'DevBridge mod-development transaction succeeds'
    Assert-Equal $transaction.workflowId $workflowId 'workflow identity reaches DevBridge mod-development'
    Assert-Equal $transaction.artifactFreshness.workflowId $workflowId 'workflow identity reaches artifact freshness'
    Assert-True ([string]$transaction.artifactFreshness.sourceFingerprint -match '^[0-9a-fA-F]{64}$') 'source fingerprint is present'
    $recipeRunPath = Join-Path $fakeRoot '.cross-stack-recipe-run.json'
    Require-Path $recipeRunPath 'DevBridge recipe-run response fixture'
    $recipeRun = Get-Content -LiteralPath $recipeRunPath -Raw | ConvertFrom-Json -Depth 40
    Assert-Contract $recipeRun 'DevBridge recipe-run response' $manifest.contracts.devBridgeRecipeRun
    Assert-True ([bool]$recipeRun.success) 'DevBridge recipe run succeeds'
    Assert-Equal $recipeRun.workflowId $workflowId 'workflow identity reaches DevBridge recipe run'
    Assert-Equal $recipeRun.runId $freshness.runId 'run identity reaches DevBridge recipe run'
    Assert-Equal $recipeRun.generation $freshness.generation 'generation identity reaches DevBridge recipe run'
    Assert-True (@($recipeRun.operations | Where-Object { [string]$_.operationId -eq [string]$operationId }).Count -gt 0) 'operation identity reaches DevBridge recipe run'
    Assert-True ([bool]$freshness.loadedArtifactFreshnessProven) 'RimLiaison requires proven artifact freshness'
    Assert-True ([string]$freshness.builtArtifactSha256 -match '^[0-9a-fA-F]{64}$') 'built artifact SHA-256 is present'
    Assert-Equal $freshness.builtArtifactSha256 $freshness.deployedArtifactSha256 'built and deployed artifact hashes agree'
    Assert-Equal $freshness.deploymentDecision 'deployed' 'the controlled source edit deploys a new tracked artifact'
    Assert-True ([bool]$freshness.sourceInputsStable) 'source inputs remain stable while the owner updates the tracked artifact'
    Assert-Equal @($freshness.buildOwnedOutputChanges).Count 1 'one tracked output mutation is classified as build-owned'
    Assert-Equal $freshness.buildOwnedOutputChanges[0].path $trackedArtifactPath 'build-owned output path comes from the validated descriptor'
    Assert-Equal $freshness.buildOwnedOutputChanges[0].sha256 $freshness.builtArtifactSha256 'build-owned output bytes match the owner build hash'
    Assert-True ([string]::IsNullOrWhiteSpace([string]$freshness.errorCode)) 'the owned tracked output does not emit a transaction-integrity error'
    Assert-True ([int]$freshness.generation -gt 0) 'generation identity is present'
    Assert-Equal $freshness.workflowId $workflowId 'workflow identity reaches artifact freshness'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$freshness.runId)) 'run identity reaches artifact freshness'
    Assert-True (@($freshness.operationIds).Count -gt 0) 'operation identity reaches artifact freshness'
    $deployedArtifactSha256 = (Get-FileHash -LiteralPath $trackedArtifactFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Equal $deployedArtifactSha256 $freshness.builtArtifactSha256 'tracked worktree artifact matches the built and deployed hash'
    Assert-True ($startingArtifactSha256 -ne $deployedArtifactSha256) 'the canonical invocation produced new artifact bytes'
    $acceptedStatus = @(& git -C $workspaceRoot status --short)
    Assert-Equal @($acceptedStatus).Count 2 'the final worktree contains the source edit and its tracked output'
    Assert-True (@($acceptedStatus | Where-Object { [string]$_ -match 'FixtureMod/Source/FixtureMarker\.cs$' }).Count -eq 1) 'the controlled source edit remains present'
    Assert-True (@($acceptedStatus | Where-Object { [string]$_ -match 'deployed/CrossStack\.Fixture\.dll$' }).Count -eq 1) 'the owner-produced tracked artifact mutation remains present'

    $logsQuery = Invoke-JsonProcess (Join-Path $fakeRoot 'DevBridge.cmd') @(
        '--root', $fakeRoot, 'logs', 'query',
        '--generation', [string]$freshness.generation,
        '--since-launch', '--severity', 'ERROR', '--limit', '64', '--json'
    ) $fakeRoot 'DevBridge bounded logs query'
    Assert-Contract $logsQuery 'DevBridge logs query' $manifest.contracts.devBridgeLogsQuery
    Assert-Equal $logsQuery.generation $freshness.generation 'logs query generation identity'
    Assert-True ([bool]$logsQuery.available) 'bounded logs query is available for the fake generation'

    # A distinct negative transaction uses the same canonical Git-discovered
    # entrypoint but asks the fake build owner to mutate a source file after
    # the transaction snapshot. The output mutation remains owner-proven; the
    # source mutation must still reject the transaction.
    & git -C $workspaceRoot add --all
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_NEGATIVE_BASELINE_ADD_FAILED' }
    & git -C $workspaceRoot commit --quiet -m 'accepted tracked artifact baseline'
    if ($LASTEXITCODE -ne 0) { throw 'CROSS_STACK_NEGATIVE_BASELINE_COMMIT_FAILED' }
    $negativeBaselineHead = ([string](& git -C $workspaceRoot rev-parse HEAD)).Trim()
    $sourceV2 = [IO.File]::ReadAllText($sourcePath)
    [IO.File]::WriteAllText(
        $sourcePath,
        $sourceV2.Replace('cross-stack-fixture/v2', 'cross-stack-fixture/v3', [StringComparison]::Ordinal),
        [Text.UTF8Encoding]::new($false))
    $previousMutationPath = $env:RIMLIAISON_CROSS_STACK_MUTATION_PATH
    try {
        $env:RIMLIAISON_CROSS_STACK_MUTATION_PATH = $sourcePath
        $negativeProcess = Invoke-ProcessBounded $rimliaisonExe @(
            'affected', '--run', '--json',
            '--catalog', $catalogPath,
            '--rimcontext', $rimctxExe,
            '--rimcontext-root', $workspaceRoot,
            '--rimcontext-store', $rimctxStore,
            '--devbridge', (Join-Path $fakeRoot 'DevBridge.cmd'),
            '--devbridge-root', $fakeRoot,
            '--devbridge-project', 'frontier'
        ) $RimLiaisonRoot
    } finally {
        $env:RIMLIAISON_CROSS_STACK_MUTATION_PATH = $previousMutationPath
    }
    $negativeSuite = Convert-ProcessJson $negativeProcess 'RimLiaison unexpected source mutation' 10
    Assert-Equal $negativeSuite.status 'infrastructure' 'unexpected source mutation is an infrastructure integrity failure'
    Assert-Equal $negativeSuite.failures[0].errorCode 'RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION' 'unexpected source mutation retains the transaction-integrity error'
    Assert-Equal $negativeSuite.artifactFreshness.errorCode 'RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION' 'negative freshness evidence retains the transaction-integrity error'
    Assert-True (-not [bool]$negativeSuite.artifactFreshness.loadedArtifactFreshnessProven) 'rejected source mutation cannot claim runtime freshness'
    Assert-Equal $negativeSuite.orchestration.runtimeValidation 'BLOCKED' 'runtime validation is blocked after the rejected mutation'

    $capabilityFixturePath = Join-Path $fakeRoot '.cross-stack-capabilities.json'
    Require-Path $capabilityFixturePath 'DevBridge capability response fixture'
    $capabilityFixture = Get-Content -LiteralPath $capabilityFixturePath -Raw | ConvertFrom-Json -Depth 40
    Assert-Contract $capabilityFixture 'DevBridge capability response fixture' $manifest.contracts.devBridgeCapabilities
    $rimliaisonCapabilities = Invoke-JsonProcess $rimliaisonExe @(
        'capabilities', '--json',
        '--devbridge', (Join-Path $fakeRoot 'DevBridge.cmd'),
        '--devbridge-root', $fakeRoot,
        '--limit', '10'
    ) $RimLiaisonRoot 'RimLiaison capabilities'
    Assert-Contract $rimliaisonCapabilities 'RimLiaison capability result' $manifest.contracts.rimTestCapabilities
    Assert-Equal $rimliaisonCapabilities.status 'ok' 'RimLiaison capability status'
    Assert-Equal $rimliaisonCapabilities.count 1 'RimLiaison capability count'
    Assert-Equal $rimliaisonCapabilities.capabilities[0].id 'rimworld/inspect_fixture' 'RimLiaison capability id'

    # Exercise the actual pinned DevBridge2 mod-test serializer with a
    # synthetic compiler failure before feeding its JSON through the
    # RimLiaison parser and observability export test. The fake host above
    # remains the bounded lifecycle composition fixture; this block proves
    # that the authoritative build owner emits the richer failure contract.
    $devBridgeDiagnosticScript = Join-Path $DevBridgeRoot 'scripts\process-e2e.tests.ps1'
    Require-Path $devBridgeDiagnosticScript 'DevBridge2 diagnostic contract test'
    $devBridgeDiagnosticPath = Join-Path $workspaceRoot 'devbridge-build-failure.json'
    $devBridgeDiagnosticProcess = Invoke-ProcessBounded 'pwsh' @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $devBridgeDiagnosticScript,
        '-OnlyBuildFailure', '-DiagnosticFixturePath', $devBridgeDiagnosticPath
    ) $DevBridgeRoot 900000
    if ($devBridgeDiagnosticProcess.ExitCode -ne 0) {
        throw "CROSS_STACK_DEVBRIDGE_DIAGNOSTIC_FAILED: $(Limit-Text ((@($devBridgeDiagnosticProcess.Stderr, $devBridgeDiagnosticProcess.Stdout) | Where-Object { $_ }) -join "`n") 4096)"
    }
    Require-Path $devBridgeDiagnosticPath 'DevBridge2 generated build-failure response'
    $devBridgeBuildFailure = Get-Content -LiteralPath $devBridgeDiagnosticPath -Raw | ConvertFrom-Json -Depth 40
    Assert-Contract $devBridgeBuildFailure 'DevBridge2 build-failure response' $manifest.contracts.devBridgeModDevelopmentFailure
    Assert-Equal $devBridgeBuildFailure.stage 'build' 'DevBridge2 build-failure stage'
    Assert-Equal $devBridgeBuildFailure.failure.stage 'build' 'DevBridge2 nested failure stage'
    Assert-Equal $devBridgeBuildFailure.failure.errorCode 'DEVELOPMENT_BUILD_FAILED' 'DevBridge2 primary build error code'
    Assert-Equal $devBridgeBuildFailure.build.errorCode 'DEVELOPMENT_BUILD_FAILED' 'DevBridge2 build error code'
    Assert-Equal $devBridgeBuildFailure.build.command $devBridgeBuildFailure.failure.command 'DevBridge2 preserves one exact build command'
    Assert-True ([string]$devBridgeBuildFailure.build.output -match '(?i)(error\s+(CS|MSB)|CS\d{4}|MSB\d{4})') 'DevBridge2 preserves compiler output'
    Assert-True ([string]$devBridgeBuildFailure.failure.output -match '(?i)(error\s+(CS|MSB)|CS\d{4}|MSB\d{4})') 'DevBridge2 failure projection preserves compiler output'
    Assert-True (([string]$devBridgeBuildFailure.build.output).Length -le [int]$manifest.limits.maxDevBridgeBuildOutputCharacters) 'DevBridge2 build output remains bounded'
    Assert-Equal $devBridgeBuildFailure.build.outputTruncated $devBridgeBuildFailure.failure.outputTruncated 'DevBridge2 truncation state is repeated consistently'
    if ([bool]$devBridgeBuildFailure.build.outputTruncated) {
        Assert-True ([string]$devBridgeBuildFailure.build.output -match '\[truncated to') 'DevBridge2 build output exposes an explicit truncation marker'
        Assert-True ([string]$devBridgeBuildFailure.failure.output -match '\[truncated to') 'DevBridge2 failure output exposes an explicit truncation marker'
    }
    Assert-Equal $devBridgeBuildFailure.build.transactionId $devBridgeBuildFailure.transactionId 'DevBridge2 build transaction identity'
    Assert-Equal $devBridgeBuildFailure.build.workflowId $devBridgeBuildFailure.workflowId 'DevBridge2 build workflow identity'
    Assert-Equal $devBridgeBuildFailure.failure.transactionId $devBridgeBuildFailure.transactionId 'DevBridge2 failure transaction identity'
    Assert-Equal $devBridgeBuildFailure.failure.workflowId $devBridgeBuildFailure.workflowId 'DevBridge2 failure workflow identity'

    $rimliaisonTestsExe = Join-Path $RimLiaisonRoot 'tests\RimLiaison.Tests\bin\Release\net8.0\RimLiaison.Tests.exe'
    Require-Path $rimliaisonTestsExe 'RimLiaison focused cross-stack test executable'
    $previousDiagnosticFixture = $env:RIMLIAISON_DEVBRIDGE_DIAGNOSTIC_FIXTURE
    try {
        $env:RIMLIAISON_DEVBRIDGE_DIAGNOSTIC_FIXTURE = $devBridgeDiagnosticPath
        $rimliaisonDiagnosticProcess = Invoke-ProcessBounded $rimliaisonTestsExe @(
            '--filter', 'pinned DevBridge build diagnostics cross the real wire boundary'
        ) $RimLiaisonRoot 300000
    } finally {
        $env:RIMLIAISON_DEVBRIDGE_DIAGNOSTIC_FIXTURE = $previousDiagnosticFixture
    }
    if ($rimliaisonDiagnosticProcess.ExitCode -ne 0) {
        throw "CROSS_STACK_RIMLIAISON_DIAGNOSTIC_FAILED: $(Limit-Text ((@($rimliaisonDiagnosticProcess.Stderr, $rimliaisonDiagnosticProcess.Stdout) | Where-Object { $_ }) -join "`n") 4096)"
    }
    $diagnosticCrossStack = [ordered]@{
        source = 'DevBridge2/scripts/process-e2e.tests.ps1 -OnlyBuildFailure'
        responseSchema = [string]$devBridgeBuildFailure.schemaVersion
        errorCode = [string]$devBridgeBuildFailure.failure.errorCode
        compilerOutputPreserved = [bool](-not [string]::IsNullOrWhiteSpace([string]$devBridgeBuildFailure.build.output))
        outputTruncated = [bool]$devBridgeBuildFailure.build.outputTruncated
        outputLimitCharacters = [int]$manifest.limits.maxDevBridgeBuildOutputCharacters
        rimLiaisonWireTest = 'pass'
    }

    $brokenSuite = ($suite | ConvertTo-Json -Depth 40 | ConvertFrom-Json -Depth 40)
    $brokenSuite.PSObject.Properties.Remove('artifactFreshness')
    $contractBreakDetected = $false
    $contractBreakMessage = $null
    try {
        Assert-Contract $brokenSuite 'intentional RimLiaison contract break' $manifest.contracts.rimTestSuite
        throw 'intentional contract break was not detected'
    }
    catch {
        if ($_.Exception.Message -like 'CONTRACT_FIELD_MISSING:*') {
            $contractBreakDetected = $true
            $contractBreakMessage = Limit-Text $_.Exception.Message 256
        } else {
            throw
        }
    }
    Assert-True $contractBreakDetected 'intentional required-field contract break is detected'

    $runId = [string]$freshness.runId
    $generation = [int]$freshness.generation
    $diagnosticLog = Join-Path $workspaceRoot 'controlled-diagnostic.log'
    [IO.File]::WriteAllText(
        $diagnosticLog,
        "Exception in rimworld/inspect_fixture: System.InvalidOperationException: controlled cross-stack diagnostic`r`n  at CrossStack.FixtureMarker.Tick() in FixtureMarker.cs:line 5`r`n",
        [Text.UTF8Encoding]::new($false))
    $integrationPath = Join-Path $workspaceRoot 'rimerror-integration.json'
    $integration = [ordered]@{
        schemaVersion = 'rimerror-integration/v1'
        devBridge = [ordered]@{
            schemaVersion = 'devbridge-generation-context/v1'
            workflowId = $workflowId
            runId = $runId
            testId = 'cross-stack-live-test'
            launchId = 'launch-cross-stack-contract-v1'
            generation = $generation
            evidence = 'evidence-cross-stack-contract-v1'
        }
        rimBridge = [ordered]@{
            workflowId = $workflowId
            operations = @([ordered]@{
                operationId = $operationId
                operationName = 'rimworld/inspect_fixture'
                status = 'Completed'
                success = $true
                workflowId = $workflowId
                runId = $runId
                launchId = 'launch-cross-stack-contract-v1'
                generation = $generation
            })
        }
    }
    Copy-JsonFile $integrationPath $integration
    Assert-Contract $integration 'RimError integration envelope' $manifest.contracts.rimErrorIntegration
    $integrationBytes = (Get-Item -LiteralPath $integrationPath).Length
    Assert-True ($integrationBytes -le [int]$manifest.limits.maxIntegrationBytes) 'RimError integration input is bounded'
    $diagnosticStore = Join-Path $workspaceRoot '.rimerror\latest.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $diagnosticStore) | Out-Null
    $ingest = Invoke-ProcessBounded $rimerrorExe @(
        'ingest', $diagnosticLog,
        '--store', $diagnosticStore,
        '--integration', $integrationPath,
        '--run', $runId,
        '--test', 'cross-stack-live-test',
        '--operation', $operationId,
        '--operation-name', 'rimworld/inspect_fixture',
        '--json'
    ) $RimLiaisonRoot
    $ingestReport = Convert-ProcessJson $ingest 'RimError ingest' 1
    Assert-Equal $ingestReport.status 'fail' 'RimError reports the controlled diagnostic'
    $latestReport = Invoke-JsonProcess $rimerrorExe @('latest', '--json', '--all', '--store', $diagnosticStore) $RimLiaisonRoot 'RimError latest' 1
    Assert-Equal $latestReport.status 'fail' 'RimError latest retains the controlled diagnostic'
    $rootCauses = @($latestReport.rootCauses)
    Assert-True ($rootCauses.Count -gt 0) 'RimError returns a root cause'
    $diagnosticId = [string]$rootCauses[0].id
    Assert-True (-not [string]::IsNullOrWhiteSpace($diagnosticId)) 'RimError returns a diagnostic id'
    $showDiagnostic = Invoke-JsonProcess $rimerrorExe @('show', $diagnosticId, '--store', $diagnosticStore) $RimLiaisonRoot 'RimError show'
    $shownRun = Get-PathValue $showDiagnostic 'run'
    if ([string]::IsNullOrWhiteSpace([string]$shownRun)) { $shownRun = Get-PathValue $showDiagnostic 'runId' }
    $shownOperation = Get-PathValue $showDiagnostic 'operation'
    if ([string]::IsNullOrWhiteSpace([string]$shownOperation)) { $shownOperation = Get-PathValue $showDiagnostic 'operationId' }
    if ([string]::IsNullOrWhiteSpace([string]$shownOperation)) { $shownOperation = Get-PathValue $showDiagnostic 'op' }
    Assert-Equal $shownRun $runId 'RimError diagnostic retains run identity'
    Assert-Equal $shownOperation $operationId 'RimError diagnostic retains operation identity'
    $showBytes = [Text.Encoding]::UTF8.GetByteCount(($showDiagnostic | ConvertTo-Json -Depth 40 -Compress))
    Assert-True ($showBytes -le [int]$manifest.limits.maxDiagnosticBytes) 'RimError diagnostic output is bounded'

    $report = [ordered]@{
        schemaVersion = 'rimtest-cross-stack-gate/v1'
        status = 'pass'
        repositories = $heads
        devBridge2Resolution = if ($null -eq $devBridgeResolution) {
            $null
        } else {
            [ordered]@{
                pinnedRevision = [string]$devBridgeResolution.pinnedRevision
                resolvedRoot = [string]$devBridgeResolution.resolvedRoot
                requestedRoot = [string]$devBridgeResolution.requestedRoot
                materialization = [string]$devBridgeResolution.materialization
                usedNormalCheckout = [bool]$devBridgeResolution.usedNormalCheckout
            }
        }
        fixture = [ordered]@{
            id = 'cross-stack-fixture'
            changedPath = $changedPath
            selectedTests = @('cross-stack-live-test')
        }
        workflowId = $workflowId
        runId = $runId
        generation = $generation
        operationIds = @($freshness.operationIds)
        artifactFreshness = [ordered]@{
            deploymentDecision = [string]$freshness.deploymentDecision
            builtArtifactSha256 = [string]$freshness.builtArtifactSha256
            deployedArtifactSha256 = [string]$freshness.deployedArtifactSha256
            loadedArtifactFreshnessProven = [bool]$freshness.loadedArtifactFreshnessProven
            sourceInputsStable = [bool]$freshness.sourceInputsStable
            buildOwnedOutputChanges = @($freshness.buildOwnedOutputChanges)
        }
        trackedArtifactTransaction = [ordered]@{
            startingHead = $startingHead
            startingSourceSha256 = $startingSourceSha256
            startingArtifactSha256 = $startingArtifactSha256
            finalArtifactSha256 = $deployedArtifactSha256
            activeTransactionBefore = $activeTransactionBefore
            startingStatus = @($startingStatus)
            finalAcceptedStatus = @($acceptedStatus)
            canonicalInvocationCount = 1
            automaticSecondRun = $false
            priorRequiredInvocationCount = 2
            mutationClassification = 'build-owned-output'
            negativeBaselineHead = $negativeBaselineHead
            negativeErrorCode = [string]$negativeSuite.failures[0].errorCode
        }
        diagnostic = [ordered]@{
            id = $diagnosticId
            status = [string]$ingestReport.status
            runId = $runId
            operationId = $operationId
            correlationVerified = $true
        }
        devBridgeBuildDiagnostics = $diagnosticCrossStack
        contracts = [ordered]@{
            rimContextAffected = [string]$affected.schemaVersion
            devBridgeModDevelopment = [string]$manifest.contracts.devBridgeModDevelopment.schemaVersion
            devBridgeRecipeRun = [string]$manifest.contracts.devBridgeRecipeRun.schemaVersion
            devBridgeCapabilities = [string]$manifest.contracts.devBridgeCapabilities.schemaVersion
            devBridgeLogsQuery = [string]$manifest.contracts.devBridgeLogsQuery.schemaVersion
            rimErrorIntegration = [string]$integration.schemaVersion
            rimTestSuite = [string]$suite.schemaVersion
            rimTestCapabilities = [string]$rimliaisonCapabilities.schemaVersion
        }
        bounded = [ordered]@{
            rimTestBytes = [Text.Encoding]::UTF8.GetByteCount(([string]$rimliaison.Stdout).Trim())
            rimErrorShowBytes = $showBytes
            integrationBytes = $integrationBytes
            maxResultBytes = [int]$manifest.limits.maxResultBytes
        }
        contractBreakProbe = [ordered]@{
            detected = $contractBreakDetected
            error = $contractBreakMessage
        }
        realRimWorldSmoke = 'Not covered here; see DevBridge2/scripts/live-stack-smoke.ps1.'
    }
}
catch {
    $exitCode = 1
    $message = [string]$_.Exception.Message
    $code = if ($message -match '^([A-Z][A-Z0-9_]+):') { $Matches[1] } else { 'CROSS_STACK_GATE_FAILED' }
    $report = [ordered]@{
        schemaVersion = 'rimtest-cross-stack-gate/v1'
        status = 'fail'
        errorCode = $code
        error = Limit-Text $message
        repositories = [ordered]@{
            rimLiaison = $RimLiaisonRoot
            devBridge2 = $DevBridgeRoot
        }
        devBridge2Resolution = if ($null -eq $devBridgeResolution) {
            $null
        } else {
            [ordered]@{
                pinnedRevision = [string]$devBridgeResolution.pinnedRevision
                resolvedRoot = [string]$devBridgeResolution.resolvedRoot
                requestedRoot = [string]$devBridgeResolution.requestedRoot
                materialization = [string]$devBridgeResolution.materialization
                usedNormalCheckout = [bool]$devBridgeResolution.usedNormalCheckout
            }
        }
    }
}
finally {
    if (-not $KeepWorkspace -and $null -ne $workspaceRoot -and (Test-Path -LiteralPath $workspaceRoot)) {
        Remove-Item -LiteralPath $workspaceRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $KeepWorkspace -and $null -ne $auxiliaryRoot -and (Test-Path -LiteralPath $auxiliaryRoot)) {
        Remove-Item -LiteralPath $auxiliaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

[Console]::Out.WriteLine(($report | ConvertTo-Json -Depth 30 -Compress))
exit $exitCode
