function Get-PinnedDevBridgeManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RimLiaisonRoot,
        [string]$ManifestPath
    )

    $path = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        Join-Path ([IO.Path]::GetFullPath($RimLiaisonRoot)) 'contracts\cross-stack-compatibility.json'
    } else {
        [IO.Path]::GetFullPath($ManifestPath)
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "CROSS_STACK_MANIFEST_MISSING: $path"
    }

    try {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 30
    } catch {
        throw "CROSS_STACK_MANIFEST_INVALID: $($_.Exception.Message)"
    }
    if ([string]$manifest.schemaVersion -ne 'rimtest-cross-stack-compatibility/v1') {
        throw 'CROSS_STACK_MANIFEST_SCHEMA_UNSUPPORTED'
    }
    $revision = [string]$manifest.repositories.devBridge2.revision
    if ($revision -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'CROSS_STACK_PIN_INVALID: DevBridge2 compatibility revision is not a full SHA'
    }
    return [pscustomobject]@{
        Path = $path
        Revision = $revision.ToLowerInvariant()
        Repository = [string]$manifest.repositories.devBridge2.repository
    }
}

function Get-PinnedDevBridgeGitHead {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $null
    }
    $head = @(& git -c "safe.directory=$Root" -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -eq 0) {
        return $null
    }
    return ([string]$head[0]).Trim().ToLowerInvariant()
}

function Get-PinnedDevBridgeStatus {
    param([Parameter(Mandatory = $true)][string]$Root)

    $status = @(& git -c "safe.directory=$Root" -C $Root status --porcelain --untracked-files=all 2>$null)
    return [pscustomobject]@{
        Available = $LASTEXITCODE -eq 0
        Entries = @($status | ForEach-Object { [string]$_ })
    }
}

function Test-PinnedDevBridgeCommit {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Revision
    )

    & git -c "safe.directory=$Root" -C $Root cat-file -e ($Revision + '^{commit}') 2>$null
    return $LASTEXITCODE -eq 0
}

function Resolve-PinnedDevBridgeWorktree {
    param(
        [Parameter(Mandatory = $true)][string]$RimLiaisonRoot,
        [Parameter(Mandatory = $true)][string]$DevBridgeRoot,
        [string]$ManifestPath
    )

    $manifest = Get-PinnedDevBridgeManifest -RimLiaisonRoot $RimLiaisonRoot -ManifestPath $ManifestPath
    $normalRoot = [IO.Path]::GetFullPath($DevBridgeRoot)
    $normalHead = Get-PinnedDevBridgeGitHead -Root $normalRoot
    $normalStatus = if ($null -ne $normalHead) { Get-PinnedDevBridgeStatus -Root $normalRoot } else { $null }
    if ($normalHead -eq $manifest.Revision -and $null -ne $normalStatus -and
        [bool]$normalStatus.Available -and $normalStatus.Entries.Count -eq 0) {
        return [pscustomobject]@{
            schemaVersion = 'rimtest-pinned-devbridge/v1'
            pinnedRevision = $manifest.Revision
            resolvedRoot = $normalRoot
            requestedRoot = $normalRoot
            materialization = 'normal-checkout'
            usedNormalCheckout = $true
            worktreePath = $null
            manifestPath = $manifest.Path
        }
    }

    if ($null -eq $normalHead) {
        throw "CROSS_STACK_DEVBRIDGE_CHECKOUT_INVALID: $normalRoot is not a usable Git checkout"
    }

    $workspaceRoot = [Environment]::GetEnvironmentVariable('RIMDEV_ROOT')
    if ([string]::IsNullOrWhiteSpace($workspaceRoot)) {
        $workspaceRoot = [IO.Directory]::GetParent([IO.Directory]::GetParent([IO.Path]::GetFullPath($RimLiaisonRoot)).FullName).FullName
    }
    $cacheRoot = Join-Path ([IO.Path]::GetFullPath($workspaceRoot)) '.rimdev\pinned-worktrees\DevBridge2'
    $worktreePath = Join-Path $cacheRoot $manifest.Revision
    $existingHead = Get-PinnedDevBridgeGitHead -Root $worktreePath
    if ($existingHead -eq $manifest.Revision) {
        $existingStatus = Get-PinnedDevBridgeStatus -Root $worktreePath
        if (-not [bool]$existingStatus.Available) {
            throw "CROSS_STACK_PIN_WORKTREE_STATUS_UNAVAILABLE: $worktreePath"
        }
        if ($existingStatus.Entries.Count -gt 0) {
            throw "CROSS_STACK_PIN_WORKTREE_DIRTY: $worktreePath"
        }
        return [pscustomobject]@{
            schemaVersion = 'rimtest-pinned-devbridge/v1'
            pinnedRevision = $manifest.Revision
            resolvedRoot = $worktreePath
            requestedRoot = $normalRoot
            materialization = 'cached-worktree'
            usedNormalCheckout = $false
            worktreePath = $worktreePath
            manifestPath = $manifest.Path
        }
    }
    if (Test-Path -LiteralPath $worktreePath) {
        $entries = @(Get-ChildItem -LiteralPath $worktreePath -Force -ErrorAction SilentlyContinue)
        if ($entries.Count -gt 0) {
            throw "CROSS_STACK_PIN_WORKTREE_CONFLICT: $worktreePath is not the requested pinned worktree"
        }
    }

    if (-not (Test-PinnedDevBridgeCommit -Root $normalRoot -Revision $manifest.Revision)) {
        & git -c "safe.directory=$normalRoot" -C $normalRoot fetch --no-tags origin $manifest.Revision 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-PinnedDevBridgeCommit -Root $normalRoot -Revision $manifest.Revision)) {
            throw "CROSS_STACK_PIN_UNAVAILABLE: could not obtain $($manifest.Revision) from $normalRoot"
        }
    }

    [void](New-Item -ItemType Directory -Force -Path $cacheRoot)
    & git -c "safe.directory=$normalRoot" -C $normalRoot worktree add --detach $worktreePath $manifest.Revision 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Get-PinnedDevBridgeGitHead -Root $worktreePath) -ne $manifest.Revision) {
        throw "CROSS_STACK_PIN_MATERIALIZATION_FAILED: could not materialize $($manifest.Revision) at $worktreePath"
    }

    return [pscustomobject]@{
        schemaVersion = 'rimtest-pinned-devbridge/v1'
        pinnedRevision = $manifest.Revision
        resolvedRoot = $worktreePath
        requestedRoot = $normalRoot
        materialization = 'new-worktree'
        usedNormalCheckout = $false
        worktreePath = $worktreePath
        manifestPath = $manifest.Path
    }
}

function Remove-PinnedDevBridgeWorktree {
    param(
        [Parameter(Mandatory = $true)][string]$DevBridgeRoot,
        [Parameter(Mandatory = $true)][string]$WorktreePath
    )

    if (-not (Test-Path -LiteralPath $WorktreePath -PathType Container)) {
        return
    }
    & git -c "safe.directory=$DevBridgeRoot" -C $DevBridgeRoot worktree remove --force $WorktreePath 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "CROSS_STACK_PIN_WORKTREE_REMOVE_FAILED: $WorktreePath"
    }
}
