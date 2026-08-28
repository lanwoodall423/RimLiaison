[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Project,

    [string]$DescriptorPath,
    [string]$CoordinatorRoot,
    [string[]]$DevelopmentRoot,
    [string[]]$AdditionalDevelopmentRoot,
    [string]$DeploymentRoot,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$SourceFingerprint,
    [string]$WorkflowId,
    [switch]$SkipRecipe,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$scriptRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$coordinatorRoot = if ([string]::IsNullOrWhiteSpace($CoordinatorRoot)) {
    $scriptRoot
} else {
    [IO.Path]::GetFullPath($CoordinatorRoot)
}
$developmentRoots = @($DevelopmentRoot | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { [IO.Path]::GetFullPath($_) })
if ($developmentRoots.Count -eq 0) {
    $developmentRoots = @($scriptRoot)
}
$deploymentRoot = if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $developmentRoots[0]
} else {
    [IO.Path]::GetFullPath($DeploymentRoot)
}
$workflow = if ([string]::IsNullOrWhiteSpace($WorkflowId)) {
    'workflow-cross-stack-contract-v1'
} else {
    $WorkflowId
}
$transactionId = 'tx-cross-stack-' + [Guid]::NewGuid().ToString('N')
$statePath = Join-Path $coordinatorRoot '.cross-stack-state.json'
$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-cross-stack-build-' + $transactionId)
$stagingRoot = Join-Path $transactionRoot 'staging'
$script:ResultPath = Join-Path $coordinatorRoot '.cross-stack-mod-development.json'

function Limit-Text {
    param([AllowNull()][string]$Text, [int]$Limit = 2048)
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $value = $Text.Trim()
    if ($value.Length -le $Limit) { return $value }
    return $value.Substring(0, $Limit) + "`n...[truncated]"
}

function Write-Result {
    param([Parameter(Mandatory = $true)]$Value, [int]$ExitCode = 0)
    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    if (-not [string]::IsNullOrWhiteSpace($script:ResultPath)) {
        [IO.File]::WriteAllText($script:ResultPath, $json, [Text.UTF8Encoding]::new($false))
    }
    [Console]::Out.WriteLine($json)
    exit $ExitCode
}

function Invoke-DotnetBuild {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $dotnet = [string]((Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1).Source)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $developmentRoots[0]
    $isolatedAppData = Join-Path $transactionRoot 'AppData'
    $isolatedDotnetHome = Join-Path $transactionRoot 'DotnetHome'
    New-Item -ItemType Directory -Force -Path $isolatedAppData, $isolatedDotnetHome | Out-Null
    # NuGet resolves the per-user config from APPDATA before evaluating the
    # explicit config file. Isolate that location and the CLI home while
    # retaining the normal package cache (which CI can cache).
    $start.Environment['APPDATA'] = $isolatedAppData
    $start.Environment['DOTNET_CLI_HOME'] = $isolatedDotnetHome
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit(120000)
    if (-not $completed) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($completed) { $process.ExitCode } else { 124 }
    $process.Dispose()
    return [pscustomobject]@{
        exitCode = $exitCode
        output = Limit-Text ((@($stdout, $stderr) | Where-Object { $_ }) -join "`n")
        timedOut = -not $completed
    }
}

function Get-Hash {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

try {
    if ([string]::IsNullOrWhiteSpace($SourceFingerprint)) {
        throw 'source fingerprint is required'
    }
    $descriptorPath = if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
        Join-Path $coordinatorRoot ('DevelopmentProjects\' + $Project + '.json')
    } else {
        [IO.Path]::GetFullPath($DescriptorPath)
    }
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        throw "descriptor not found: $descriptorPath"
    }
    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json -Depth 12
    if ([string]$descriptor.schemaVersion -ne 'devbridge-mod-development/v1' -or
        [string]$descriptor.project -ne $Project) {
        throw 'descriptor schema or project does not match the transaction'
    }
    $sourceRelative = ([string]$descriptor.sourceProject).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $sourceProject = Join-Path $developmentRoots[0] $sourceRelative
    if (-not (Test-Path -LiteralPath $sourceProject -PathType Leaf)) {
        throw "source project not found below the fixture workspace: $sourceRelative"
    }
    $targetRelative = ([string]$descriptor.deploymentTarget).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($targetRelative) -or $targetRelative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'deployment target must be a bounded relative path'
    }
    $target = Join-Path $deploymentRoot $targetRelative
    $targetParent = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $stagingRoot, $targetParent | Out-Null

    # Keep this deterministic fixture independent of a user's protected or
    # machine-wide NuGet.Config. The project has no package dependencies, so
    # an empty local source configuration is sufficient for restore.
    $nugetConfig = Join-Path $transactionRoot 'NuGet.Config'
    $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE')
    $packageCache = if ([string]::IsNullOrWhiteSpace($userProfile)) {
        $null
    } else {
        Join-Path $userProfile '.nuget\packages'
    }
    $packageSource = if (-not [string]::IsNullOrWhiteSpace($packageCache) -and
        (Test-Path -LiteralPath $packageCache -PathType Container)) {
        '<add key="local-package-cache" value="' +
            $packageCache.Replace('&', '&amp;') + '" />'
    } else {
        ''
    }
    $packageSource += '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    [IO.File]::WriteAllText(
        $nugetConfig,
        '<configuration><packageSources><clear />' + $packageSource +
            '</packageSources></configuration>',
        [Text.UTF8Encoding]::new($false))
    $buildProperties = @(
        ('-p:BaseIntermediateOutputPath=' + (Join-Path $transactionRoot 'obj\')),
        ('-p:MSBuildProjectExtensionsPath=' + (Join-Path $transactionRoot 'obj\')),
        ('-p:RestoreConfigFile=' + $nugetConfig)
    )
    $restoreArguments = @(
        'restore', $sourceProject,
        '--configfile', $nugetConfig,
        '--nologo',
        '--verbosity', 'quiet'
    ) + $buildProperties
    $restore = Invoke-DotnetBuild $restoreArguments
    if ([int]$restore.exitCode -ne 0) {
        $build = $restore
    } else {
        $buildArguments = @(
        'build', $sourceProject,
        '--configuration', 'Release',
        '--no-restore',
        '--output', $stagingRoot,
        '--nologo',
        '--verbosity', 'quiet'
        ) + $buildProperties
        $build = Invoke-DotnetBuild $buildArguments
    }
    $expectedArtifact = Join-Path $stagingRoot ([string]$descriptor.expectedAssembly)
    if ([int]$build.exitCode -ne 0 -or -not (Test-Path -LiteralPath $expectedArtifact -PathType Leaf)) {
        $failure = [ordered]@{
            schemaVersion = 'devbridge-mod-development/v1'
            transactionId = $transactionId
            project = $Project
            workflowId = $workflow
            sourceFingerprint = $SourceFingerprint
            success = $false
            stage = 'build'
            exitCode = [int]$build.exitCode
            failure = [ordered]@{
                stage = 'build'
                errorCode = if ($build.timedOut) { 'DEVELOPMENT_BUILD_TIMEOUT' } else { 'DEVELOPMENT_BUILD_FAILED' }
                message = 'The deterministic cross-stack fixture build failed.'
                output = $build.output
            }
            artifactFreshness = [ordered]@{
                sourceFingerprint = $SourceFingerprint
                transactionId = $transactionId
                workflowId = $workflow
                loadedArtifactFreshnessProven = $false
                errorCode = if ($build.timedOut) { 'DEVELOPMENT_BUILD_TIMEOUT' } else { 'DEVELOPMENT_BUILD_FAILED' }
            }
        }
        Write-Result $failure ([Math]::Max(1, [int]$build.exitCode))
    }

    $builtHash = Get-Hash $expectedArtifact
    $deployedBefore = Get-Hash $target
    $state = $null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 12 } catch { $state = $null }
    }
    $unchanged = $null -ne $state -and
        [string]$state.project -eq $Project -and
        [string]$state.artifactSha256 -eq $builtHash -and
        $deployedBefore -eq $builtHash
    $generationBefore = if ($null -ne $state -and [int]$state.generation -gt 0) {
        [int]$state.generation
    } else {
        40
    }
    $generation = if ($unchanged) { $generationBefore } else { $generationBefore + 1 }
    if (-not $unchanged) {
        Copy-Item -LiteralPath $expectedArtifact -Destination $target -Force
    }
    $deployedAfter = Get-Hash $target
    if ($deployedAfter -ne $builtHash) {
        throw 'cross-stack fake deployment hash did not match the staged artifact'
    }

    # The integration gate can opt into one controlled concurrent mutation.
    # Keep this hook fixture-only and bounded to the disposable development
    # root so it cannot touch a user's real mod worktree.
    $mutationPathValue = $env:RIMLIAISON_CROSS_STACK_MUTATION_PATH
    if (-not [string]::IsNullOrWhiteSpace($mutationPathValue)) {
        $mutationPath = [IO.Path]::GetFullPath($mutationPathValue)
        $developmentRoot = [IO.Path]::GetFullPath($developmentRoots[0])
        $developmentPrefix = [IO.Path]::TrimEndingDirectorySeparator($developmentRoot) +
            [IO.Path]::DirectorySeparatorChar
        if (-not $mutationPath.StartsWith($developmentPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $mutationPath -PathType Leaf)) {
            throw 'controlled mutation path must name an existing file in the fixture development root'
        }

        [IO.File]::AppendAllText(
            $mutationPath,
            "`r`n// unexpected transaction-time source mutation`r`n",
            [Text.UTF8Encoding]::new($false))
    }

    $leaseId = 'lease-11111111111111111111111111111111'
    $runId = 'run-cross-stack-contract-v1'
    $operationId = 'op-cross-stack-contract-v1'
    $launchId = 'launch-cross-stack-contract-v1'
    $newState = [ordered]@{
        schemaVersion = 'cross-stack-fake-devbridge-state/v1'
        project = $Project
        sourceFingerprint = $SourceFingerprint
        artifactSha256 = $deployedAfter
        generation = $generation
        workflowId = $workflow
        leaseId = $leaseId
        runId = $runId
        operationId = $operationId
        launchId = $launchId
    }
    [IO.File]::WriteAllText(
        $statePath,
        ($newState | ConvertTo-Json -Depth 12 -Compress),
        [Text.UTF8Encoding]::new($false))

    $result = [ordered]@{
        schemaVersion = 'devbridge-mod-development/v1'
        transactionId = $transactionId
        project = $Project
        workflowId = $workflow
        sourceFingerprint = $SourceFingerprint
        sourceRoot = $developmentRoots[0]
        runtimeRoot = $deploymentRoot
        stagingRoot = $stagingRoot
        success = $true
        stage = 'complete'
        exitCode = 0
        generation = $generation
        leaseId = $leaseId
        artifactFreshness = [ordered]@{
            sourceFingerprint = $SourceFingerprint
            builtArtifactSha256 = $builtHash
            deployedArtifactSha256 = $deployedAfter
            deploymentDecision = if ($unchanged) { 'unchanged' } else { 'deployed' }
            generationBefore = $generationBefore
            generationAfter = $generation
            generation = $generation
            transactionId = $transactionId
            workflowId = $workflow
            leaseId = $leaseId
            loadedArtifactFreshnessProven = $true
            proof = 'deterministic-fake-host-deployment-hash-plus-generation'
            errorCode = $null
        }
    }
    Write-Result $result 0
}
catch {
    $failure = [ordered]@{
        schemaVersion = 'devbridge-mod-development/v1'
        transactionId = $transactionId
        project = $Project
        workflowId = $workflow
        sourceFingerprint = $SourceFingerprint
        success = $false
        stage = 'infrastructure'
        exitCode = 1
        failure = [ordered]@{
            stage = 'infrastructure'
            errorCode = 'CROSS_STACK_FAKE_TRANSACTION_FAILED'
            message = Limit-Text $_.Exception.Message
        }
        artifactFreshness = [ordered]@{
            sourceFingerprint = $SourceFingerprint
            transactionId = $transactionId
            workflowId = $workflow
            loadedArtifactFreshnessProven = $false
            errorCode = 'CROSS_STACK_FAKE_TRANSACTION_FAILED'
        }
    }
    Write-Result $failure 1
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
