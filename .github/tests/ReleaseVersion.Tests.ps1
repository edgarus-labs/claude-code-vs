$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Get-ReleaseVersion.ps1'

$cases = @(
    @{ Name = 'First release'; Tag = 'v1.0.0'; ExistingTags = @(); Expected = '1.0.0' },
    @{ Name = 'Current tag only'; Tag = 'v1.0.0'; ExistingTags = @('v1.0.0'); Expected = '1.0.0' },
    @{ Name = 'Ascending numeric versions'; Tag = 'v1.10.0'; ExistingTags = @('v1.10.0', 'v1.9.0'); Expected = '1.10.0' },
    @{ Name = 'Prerelease history'; Tag = 'v2.0.0'; ExistingTags = @('v2.0.0', 'v2.0.0-rc.1', 'v1.9.0'); Expected = '2.0.0' },
    @{ Name = 'Ignore nonrelease history'; Tag = 'v1.0.0'; ExistingTags = @('v1.0.0', 'v0.9.0-preview', 'v0.9.0.1', 'release-0.9.0'); Expected = '1.0.0' },
    @{ Name = 'Exclude current by name, not position'; Tag = 'v2.0.0'; ExistingTags = @('v1.9.0', 'v2.0.0', 'v1.8.0'); Expected = '2.0.0' },
    @{ Name = 'Reject tag older than highest other tag'; Tag = 'v1.5.0'; ExistingTags = @('v2.0.0', 'v1.0.0', 'v1.5.0'); Error = 'not newer' },
    @{ Name = 'Reject older tag absent from history'; Tag = 'v1.0.0'; ExistingTags = @('v2.0.0'); Error = 'not newer' },
    @{ Name = 'Reject equivalent numeric version'; Tag = 'v1.2.3'; ExistingTags = @('v1.2.3', 'v01.2.3'); Error = 'not newer' },
    @{ Name = 'Reject prerelease being published'; Tag = 'v1.0.0-rc.1'; ExistingTags = @(); Error = 'vX.Y.Z' },
    @{ Name = 'Reject four-part tag'; Tag = 'v1.0.0.1'; ExistingTags = @(); Error = 'vX.Y.Z' },
    @{ Name = 'Reject wrong-case prefix'; Tag = 'V1.0.0'; ExistingTags = @(); Error = 'vX.Y.Z' },
    @{ Name = 'Reject Unicode version digits'; Tag = 'v١.0.0'; ExistingTags = @(); Error = 'vX.Y.Z' }
)

$failures = @()
foreach ($case in $cases) {
    try {
        $result = & $script -Tag $case.Tag -ExistingTags $case.ExistingTags
        if ($case.ContainsKey('Error')) {
            $failures += "$($case.Name): expected rejection containing '$($case.Error)', got '$result'."
        }
        elseif ($result -cne $case.Expected) {
            $failures += "$($case.Name): expected '$($case.Expected)', got '$result'."
        }
    }
    catch {
        if (-not $case.ContainsKey('Error') -or $_.Exception.Message -notlike "*$($case.Error)*") {
            $failures += "$($case.Name): $($_.Exception.Message)"
        }
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "Passed $($cases.Count) release version cases."
