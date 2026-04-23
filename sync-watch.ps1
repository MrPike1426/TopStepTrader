# sync-watch.ps1
# Watches the remote GitHub repo and pulls automatically whenever the agent pushes.
# Usage: .\sync-watch.ps1
#        .\sync-watch.ps1 -IntervalSeconds 60 -Branch main

param(
    [string] $Branch         = "clean-start",
    [int]    $IntervalSeconds = 30
)

$RepoPath = $PSScriptRoot
$Remote   = "origin"

function Get-LocalCommit  { git -C $RepoPath rev-parse HEAD }
function Get-RemoteCommit { git -C $RepoPath rev-parse "$Remote/$Branch" }
function Has-UncommittedChanges {
    $status = git -C $RepoPath status --porcelain
    return ($null -ne $status -and $status.Trim().Length -gt 0)
}

Write-Host "🔄  sync-watch started — polling $Remote/$Branch every ${IntervalSeconds}s" -ForegroundColor Cyan
Write-Host "    Repo : $RepoPath"
Write-Host "    Press Ctrl+C to stop.`n"

while ($true) {
    try {
        # Remove any stale lock files before git operations
        Get-ChildItem "$RepoPath\.git" -Recurse -Filter "*.lock" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue

        # Fetch quietly so we get the latest remote refs
        git -C $RepoPath fetch $Remote $Branch --quiet 2>$null

        $local  = Get-LocalCommit
        $remote = Get-RemoteCommit

        if ($local -ne $remote) {
            $timestamp = Get-Date -Format "HH:mm:ss"

            if (Has-UncommittedChanges) {
                Write-Host "[$timestamp] ⚠️  Remote has new commits but you have uncommitted changes — skipping pull." -ForegroundColor Yellow
                Write-Host "             Commit or stash your changes, then the next cycle will pull automatically."
            } else {
                Write-Host "[$timestamp] ⬇️  New commits detected — pulling..." -ForegroundColor Green
                $result = git -C $RepoPath pull $Remote $Branch 2>&1
                Write-Host "             $result" -ForegroundColor Gray
                Write-Host "[$timestamp] ✅  Up to date with $Remote/$Branch  ($(Get-RemoteCommit))" -ForegroundColor Green
            }
        }
    }
    catch {
        Write-Host "[$( Get-Date -Format 'HH:mm:ss' )] ❌  Error: $_" -ForegroundColor Red
    }

    Start-Sleep -Seconds $IntervalSeconds
}
