[CmdletBinding()]
param(
    [Alias('ChangedPaths', 'Changed')]
    [AllowEmptyCollection()]
    [string[]]$ChangedPath,

    [Alias('BaseRef', 'BaseSha', 'Base')]
    [string]$BaseRevision,

    [Alias('HeadRef', 'HeadSha', 'Head')]
    [string]$HeadRevision,

    [string]$EventName,
    [string]$RepositoryRoot,
    [string]$GitHubOutputPath,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
} else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}

if ([string]::IsNullOrWhiteSpace($GitHubOutputPath) -and
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    $GitHubOutputPath = $env:GITHUB_OUTPUT
}

$maxChangedPaths = 2048
$maxPathLength = 512
$maxSamplePaths = 20
$maxReasonLength = 512

function Add-UniqueString {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$List,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if (-not $List.Contains($Value)) {
        [void]$List.Add($Value)
    }
}

function Test-PathPrefix {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Prefix
    )

    $normalizedPrefix = $Prefix.Trim('/').Replace('\', '/')
    return ([string]::Equals($Path, $normalizedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($normalizedPrefix + '/', [StringComparison]::OrdinalIgnoreCase))
}

function Normalize-ChangedPath {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [pscustomobject]@{ Valid = $false; Path = $null; Reason = 'empty-path' }
    }

    $path = $Value.Trim().Replace('\', '/')
    while ($path.StartsWith('./', [StringComparison]::Ordinal)) {
        $path = $path.Substring(2)
    }

    if ($path.Length -eq 0) {
        return [pscustomobject]@{ Valid = $false; Path = $null; Reason = 'empty-path' }
    }

    if ($path.Length -gt $maxPathLength) {
        return [pscustomobject]@{ Valid = $false; Path = $null; Reason = 'path-too-long' }
    }

    if ($path.Contains([char]13) -or $path.Contains([char]10) -or
        $path.StartsWith('/', [StringComparison]::Ordinal) -or
        $path -match '^[A-Za-z]:') {
        return [pscustomobject]@{ Valid = $false; Path = $null; Reason = 'path-not-repository-relative' }
    }

    $segments = $path.Split('/')
    if (@($segments | Where-Object { $_.Length -eq 0 -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        return [pscustomobject]@{ Valid = $false; Path = $null; Reason = 'path-normalization-ambiguous' }
    }

    return [pscustomobject]@{ Valid = $true; Path = $path; Reason = $null }
}

function Test-DocumentationPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ((Test-PathPrefix $Path '.github/workflows') -or
        (Test-PathPrefix $Path 'contracts') -or
        ((Test-PathPrefix $Path 'scripts') -and $Path -match '\.(ps1|psm1|cmd|bat)$') -or
        (Test-PathPrefix $Path 'fixtures') -or
        (Test-PathPrefix $Path 'tests/fixtures') -or
        (Test-PathPrefix $Path 'TestCatalog') -or
        (Test-PathPrefix $Path 'templates') -or
        [string]::Equals($Path, 'templates/AGENTS.md', [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if ((Test-PathPrefix $Path 'docs') -or (Test-PathPrefix $Path 'doc')) {
        return $true
    }

    $fileName = [IO.Path]::GetFileName($Path)
    if ($fileName -match '^(README|CHANGELOG|CONTRIBUTING|SECURITY|LICENSE)(\.|$)') {
        return $true
    }

    return [IO.Path]::GetExtension($Path).ToLowerInvariant() -in @('.md', '.markdown', '.rst')
}

function Test-SharedBuildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fileName = [IO.Path]::GetFileName($Path)
    $sharedFiles = @(
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'Directory.Packages.targets',
        'global.json',
        'NuGet.config',
        'nuget.config',
        '.editorconfig',
        'RimLiaison.sln'
    )

    if ($sharedFiles -contains $fileName -or
        $Path -match '\.(sln|slnx|csproj|fsproj|vbproj|props|targets)$' -or
        $fileName -eq 'packages.lock.json' -or
        (Test-PathPrefix $Path '.github') -or
        (Test-PathPrefix $Path 'build') -or
        (Test-PathPrefix $Path 'eng')) {
        return $true
    }

    return $false
}

function Get-SourceClassification {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-SharedBuildPath $Path) {
        return [pscustomobject]@{ Kind = 'shared-infrastructure'; AllInternal = $true; CrossStack = $true }
    }

    if ((Test-PathPrefix $Path 'contracts') -or
        (Test-PathPrefix $Path 'TestCatalog') -or
        (Test-PathPrefix $Path 'tests/fixtures/cross-stack') -or
        (Test-PathPrefix $Path 'fixtures/cross-stack')) {
        return [pscustomobject]@{ Kind = 'composition-contract'; AllInternal = $false; CrossStack = $true }
    }

    if (Test-PathPrefix $Path 'scripts/cross-stack') {
        return [pscustomobject]@{ Kind = 'composition-script'; AllInternal = $false; CrossStack = $true }
    }

    if (Test-PathPrefix $Path 'scripts') {
        return [pscustomobject]@{ Kind = 'ambiguous-script'; AllInternal = $true; CrossStack = $true }
    }

    if ((Test-PathPrefix $Path 'src/RimContext.Core') -or
        (Test-PathPrefix $Path 'src/RimContext.Cli')) {
        return [pscustomobject]@{ Kind = 'rimcontext-implementation'; AllInternal = $false; CrossStack = $true; RimContext = $true; RimLiaison = $true }
    }

    if ((Test-PathPrefix $Path 'src/RimError.Core') -or
        (Test-PathPrefix $Path 'src/RimError.Cli')) {
        return [pscustomobject]@{ Kind = 'rimerror-implementation'; AllInternal = $false; CrossStack = $true; RimError = $true; RimLiaison = $true }
    }

    if (Test-PathPrefix $Path 'tests/RimContext.Tests') {
        return [pscustomobject]@{ Kind = 'rimcontext-tests'; AllInternal = $false; CrossStack = $false; RimContext = $true }
    }

    if ((Test-PathPrefix $Path 'tests/RimError.Core.Tests') -or
        (Test-PathPrefix $Path 'fixtures')) {
        return [pscustomobject]@{ Kind = 'rimerror-tests'; AllInternal = $false; CrossStack = $false; RimError = $true }
    }

    if (Test-PathPrefix $Path 'tests/RimLiaison.Tests') {
        return [pscustomobject]@{ Kind = 'rimliaison-tests'; AllInternal = $false; CrossStack = $false; RimLiaison = $true }
    }

    if ([string]::Equals($Path, 'templates/AGENTS.md', [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Kind = 'rimliaison-embedded-resource'; AllInternal = $false; CrossStack = $false; RimLiaison = $true }
    }

    if (Test-PathPrefix $Path 'templates') {
        return [pscustomobject]@{ Kind = 'ambiguous-template'; AllInternal = $true; CrossStack = $true }
    }

    if (Test-PathPrefix $Path 'src/RimLiaison.Cli') {
        $relativePath = $Path.Substring('src/RimLiaison.Cli'.Length).TrimStart('/')
        $integrationDirectories = @('Catalog', 'DevBridge', 'Doctor', 'Execution', 'Git', 'Results', 'RimContext', 'RimError', 'Stack')
        $integrationFileNames = @('CliApplication.cs', 'CliParser.cs', 'Program.cs', 'WorkflowCorrelation.cs')
        $knownNonIntegrationFiles = @('CliExitCodes.cs')
        $firstSegment = ($relativePath -split '/')[0]
        $isIntegration = ($integrationDirectories -contains $firstSegment) -or
            ($integrationFileNames -contains [IO.Path]::GetFileName($relativePath))
        $isKnownNonIntegration = $knownNonIntegrationFiles -contains [IO.Path]::GetFileName($relativePath)

        return [pscustomobject]@{
            Kind = if ($isIntegration) { 'rimliaison-integration' } elseif ($isKnownNonIntegration) { 'rimliaison-implementation' } else { 'ambiguous-rimliaison' }
            AllInternal = -not ($isIntegration -or $isKnownNonIntegration)
            CrossStack = $isIntegration -or -not $isKnownNonIntegration
            RimLiaison = $true
        }
    }

    if ((Test-PathPrefix $Path 'src') -or (Test-PathPrefix $Path 'tests')) {
        return [pscustomobject]@{ Kind = 'ambiguous-source'; AllInternal = $true; CrossStack = $true }
    }

    return [pscustomobject]@{ Kind = 'ambiguous-path'; AllInternal = $true; CrossStack = $true }
}

function Get-GitChangedPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Base,

        [Parameter(Mandatory = $true)]
        [string]$Head
    )

    if ([string]::IsNullOrWhiteSpace($Base) -or [string]::IsNullOrWhiteSpace($Head) -or
        $Base -match '^[0]+$' -or $Head -match '^[0]+$' -or
        $Base.StartsWith('-', [StringComparison]::Ordinal) -or
        $Head.StartsWith('-', [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Success = $false; Paths = [string[]]@(); Reason = 'base-or-head-revision-unavailable' }
    }

    try {
        $baseSpec = $Base + '^{commit}'
        $headSpec = $Head + '^{commit}'
        $null = & git -C $Root rev-parse --verify $baseSpec 2>$null
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Success = $false; Paths = [string[]]@(); Reason = 'base-revision-unavailable' }
        }
        $null = & git -C $Root rev-parse --verify $headSpec 2>$null
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Success = $false; Paths = [string[]]@(); Reason = 'head-revision-unavailable' }
        }

        $rawPaths = @(& git -C $Root diff --name-only --diff-filter=ACDMRTUXB $Base $Head -- 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Success = $false; Paths = [string[]]@(); Reason = 'git-diff-unavailable' }
        }

        return [pscustomobject]@{ Success = $true; Paths = [string[]]$rawPaths; Reason = 'git-diff' }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Paths = [string[]]@(); Reason = 'git-diff-error' }
    }
}

function Get-PathInput {
    param(
        [AllowEmptyCollection()]
        [string[]]$Paths,

        [Parameter(Mandatory = $true)]
        [bool]$ExplicitPaths,

        [string]$Base,
        [string]$Head,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if ($ExplicitPaths) {
        return [pscustomobject]@{
            Source = 'explicit'
            RawPaths = if ($null -eq $Paths) { [string[]]@() } else { [string[]]$Paths }
            Certain = $true
            Reason = 'explicit-changed-paths'
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Base) -or -not [string]::IsNullOrWhiteSpace($Head)) {
        $gitResult = Get-GitChangedPaths $Root $Base $Head
        return [pscustomobject]@{
            Source = 'git'
            RawPaths = $gitResult.Paths
            Certain = [bool]$gitResult.Success
            Reason = [string]$gitResult.Reason
        }
    }

    return [pscustomobject]@{
        Source = 'unavailable'
        RawPaths = [string[]]@()
        Certain = $false
        Reason = 'changed-paths-unavailable'
    }
}

function Convert-ToOutputBoolean {
    param([Parameter(Mandatory = $true)][bool]$Value)
    if ($Value) { return 'true' }
    return 'false'
}

$explicitPaths = $PSBoundParameters.ContainsKey('ChangedPath')
$input = Get-PathInput $ChangedPath $explicitPaths $BaseRevision $HeadRevision $RepositoryRoot
$normalizedPaths = [Collections.Generic.List[string]]::new()
$inputUncertain = -not [bool]$input.Certain
$uncertaintyReasons = [Collections.Generic.List[string]]::new()

if ($inputUncertain) {
    Add-UniqueString $uncertaintyReasons ([string]$input.Reason)
}

foreach ($rawPath in @($input.RawPaths)) {
    $normalized = Normalize-ChangedPath ([string]$rawPath)
    if (-not $normalized.Valid) {
        $inputUncertain = $true
        Add-UniqueString $uncertaintyReasons ([string]$normalized.Reason)
        continue
    }

    if (-not $normalizedPaths.Contains($normalized.Path)) {
        [void]$normalizedPaths.Add($normalized.Path)
    }
}

if ($normalizedPaths.Count -gt $maxChangedPaths) {
    $inputUncertain = $true
    Add-UniqueString $uncertaintyReasons 'changed-path-count-exceeds-bound'
}

$orderedPaths = @($normalizedPaths | Sort-Object)
$runRimContext = $false
$runRimError = $false
$runRimLiaison = $false
$runAllInternal = $false
$runCrossStack = $false
$documentationCount = 0
$nonDocumentationCount = 0
$kindCodes = [Collections.Generic.List[string]]::new()

foreach ($path in $orderedPaths) {
    if (Test-DocumentationPath $path) {
        $documentationCount++
        continue
    }

    $nonDocumentationCount++
    $classification = Get-SourceClassification $path
    Add-UniqueString $kindCodes ([string]$classification.Kind)
    if ([string]$classification.Kind -like 'ambiguous-*') {
        $inputUncertain = $true
        Add-UniqueString $uncertaintyReasons ([string]$classification.Kind)
    }
    $runAllInternal = $runAllInternal -or [bool]$classification.AllInternal
    $runCrossStack = $runCrossStack -or [bool]$classification.CrossStack
    if ($classification.PSObject.Properties.Name -contains 'RimContext') {
        $runRimContext = $runRimContext -or [bool]$classification.RimContext
    }
    if ($classification.PSObject.Properties.Name -contains 'RimError') {
        $runRimError = $runRimError -or [bool]$classification.RimError
    }
    if ($classification.PSObject.Properties.Name -contains 'RimLiaison') {
        $runRimLiaison = $runRimLiaison -or [bool]$classification.RimLiaison
    }
}

if ($inputUncertain) {
    $runAllInternal = $true
    $runRimContext = $true
    $runRimError = $true
    $runRimLiaison = $true
    $runCrossStack = $true
    Add-UniqueString $kindCodes 'planner-uncertainty'
}

if ($runAllInternal) {
    $runRimContext = $true
    $runRimError = $true
    $runRimLiaison = $true
}

$hasChanges = $orderedPaths.Count -gt 0
$runFormat = $runAllInternal -or $runRimContext -or $runRimError -or $runRimLiaison

$category = 'no-change'
if ($inputUncertain) {
    $category = 'full-uncertain'
} elseif (-not $hasChanges) {
    $category = 'no-change'
} elseif ($nonDocumentationCount -eq 0) {
    $category = 'documentation-only'
} elseif ($runAllInternal) {
    $category = 'all-internal'
} elseif ($runCrossStack -and -not $runFormat) {
    $category = 'composition-only'
} elseif ($runRimContext -and $runRimLiaison -and -not $runRimError) {
    $category = 'rimcontext-and-rimliaison'
} elseif ($runRimError -and $runRimLiaison -and -not $runRimContext) {
    $category = 'rimerror-and-rimliaison'
} elseif ($runRimContext -and -not $runRimError -and -not $runRimLiaison) {
    $category = 'rimcontext-only'
} elseif ($runRimError -and -not $runRimContext -and -not $runRimLiaison) {
    $category = 'rimerror-only'
} elseif ($runRimLiaison -and -not $runRimContext -and -not $runRimError) {
    $category = if ($runCrossStack) { 'rimliaison-and-composition' } else { 'rimliaison-only' }
} else {
    $category = 'mixed'
}

$reasonParts = [Collections.Generic.List[string]]::new()
if ($inputUncertain) {
    foreach ($reasonCode in $uncertaintyReasons) {
        Add-UniqueString $reasonParts $reasonCode
    }
} elseif (-not $hasChanges) {
    Add-UniqueString $reasonParts 'no-changed-paths'
} elseif ($nonDocumentationCount -eq 0) {
    Add-UniqueString $reasonParts 'documentation-only'
} else {
    foreach ($kindCode in $kindCodes) {
        Add-UniqueString $reasonParts $kindCode
    }
}

$reason = ($reasonParts -join ',')
if ($reason.Length -gt $maxReasonLength) {
    $reason = $reason.Substring(0, $maxReasonLength)
}

$samplePaths = @($orderedPaths | Select-Object -First $maxSamplePaths)
$plan = [ordered]@{
    schemaVersion = 'rimliaison-ci-plan/v1'
    status = 'ok'
    event = if ([string]::IsNullOrWhiteSpace($EventName)) { $null } else { $EventName }
    source = [string]$input.Source
    category = $category
    reason = $reason
    certain = -not $inputUncertain
    hasChanges = $hasChanges
    changedPathCount = $orderedPaths.Count
    documentationPathCount = $documentationCount
    sampleChangedPaths = $samplePaths
    runAllInternal = $runAllInternal
    runRimContext = $runRimContext
    runRimError = $runRimError
    runRimLiaison = $runRimLiaison
    runFormat = $runFormat
    runCrossStack = $runCrossStack
    runPlannerTests = $true
}

$planJson = ConvertTo-Json -InputObject ([pscustomobject]$plan) -Depth 10 -Compress

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    $outputValues = [ordered]@{
        plan_json = $planJson
        plan_category = [string]$plan.category
        plan_reason = [string]$plan.reason
        plan_source = [string]$plan.source
        plan_certain = Convert-ToOutputBoolean ([bool]$plan.certain)
        changed_path_count = [string]$plan.changedPathCount
        run_all_internal = Convert-ToOutputBoolean ([bool]$plan.runAllInternal)
        run_rimcontext = Convert-ToOutputBoolean ([bool]$plan.runRimContext)
        run_rimerror = Convert-ToOutputBoolean ([bool]$plan.runRimError)
        run_rimliaison = Convert-ToOutputBoolean ([bool]$plan.runRimLiaison)
        run_format = Convert-ToOutputBoolean ([bool]$plan.runFormat)
        run_cross_stack = Convert-ToOutputBoolean ([bool]$plan.runCrossStack)
        run_planner_tests = 'true'
    }

    $outputText = (($outputValues.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join [Environment]::NewLine) + [Environment]::NewLine
    [IO.File]::AppendAllText($GitHubOutputPath, $outputText, [Text.UTF8Encoding]::new($false))
}

Write-Output $planJson
