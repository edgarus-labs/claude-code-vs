$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Publish-Marketplace.ps1'
$repoRoot = Join-Path $PSScriptRoot '../..'
$vsixProject = Join-Path $repoRoot 'src/ClaudeCode.Vsix'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$work = Join-Path ([IO.Path]::GetTempPath()) "publish-marketplace-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null

function New-TestVsix([string] $Path, [string] $Publisher) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry('extension.vsixmanifest').Open())
        try {
            $writer.Write(@"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Id="Test.Extension" Version="1.2.3" Language="en-US" Publisher="$Publisher" />
  </Metadata>
</PackageManifest>
"@)
        }
        finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
}

# Stands in for VsixPublisher.exe: records its arguments and exits with FAKE_PUBLISHER_EXIT.
$fakePublisher = Join-Path $work 'FakePublisher.cmd'
$invocationLog = Join-Path $work 'invocation.txt'
Set-Content -Path $fakePublisher -Encoding ascii -Value @"
@echo off
echo %*> "$invocationLog"
exit /b %FAKE_PUBLISHER_EXIT%
"@

$publishManifest = Join-Path $work 'publishManifest.json'
Set-Content -Path $publishManifest -Encoding utf8 -Value '{ "publisher": "edgarus-labs" }'
$matchingVsix = Join-Path $work 'matching.vsix'
New-TestVsix -Path $matchingVsix -Publisher 'edgarus-labs'
$mismatchedVsix = Join-Path $work 'mismatched.vsix'
New-TestVsix -Path $mismatchedVsix -Publisher 'EdgarusLabs'

$cases = @(
    @{ Name = 'Reject missing token'; Vsix = $matchingVsix; Token = ''; Exit = 0; Error = 'VS_MARKETPLACE_PAT'; Invoked = $false },
    @{ Name = 'Reject whitespace token'; Vsix = $matchingVsix; Token = '   '; Exit = 0; Error = 'VS_MARKETPLACE_PAT'; Invoked = $false },
    @{ Name = 'Reject VSIX publisher differing from Marketplace publisher'; Vsix = $mismatchedVsix; Token = 'token'; Exit = 0; Error = "'EdgarusLabs' does not match"; Invoked = $false },
    @{ Name = 'Fail when VsixPublisher fails'; Vsix = $matchingVsix; Token = 'token'; Exit = 3; Error = 'exit code 3'; Invoked = $true },
    @{ Name = 'Publish matching VSIX'; Vsix = $matchingVsix; Token = 'token'; Exit = 0; Invoked = $true }
)

$failures = @()
try {
    foreach ($case in $cases) {
        Remove-Item -Path $invocationLog -ErrorAction SilentlyContinue
        $env:FAKE_PUBLISHER_EXIT = $case.Exit
        try {
            & $script -VsixPath $case.Vsix -PublishManifestPath $publishManifest -PublisherPath $fakePublisher -PersonalAccessToken $case.Token | Out-Null
            if ($case.ContainsKey('Error')) {
                $failures += "$($case.Name): expected rejection containing '$($case.Error)'."
            }
        }
        catch {
            if (-not $case.ContainsKey('Error') -or $_.Exception.Message -notlike "*$($case.Error)*") {
                $failures += "$($case.Name): $($_.Exception.Message)"
            }
        }

        $invoked = Test-Path $invocationLog
        if ($invoked -ne $case.Invoked) {
            $failures += "$($case.Name): expected VsixPublisher invoked = $($case.Invoked), was $invoked."
        }
        elseif ($invoked) {
            $arguments = (Get-Content -Path $invocationLog -Raw).Replace('"', '').Trim()
            $expected = "publish -payload $($case.Vsix) -publishManifest $publishManifest -personalAccessToken $($case.Token)"
            if ($arguments -cne $expected) {
                $failures += "$($case.Name): unexpected VsixPublisher arguments '$arguments'."
            }
        }
    }

    # The shipped configuration: the VSIX identity must carry the same Marketplace publisher ID that
    # publishManifest.json publishes under, or every release would stop at the check above.
    $manifestPublisher = (Get-Content -Path (Join-Path $vsixProject 'publishManifest.json') -Raw | ConvertFrom-Json).publisher
    $vsixManifest = [xml](Get-Content -Path (Join-Path $vsixProject 'source.extension.vsixmanifest') -Raw)
    $identityPublisher = $vsixManifest.PackageManifest.Metadata.Identity.Publisher
    if ($manifestPublisher -cne 'edgarus-labs' -or $identityPublisher -cne $manifestPublisher) {
        $failures += "Shipped publisher IDs: publishManifest.json '$manifestPublisher', vsixmanifest '$identityPublisher'; both must be 'edgarus-labs'."
    }
}
finally {
    Remove-Item Env:FAKE_PUBLISHER_EXIT -ErrorAction SilentlyContinue
    Remove-Item -Path $work -Recurse -Force
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "Passed $($cases.Count + 1) Marketplace publishing cases."
