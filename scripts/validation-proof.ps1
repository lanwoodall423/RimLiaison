Set-StrictMode -Version Latest

$script:ValidationProofSchemaVersion = 'rimliaison-validation-proof/v1'
$script:ValidationProofValidatorVersion = 'rimliaison-validation-proof-1'
$script:ValidationProofMaxRecordBytes = 64KB
$script:ValidationProofDefaultMaxRecords = 64
$script:ValidationProofDefaultMaxBytes = 1MB

function Get-ValidationProofSchemaVersion {
    return $script:ValidationProofSchemaVersion
}

function Get-ValidationProofValidatorVersion {
    return $script:ValidationProofValidatorVersion
}

function Get-ValidationProofMaxRecordBytes {
    return $script:ValidationProofMaxRecordBytes
}

function Get-ValidationProofStageDefinition {
    param(
        [Parameter(Mandatory = $true)][string]$StageId
    )

    switch ($StageId) {
        'rimcontext' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'dotnet'
                SourceRoots = @('src/RimContext.Core', 'src/RimContext.Cli', 'tests/RimContext.Tests')
                ExtraRoots = @()
                RequiredFiles = @()
            }
        }
        'rimerror' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'dotnet'
                SourceRoots = @('src/RimError.Core', 'src/RimError.Cli', 'tests/RimError.Core.Tests', 'fixtures')
                ExtraRoots = @()
                RequiredFiles = @()
            }
        }
        'rimliaison' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'dotnet'
                SourceRoots = @('src/RimLiaison.Cli', 'src/RimContext.Core', 'src/RimError.Core', 'tests/RimLiaison.Tests', 'tests/fixtures', 'templates')
                ExtraRoots = @('TestCatalog', 'contracts')
                RequiredFiles = @()
            }
        }
        'format' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'dotnet'
                SourceRoots = @('src', 'tests')
                ExtraRoots = @('templates')
                RequiredFiles = @()
            }
        }
        'cross-stack' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'dotnet'
                SourceRoots = @('src', 'tests/fixtures/cross-stack', 'contracts')
                ExtraRoots = @()
                RequiredFiles = @('scripts/cross-stack-contract.tests.ps1', 'scripts/cross-stack-fake-devbridge.ps1', 'scripts/cross-stack-fake-mod-development.ps1')
                RequiresDevBridge = $true
            }
        }
        'planner-tests' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'powershell'
                SourceRoots = @()
                ExtraRoots = @()
                RequiredFiles = @('scripts/ci-plan.ps1', 'scripts/ci-plan.tests.ps1')
            }
        }
        'diff-check' {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $true
                Toolchain = 'git'
                SourceRoots = @()
                ExtraRoots = @()
                RequiredFiles = @()
                RequiresGitRevisions = $true
            }
        }
        # These names are deliberately recognized as non-reusable. Keeping the
        # guard here prevents a future caller from accidentally treating a live
        # or mutable-state check as an offline proof stage.
        { $_ -in @('live-rimworld-smoke', 'live-stack-smoke', 'artifact-freshness', 'deployment', 'readiness', 'lease', 'lifecycle') } {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $false
                Toolchain = 'external-state'
                SourceRoots = @()
                ExtraRoots = @()
                RequiredFiles = @()
                NonReusableReason = 'external-or-live-state'
            }
        }
        default {
            return [pscustomobject]@{
                StageId = $StageId
                Reusable = $false
                Toolchain = 'unknown'
                SourceRoots = @()
                ExtraRoots = @()
                RequiredFiles = @()
                NonReusableReason = 'unknown-stage'
            }
        }
    }
}

function Get-ValidationProofDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$ProofRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($ProofRoot)) {
        return [IO.Path]::GetFullPath($ProofRoot)
    }

    return [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.rimdev/validation-proofs'))
}

function Test-ValidationExcludedPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $normalized = $RelativePath.Replace('\', '/').TrimStart('/').ToLowerInvariant()
    $segments = $normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        if ($segment -in @('.git', '.rimctx', '.rimdev', '.vs', 'bin', 'obj', 'testresults', 'coverage', 'artifacts')) {
            return $true
        }
    }

    return $false
}

function Test-ValidationPathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RepositoryRoot))
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    return $fullCandidate.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $fullCandidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-ValidationRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-ValidationPathWithin $RepositoryRoot $fullPath)) {
        throw 'Validation input escaped the repository root.'
    }

    return ([IO.Path]::GetRelativePath($RepositoryRoot, $fullPath).Replace('\', '/')).ToLowerInvariant()
}

function Get-ValidationFileSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            return ([Convert]::ToHexString($algorithm.ComputeHash($stream))).ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-ValidationToolVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Tool
    )

    try {
        $command = Get-Command $Tool -ErrorAction Stop
        if ($null -eq $command) {
            return [pscustomobject]@{ Available = $false; Value = $null }
        }

        $output = @(& $Tool --version 2>$null)
        if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
            return [pscustomobject]@{ Available = $false; Value = $null }
        }

        return [pscustomobject]@{
            Available = $true
            Value = ([string]$output[0]).Trim()
        }
    }
    catch {
        return [pscustomobject]@{ Available = $false; Value = $null }
    }
}

function Get-ValidationEnvironmentFingerprint {
    param(
        [Parameter(Mandatory = $true)]$Definition
    )

    $dotnet = [pscustomobject]@{ Available = $true; Value = $null }
    if ([string]$Definition.Toolchain -eq 'dotnet') {
        $dotnet = Get-ValidationToolVersion 'dotnet'
    }

    $osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $powershellVersion = [string]$PSVersionTable.PSVersion
    $environment = [ordered]@{
        os = [string]$osDescription
        architecture = $architecture
        powershell = $powershellVersion
        dotnet = $dotnet.Value
    }

    return [pscustomobject]@{
        Complete = [bool]$dotnet.Available
        Values = [pscustomobject]$environment
        Canonical = ($environment | ConvertTo-Json -Compress -Depth 4)
    }
}

function Get-ValidationInputFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$StageId
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $definition = Get-ValidationProofStageDefinition $StageId
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $complete = $true
    $reasons = [Collections.Generic.List[string]]::new()

    function Add-ValidationFile {
        param(
            [Parameter(Mandatory = $true)][string]$RelativePath,
            [switch]$Required
        )

        $candidate = [IO.Path]::GetFullPath((Join-Path $root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (-not (Test-ValidationPathWithin $root $candidate)) {
            $script:ValidationInputClosureComplete = $false
            [void]$script:ValidationInputClosureReasons.Add('input-outside-root')
            return
        }

        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            if ($Required) {
                $script:ValidationInputClosureComplete = $false
                [void]$script:ValidationInputClosureReasons.Add("missing:$RelativePath")
            }
            return
        }

        $relative = ConvertTo-ValidationRelativePath $root $candidate
        if (-not (Test-ValidationExcludedPath $relative)) {
            [void]$paths.Add($relative)
        }
    }

    function Add-ValidationRoot {
        param(
            [Parameter(Mandatory = $true)][string]$RelativePath,
            [switch]$Required
        )

        $candidate = [IO.Path]::GetFullPath((Join-Path $root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            if ($Required) {
                $script:ValidationInputClosureComplete = $false
                [void]$script:ValidationInputClosureReasons.Add("missing-root:$RelativePath")
            }
            return
        }

        try {
            foreach ($file in Get-ChildItem -LiteralPath $candidate -File -Force -Recurse -ErrorAction Stop) {
                $relative = ConvertTo-ValidationRelativePath $root $file.FullName
                if (Test-ValidationExcludedPath $relative) {
                    continue
                }

                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $script:ValidationInputClosureComplete = $false
                    [void]$script:ValidationInputClosureReasons.Add("reparse-point:$relative")
                    continue
                }

                [void]$paths.Add($relative)
            }
        }
        catch {
            $script:ValidationInputClosureComplete = $false
            [void]$script:ValidationInputClosureReasons.Add("enumeration-failed:$RelativePath")
        }
    }

    $script:ValidationInputClosureComplete = [bool]$definition.Reusable
    $script:ValidationInputClosureReasons = [Collections.Generic.List[string]]::new()
    if (-not $definition.Reusable) {
        [void]$script:ValidationInputClosureReasons.Add([string]$definition.NonReusableReason)
    }

    Add-ValidationFile 'scripts/validation-proof.ps1' -Required
    Add-ValidationFile 'scripts/ci-validate.ps1' -Required
    foreach ($sourceRoot in @($definition.SourceRoots)) {
        Add-ValidationRoot ([string]$sourceRoot) -Required
    }
    foreach ($extraRoot in @($definition.ExtraRoots)) {
        Add-ValidationRoot ([string]$extraRoot) -Required
    }
    foreach ($requiredFile in @($definition.RequiredFiles)) {
        Add-ValidationFile ([string]$requiredFile) -Required
    }

    # These files form the shared build/project graph. Including all project
    # references and import files is still narrower than hashing the worktree,
    # while avoiding unsafe guesses about transitive MSBuild inputs.
    try {
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Force -Recurse -ErrorAction Stop) {
            $relative = ConvertTo-ValidationRelativePath $root $file.FullName
            if (Test-ValidationExcludedPath $relative) {
                continue
            }

            $lower = $relative.ToLowerInvariant()
            $name = [IO.Path]::GetFileName($lower)
            $extension = [IO.Path]::GetExtension($lower)
            $isGraphInput = $extension -in @('.sln', '.slnx', '.csproj', '.fsproj', '.vbproj', '.props', '.targets') -or
                $name -in @('global.json', 'nuget.config', 'directory.build.props', 'directory.build.targets', 'directory.packages.props') -or
                $name -like '*.lock.json' -or $name -like '*.lock'
            if ($isGraphInput) {
                [void]$paths.Add($relative)
            }
        }
    }
    catch {
        $script:ValidationInputClosureComplete = $false
        [void]$script:ValidationInputClosureReasons.Add('graph-enumeration-failed')
    }

    if ($StageId -eq 'rimliaison') {
        foreach ($catalogFile in @('TestCatalog/rimtest.catalog.json', 'TestCatalog/devbridge-test-recipe-list.json')) {
            Add-ValidationFile $catalogFile -Required
        }
    }

    $result = [pscustomobject]@{
        Complete = [bool]$script:ValidationInputClosureComplete
        Paths = @($paths | Sort-Object)
        Reasons = @($script:ValidationInputClosureReasons | Select-Object -Unique)
    }
    Remove-Variable ValidationInputClosureComplete -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable ValidationInputClosureReasons -Scope Script -ErrorAction SilentlyContinue
    return $result
}

function Get-ValidationExternalRevision {
    param(
        [string]$DevBridgeRoot
    )

    if ([string]::IsNullOrWhiteSpace($DevBridgeRoot)) {
        return [pscustomobject]@{ Complete = $false; Revision = $null; Reason = 'devbridge-root-missing' }
    }

    $root = [IO.Path]::GetFullPath($DevBridgeRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return [pscustomobject]@{ Complete = $false; Revision = $null; Reason = 'devbridge-root-not-found' }
    }

    try {
        $revisionOutput = @(& git -c "safe.directory=$root" -C $root rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or $revisionOutput.Count -eq 0) {
            return [pscustomobject]@{ Complete = $false; Revision = $null; Reason = 'devbridge-revision-unavailable' }
        }

        $revision = ([string]$revisionOutput[0]).Trim().ToLowerInvariant()
        $status = @(& git -c "safe.directory=$root" -C $root status --porcelain --untracked-files=all 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Complete = $false; Revision = $revision; Reason = 'devbridge-status-unavailable' }
        }
        if ($status.Count -gt 0) {
            return [pscustomobject]@{ Complete = $false; Revision = $revision; Reason = 'devbridge-worktree-dirty' }
        }

        return [pscustomobject]@{ Complete = $true; Revision = $revision; Reason = $null }
    }
    catch {
        return [pscustomobject]@{ Complete = $false; Revision = $null; Reason = 'devbridge-revision-error' }
    }
}

function Get-ValidationStageFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$StageId,
        [string[]]$SelectedTestIds = @(),
        [string]$DevBridgeRoot,
        [string]$GitBaseRevision,
        [string]$GitHeadRevision
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $definition = Get-ValidationProofStageDefinition $StageId
    $closure = Get-ValidationInputFiles $root $StageId
    $environment = Get-ValidationEnvironmentFingerprint $definition
    $external = [pscustomobject]@{ Complete = $true; Revision = $null; Reason = $null }
    $requiresDevBridge = $null -ne $definition.PSObject.Properties['RequiresDevBridge'] -and
        [bool]$definition.RequiresDevBridge
    $requiresGitRevisions = $null -ne $definition.PSObject.Properties['RequiresGitRevisions'] -and
        [bool]$definition.RequiresGitRevisions
    if ($requiresDevBridge) {
        $external = Get-ValidationExternalRevision $DevBridgeRoot
    }

    $revisionsComplete = $true
    if ($requiresGitRevisions) {
        $revisionsComplete = -not [string]::IsNullOrWhiteSpace($GitBaseRevision) -and
            -not [string]::IsNullOrWhiteSpace($GitHeadRevision)
    }

    $complete = [bool]$closure.Complete -and [bool]$environment.Complete -and
        [bool]$external.Complete -and $revisionsComplete -and [bool]$definition.Reusable
    $reasons = [Collections.Generic.List[string]]::new()
    foreach ($reason in @($closure.Reasons)) { [void]$reasons.Add([string]$reason) }
    if (-not $environment.Complete) { [void]$reasons.Add('toolchain-version-unavailable') }
    if (-not $external.Complete) { [void]$reasons.Add([string]$external.Reason) }
    if (-not $revisionsComplete) { [void]$reasons.Add('git-revisions-missing') }
    if (-not $definition.Reusable) { [void]$reasons.Add([string]$definition.NonReusableReason) }

    $orderedTests = @($SelectedTestIds + $StageId |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [string]$_ } |
        Sort-Object -Unique)
    $fileEntries = [Collections.Generic.List[object]]::new()
    $closureHash = ''
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        function Add-FingerprintText {
            param([AllowNull()][AllowEmptyString()][string]$Value)
            $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
            $hash.AppendData($bytes)
            $hash.AppendData([byte[]](0))
        }

        Add-FingerprintText $script:ValidationProofSchemaVersion
        Add-FingerprintText $StageId
        Add-FingerprintText ($orderedTests -join ',')
        Add-FingerprintText $script:ValidationProofValidatorVersion
        Add-FingerprintText ([string]$environment.Canonical)
        Add-FingerprintText ([string]$external.Revision)
        Add-FingerprintText ([string]$GitBaseRevision)
        Add-FingerprintText ([string]$GitHeadRevision)

        foreach ($relative in @($closure.Paths)) {
            $fullPath = Join-Path $root $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
            try {
                $sha = Get-ValidationFileSha256 $fullPath
                [void]$fileEntries.Add([pscustomobject]@{ Path = $relative; Sha256 = $sha })
            }
            catch {
                $complete = $false
                [void]$reasons.Add("hash-failed:$relative")
            }
        }

        foreach ($entry in $fileEntries | Sort-Object Path) {
            Add-FingerprintText ([string]$entry.Path)
            Add-FingerprintText ([string]$entry.Sha256)
        }

        $closureHash = ([Convert]::ToHexString($hash.GetHashAndReset())).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }

    if ($fileEntries.Count -eq 0) {
        $complete = $false
        [void]$reasons.Add('input-closure-empty')
    }

    $proofId = $closureHash
    return [pscustomobject]@{
        Complete = [bool]$complete
        StageId = $StageId
        SelectedTestIds = $orderedTests
        InputClosureHash = $closureHash
        ProofId = $proofId
        InputCount = $fileEntries.Count
        Environment = $environment.Values
        ExternalRevision = $external.Revision
        Reasons = @($reasons | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
    }
}

function New-ValidationProofRecord {
    param(
        [Parameter(Mandatory = $true)]$Fingerprint,
        [Parameter(Mandatory = $true)][string]$StageId,
        [string[]]$SelectedTestIds = @(),
        [string]$Status = 'pass'
    )

    $orderedTests = @($SelectedTestIds + $StageId |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [string]$_ } |
        Sort-Object -Unique)
    return [pscustomobject][ordered]@{
        schemaVersion = $script:ValidationProofSchemaVersion
        status = $Status
        stageId = $StageId
        selectedTestIds = $orderedTests
        proofId = [string]$Fingerprint.ProofId
        inputClosureHash = [string]$Fingerprint.InputClosureHash
        inputCount = [int]$Fingerprint.InputCount
        validatorVersion = $script:ValidationProofValidatorVersion
        environment = $Fingerprint.Environment
        externalRevision = $Fingerprint.ExternalRevision
        closureComplete = [bool]$Fingerprint.Complete
        createdUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function Test-ValidationProofRecord {
    param(
        [AllowNull()]$Record,
        [Parameter(Mandatory = $true)]$Fingerprint,
        [Parameter(Mandatory = $true)][string]$StageId,
        [string[]]$SelectedTestIds = @()
    )

    if ($null -eq $Record -or -not [bool]$Fingerprint.Complete) {
        return $false
    }

    try {
        if ([string]$Record.schemaVersion -ne $script:ValidationProofSchemaVersion -or
            [string]$Record.status -ne 'pass' -or
            [string]$Record.stageId -ne $StageId -or
            [string]$Record.validatorVersion -ne $script:ValidationProofValidatorVersion -or
            [bool]$Record.closureComplete -ne $true -or
            $null -eq $Record.PSObject.Properties['environment'] -or
            $null -eq $Record.PSObject.Properties['selectedTestIds'] -or
            [int]$Record.inputCount -ne [int]$Fingerprint.InputCount -or
            [string]$Record.proofId -ne [string]$Fingerprint.ProofId -or
            [string]$Record.inputClosureHash -ne [string]$Fingerprint.InputClosureHash) {
            return $false
        }

        $expectedEnvironment = $Fingerprint.Environment | ConvertTo-Json -Depth 4 -Compress
        $actualEnvironment = $Record.environment | ConvertTo-Json -Depth 4 -Compress
        if ($actualEnvironment -ne $expectedEnvironment) {
            return $false
        }

        $expectedTests = @($SelectedTestIds + $StageId |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ } |
            Sort-Object -Unique)
        $actualTests = @($Record.selectedTestIds | ForEach-Object { [string]$_ })
        if ((@($actualTests) -join "`n") -ne (@($expectedTests) -join "`n")) {
            return $false
        }

        return ([string]$Fingerprint.ExternalRevision -eq [string]$Record.externalRevision)
    }
    catch {
        return $false
    }
}

function Get-ValidationProofRecord {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Fingerprint,
        [Parameter(Mandatory = $true)][string]$StageId,
        [string[]]$SelectedTestIds = @(),
        [string]$ProofRoot
    )

    if (-not [bool]$Fingerprint.Complete) {
        return $null
    }

    try {
        $directory = Get-ValidationProofDirectory $RepositoryRoot $ProofRoot
        $path = Join-Path $directory ([string]$Fingerprint.ProofId + '.json')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $null
        }

        $file = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($file.Length -le 0 -or $file.Length -gt $script:ValidationProofMaxRecordBytes) {
            return $null
        }

        $record = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 8
        if (Test-ValidationProofRecord $record $Fingerprint $StageId $SelectedTestIds) {
            return $record
        }
    }
    catch {
        # A cache read is deliberately best effort. Malformed or unavailable
        # storage is a miss and never changes validation success semantics.
    }

    return $null
}

function Save-ValidationProofRecord {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Record,
        [string]$ProofRoot,
        [int]$MaxRecords = $script:ValidationProofDefaultMaxRecords,
        [int64]$MaxBytes = $script:ValidationProofDefaultMaxBytes
    )

    if ($null -eq $Record -or [string]$Record.status -ne 'pass' -or
        [bool]$Record.closureComplete -ne $true -or
        [string]$Record.proofId -notmatch '^[0-9a-f]{64}$') {
        return $false
    }

    try {
        $directory = Get-ValidationProofDirectory $RepositoryRoot $ProofRoot
        [void](New-Item -ItemType Directory -Force -Path $directory -ErrorAction Stop)
        $json = $Record | ConvertTo-Json -Depth 8 -Compress
        if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $script:ValidationProofMaxRecordBytes) {
            return $false
        }

        $target = Join-Path $directory ([string]$Record.proofId + '.json')
        $temporary = [IO.Path]::GetTempFileName()
        try {
            [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($temporary, $target, $true)
        }
        finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            }
        }

        Prune-ValidationProofCache $directory $MaxRecords $MaxBytes
        return $true
    }
    catch {
        return $false
    }
}

function Prune-ValidationProofCache {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [int]$MaxRecords = $script:ValidationProofDefaultMaxRecords,
        [int64]$MaxBytes = $script:ValidationProofDefaultMaxBytes
    )

    try {
        if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
            return
        }

        $entries = [Collections.Generic.List[object]]::new()
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter '*.json' -File -Force -ErrorAction Stop)) {
            $created = [DateTime]::MinValue
            $proofId = $file.BaseName
            try {
                if ($file.Length -le $script:ValidationProofMaxRecordBytes) {
                    $record = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 8
                    if ($null -ne $record.createdUtc) {
                        [void][DateTime]::TryParse(
                            [string]$record.createdUtc,
                            [Globalization.CultureInfo]::InvariantCulture,
                            [Globalization.DateTimeStyles]::RoundtripKind,
                            [ref]$created)
                    }
                    if (-not [string]::IsNullOrWhiteSpace([string]$record.proofId)) {
                        $proofId = [string]$record.proofId
                    }
                }
            }
            catch {
                # Corrupt records remain misses; they still participate in
                # deterministic bounded eviction using their file name.
            }
            [void]$entries.Add([pscustomobject]@{
                    Path = $file.FullName
                    Size = [int64]$file.Length
                    Created = $created
                    ProofId = $proofId
                    Name = $file.Name
                })
        }

        [int64]$totalBytes = ($entries | Measure-Object -Property Size -Sum).Sum
        $ordered = @($entries | Sort-Object Created, ProofId, Name)
        while ($ordered.Count -gt [Math]::Max(0, $MaxRecords) -or
            $totalBytes -gt [Math]::Max([int64]0, $MaxBytes)) {
            if ($ordered.Count -eq 0) { break }
            $victim = $ordered[0]
            $ordered = @($ordered | Select-Object -Skip 1)
            try {
                Remove-Item -LiteralPath $victim.Path -Force -ErrorAction Stop
                $totalBytes -= [int64]$victim.Size
            }
            catch {
                break
            }
        }
    }
    catch {
        # Cache maintenance is never allowed to fail validation.
    }
}
