param(
    [Parameter(Mandatory)]
    [string] $VsixPath,
    [Parameter(Mandatory)]
    [string] $Tag
)

$ErrorActionPreference = 'Stop'

if ($Tag -cnotmatch '\Av([0-9]+\.[0-9]+\.[0-9]+)\z') {
    throw "Tag '$Tag' does not match the required vX.Y.Z format."
}
$version = $Matches[1]
$numericVersion = "$version.0"
$assemblyEntryName = 'EdgarusLabs.ClaudeCode.Vsix.dll'
$pkgdefEntryName = 'EdgarusLabs.ClaudeCode.Vsix.pkgdef'

$mismatches = @()

$fileName = Split-Path -Path $VsixPath -Leaf
$expectedFileName = "edgarus-labs.claude-code.vs.v$version.vsix"
if ($fileName -cne $expectedFileName) {
    $mismatches += "VSIX file name '$fileName' is not '$expectedFileName'."
}

function Read-EntryText($Zip, [string] $Name) {
    $entry = $Zip.GetEntry($Name)
    if (-not $entry) {
        throw "'$VsixPath' has no $Name."
    }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Get-AssemblyAttributeValues([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $peReader = New-Object Reflection.PortableExecutable.PEReader($stream)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            $values = @{}
            foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
                $name = $metadata.GetString($type.Name)
                if ($name -cnotin @('AssemblyFileVersionAttribute', 'AssemblyInformationalVersionAttribute')) { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { continue }
                $values[$name] = $blob.ReadSerializedString()
            }
            $values
        }
        finally { $peReader.Dispose() }
    }
    finally { $stream.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$extracted = Join-Path ([IO.Path]::GetTempPath()) "vsix-version-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $extracted | Out-Null
try {
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -Path $VsixPath).ProviderPath)
    try {
        $manifest = [xml](Read-EntryText $zip 'extension.vsixmanifest')
        $pkgdef = Read-EntryText $zip $pkgdefEntryName
        $assemblyEntry = $zip.GetEntry($assemblyEntryName)
        if (-not $assemblyEntry) {
            throw "'$VsixPath' has no $assemblyEntryName."
        }
        $assemblyPath = Join-Path $extracted $assemblyEntryName
        [IO.Compression.ZipFileExtensions]::ExtractToFile($assemblyEntry, $assemblyPath)
    }
    finally { $zip.Dispose() }

    $manifestVersion = $manifest.PackageManifest.Metadata.Identity.Version
    if ($manifestVersion -cne $version) {
        $mismatches += "VSIX manifest version '$manifestVersion' is not '$version'."
    }

    $productIdMatch = [regex]::Match($pkgdef, '(?m)^"PID"="([^"]*)"')
    $productId = if ($productIdMatch.Success) { $productIdMatch.Groups[1].Value } else { '<missing>' }
    if ($productId -cne $version) {
        $mismatches += "Installed product version '$productId' in $pkgdefEntryName is not '$version'."
    }

    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
    if ($assemblyVersion -cne $numericVersion) {
        $mismatches += "$assemblyEntryName assembly version '$assemblyVersion' is not '$numericVersion'."
    }

    $attributes = Get-AssemblyAttributeValues $assemblyPath
    $fileVersion = "$($attributes['AssemblyFileVersionAttribute'])"
    if ($fileVersion -cne $numericVersion) {
        $mismatches += "$assemblyEntryName file version '$fileVersion' is not '$numericVersion'."
    }

    # The .NET SDK appends '+<source revision>' to the informational version; the version before it must match.
    $informationalVersion = "$($attributes['AssemblyInformationalVersionAttribute'])"
    if ($informationalVersion -cne $version -and -not $informationalVersion.StartsWith("$version+", [StringComparison]::Ordinal)) {
        $mismatches += "$assemblyEntryName informational version '$informationalVersion' is not '$version'."
    }
}
finally {
    Remove-Item -Path $extracted -Recurse -Force
}

if ($mismatches.Count -gt 0) {
    throw (@("VSIX '$VsixPath' does not match release tag '$Tag':") + $mismatches) -join [Environment]::NewLine
}

Write-Host "VSIX '$fileName' matches release tag '$Tag'."
