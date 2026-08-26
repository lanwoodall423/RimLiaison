[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pinned-devbridge-worktree.ps1')

$testsRun = 0
function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw "PINNED_WORKTREE_ASSERTION_FAILED: $Message" }
}
function Assert-Equal {
    param($Actual, $Expected, [Parameter(Mandatory = $true)][string]$Message)
    if ([string]$Actual -ne [string]$Expected) {
        throw "PINNED_WORKTREE_ASSERTION_FAILED: $Message expected '$Expected', received '$Actual'"
    }
}
function Invoke-Git {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = @(& git -c "safe.directory=$Root" -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "PINNED_WORKTREE_GIT_FAILED: git $($Arguments -join ' ')`n$($output -join "`n")" }
    return $output
}
function New-Commit {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Text)
    [IO.File]::WriteAllText((Join-Path $Root 'DevBridge.txt'), $Text, [Text.UTF8Encoding]::new($false))
    Invoke-Git $Root @('add', 'DevBridge.txt') | Out-Null
    Invoke-Git $Root @('commit', '--quiet', '-m', $Text) | Out-Null
    return ([string](Invoke-Git $Root @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('rimliaison-pinned-worktree-test-' + [Guid]::NewGuid().ToString('N'))
$normalRoot = Join-Path $fixtureRoot 'DevBridge2'
$rimLiaisonRoot = Join-Path $fixtureRoot 'RimLiaison'
$manifestPath = Join-Path $rimLiaisonRoot 'contracts\cross-stack-compatibility.json'
$worktreePath = $null
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $rimLiaisonRoot 'contracts'), $normalRoot | Out-Null
    Invoke-Git $normalRoot @('init', '--quiet') | Out-Null
    Invoke-Git $normalRoot @('config', 'user.name', 'Pinned worktree test') | Out-Null
    Invoke-Git $normalRoot @('config', 'user.email', 'pinned-worktree.invalid@example.invalid') | Out-Null
    $firstRevision = New-Commit $normalRoot 'unrelated development baseline'
    $pinnedRevision = New-Commit $normalRoot 'compatibility pin'
    [IO.File]::WriteAllText(
        $manifestPath,
        (@{
            schemaVersion = 'rimtest-cross-stack-compatibility/v1'
            repositories = @{ devBridge2 = @{ repository = 'fixture/DevBridge2'; revision = $pinnedRevision } }
        } | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    [IO.File]::WriteAllText((Join-Path $normalRoot 'unrelated.txt'), 'must remain', [Text.UTF8Encoding]::new($false))
    $beforeStatus = @(Invoke-Git $normalRoot @('status', '--porcelain', '--untracked-files=all'))
    $beforeHead = ([string](Invoke-Git $normalRoot @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
    $resolution = Resolve-PinnedDevBridgeWorktree -RimLiaisonRoot $rimLiaisonRoot -DevBridgeRoot $normalRoot -ManifestPath $manifestPath
    $testsRun++
    Assert-True (-not [bool]$resolution.usedNormalCheckout) 'a different dirty checkout uses an isolated worktree'
    Assert-Equal $resolution.pinnedRevision $pinnedRevision 'resolved revision comes from the manifest'
    Assert-Equal (Get-PinnedDevBridgeGitHead $resolution.resolvedRoot) $pinnedRevision 'isolated worktree head matches the pin'
    $worktreePath = [string]$resolution.worktreePath
    $afterStatus = @(Invoke-Git $normalRoot @('status', '--porcelain', '--untracked-files=all'))
    $afterHead = ([string](Invoke-Git $normalRoot @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
    $testsRun++
    Assert-Equal ($afterStatus -join "`n") ($beforeStatus -join "`n") 'dirty normal checkout remains unchanged'
    Assert-Equal $afterHead $beforeHead 'normal checkout revision remains unchanged'

    $cached = Resolve-PinnedDevBridgeWorktree -RimLiaisonRoot $rimLiaisonRoot -DevBridgeRoot $normalRoot -ManifestPath $manifestPath
    $testsRun++
    Assert-Equal $cached.materialization 'cached-worktree' 'the exact pinned worktree is reused'
    Assert-Equal $cached.resolvedRoot $worktreePath 'the pinned worktree path is stable'

    $wrongManifest = Join-Path $rimLiaisonRoot 'contracts\wrong.json'
    [IO.File]::WriteAllText(
        $wrongManifest,
        (@{
            schemaVersion = 'rimtest-cross-stack-compatibility/v1'
            repositories = @{ devBridge2 = @{ repository = 'fixture/DevBridge2'; revision = ('0' * 40) } }
        } | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    $wrongFailed = $false
    try {
        Resolve-PinnedDevBridgeWorktree -RimLiaisonRoot $rimLiaisonRoot -DevBridgeRoot $normalRoot -ManifestPath $wrongManifest | Out-Null
    } catch {
        $wrongFailed = $_.Exception.Message -like 'CROSS_STACK_PIN_UNAVAILABLE:*'
    }
    $testsRun++
    Assert-True $wrongFailed 'unavailable pinned revision fails instead of falling back'

    Write-Output ("PASS pinned DevBridge2 worktree tests=$testsRun"); exit 0
}
finally {
    if ($null -ne $worktreePath -and (Test-Path -LiteralPath $worktreePath)) {
        Remove-PinnedDevBridgeWorktree -DevBridgeRoot $normalRoot -WorktreePath $worktreePath
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
