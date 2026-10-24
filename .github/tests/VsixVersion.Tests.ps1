$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Confirm-VsixVersion.ps1'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$work = Join-Path ([IO.Path]::GetTempPath()) "vsix-version-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null

function New-TestAssembly([string] $Path, [string] $AssemblyVersion, [string] $FileVersion, [string] $InformationalVersion) {
    Add-Type -OutputType Library -OutputAssembly $Path -TypeDefinition @"
[assembly: System.Reflection.AssemblyVersion("$AssemblyVersion")]
[assembly: System.Reflection.AssemblyFileVersion("$FileVersion")]
[assembly: System.Reflection.AssemblyInformationalVersion("$InformationalVersion")]
namespace VsixVersionTests { public static class Marker { } }
"@
}

function Add-TextEntry($Zip, [string] $Name, [string] $Text) {
    $writer = New-Object IO.StreamWriter($Zip.CreateEntry($Name).Open())
    try { $writer.Write($Text) }
    finally { $writer.Dispose() }
}

function New-TestVsix([hashtable] $Case) {
    $values = @{
        FileName             = 'edgarus-labs.claude-code.vs.v1.7.0.vsix'
        ManifestVersion      = '1.7.0'
        AssemblyVersion      = '1.7.0.0'
        FileVersion          = '1.7.0.0'
        InformationalVersion = '1.7.0+0123456789abcdef'
        ProductId            = '1.7.0'
        SidecarAssemblyVersion = '1.7.0.0'
        IncludeAssembly      = $true
    }
    foreach ($key in @($values.Keys)) {
        if ($Case.ContainsKey($key)) { $values[$key] = $Case[$key] }
    }

    $directory = Join-Path $work ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $vsixPath = Join-Path $directory $values.FileName
    $zip = [IO.Compression.ZipFile]::Open($vsixPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-TextEntry $zip 'extension.vsixmanifest' @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Id="Test.Extension" Version="$($values.ManifestVersion)" Language="en-US" Publisher="edgarus-labs" />
  </Metadata>
</PackageManifest>
"@
        Add-TextEntry $zip 'EdgarusLabs.ClaudeCode.Vsix.pkgdef' @"
[`$RootKey`$\InstalledProducts\ClaudeCodePackage]
@="Claude Code for Visual Studio"
"PID"="$($values.ProductId)"
"ProductDetails"="Test"
"@
        if ($values.IncludeAssembly) {
            $assemblyPath = Join-Path $directory 'assembly.dll'
            New-TestAssembly -Path $assemblyPath -AssemblyVersion $values.AssemblyVersion -FileVersion $values.FileVersion -InformationalVersion $values.InformationalVersion
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $assemblyPath, 'EdgarusLabs.ClaudeCode.Vsix.dll') | Out-Null
        }
        $sidecarPath = Join-Path $directory 'sidecar.dll'
        New-TestAssembly -Path $sidecarPath -AssemblyVersion $values.SidecarAssemblyVersion -FileVersion '1.7.0.0' -InformationalVersion '1.7.0'
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $sidecarPath, 'VsControlMcp/EdgarusLabs.ClaudeCode.VsControl.Mcp.dll') | Out-Null
    }
    finally { $zip.Dispose() }
    $vsixPath
}

$cases = @(
    @{ Name = 'Accept consistent package with source revision suffix'; Tag = 'v1.7.0' },
    @{ Name = 'Accept consistent package without source revision suffix'; Tag = 'v1.7.0'; InformationalVersion = '1.7.0' },
    @{ Name = 'Reject legacy file name'; Tag = 'v1.7.0'; FileName = 'EdgarusLabs.ClaudeCode.v1.7.0.vsix'; Error = "file name 'EdgarusLabs.ClaudeCode.v1.7.0.vsix'" },
    @{ Name = 'Reject file name for another version'; Tag = 'v1.7.0'; FileName = 'edgarus-labs.claude-code.vs.v1.6.0.vsix'; Error = "file name 'edgarus-labs.claude-code.vs.v1.6.0.vsix'" },
    @{ Name = 'Reject stale manifest version'; Tag = 'v1.7.0'; ManifestVersion = '0.2.0'; Error = "manifest version '0.2.0'" },
    @{ Name = 'Reject stale assembly version'; Tag = 'v1.7.0'; AssemblyVersion = '0.2.0.0'; Error = "assembly version '0.2.0.0'" },
    @{ Name = 'Reject stale file version'; Tag = 'v1.7.0'; FileVersion = '0.2.0.0'; Error = "file version '0.2.0.0'" },
    @{ Name = 'Reject informational version for another version'; Tag = 'v1.7.0'; InformationalVersion = '1.7.0.1'; Error = "informational version '1.7.0.1'" },
    @{ Name = 'Reject stale installed product version'; Tag = 'v1.7.0'; ProductId = '0.2.0'; Error = "installed product version '0.2.0'" },
    @{ Name = 'Reject stale version of another first-party assembly'; Tag = 'v1.7.0'; SidecarAssemblyVersion = '0.2.0.0'; Error = "VsControlMcp/EdgarusLabs.ClaudeCode.VsControl.Mcp.dll assembly version '0.2.0.0'" },
    @{ Name = 'Reject package without the extension assembly'; Tag = 'v1.7.0'; IncludeAssembly = $false; Error = 'EdgarusLabs.ClaudeCode.Vsix.dll' },
    @{ Name = 'Reject tag that is not vX.Y.Z'; Tag = 'v1.7'; FileName = 'edgarus-labs.claude-code.vs.v1.7.vsix'; Error = 'vX.Y.Z' }
)

$failures = @()
try {
    foreach ($case in $cases) {
        try {
            $vsix = New-TestVsix $case
            & $script -VsixPath $vsix -Tag $case.Tag | Out-Null
            if ($case.ContainsKey('Error')) {
                $failures += "$($case.Name): expected rejection containing '$($case.Error)'."
            }
        }
        catch {
            if (-not $case.ContainsKey('Error') -or $_.Exception.Message -notlike "*$($case.Error)*") {
                $failures += "$($case.Name): $($_.Exception.Message)"
            }
        }
    }
}
finally {
    Remove-Item -Path $work -Recurse -Force
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "Passed $($cases.Count) VSIX version consistency cases."
