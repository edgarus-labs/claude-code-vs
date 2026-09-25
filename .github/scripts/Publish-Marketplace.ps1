param(
    [Parameter(Mandatory)]
    [string] $VsixPath,
    [Parameter(Mandatory)]
    [string] $PublishManifestPath,
    [Parameter(Mandatory)]
    [string] $PublisherPath,
    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string] $PersonalAccessToken
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PersonalAccessToken)) {
    throw 'The Visual Studio Marketplace personal access token is empty; configure the VS_MARKETPLACE_PAT repository secret.'
}

$marketplacePublisher = (Get-Content -Path $PublishManifestPath -Raw | ConvertFrom-Json).publisher

Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsix = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -Path $VsixPath).ProviderPath)
try {
    $entry = $vsix.GetEntry('extension.vsixmanifest')
    if (-not $entry) {
        throw "'$VsixPath' has no extension.vsixmanifest."
    }
    $reader = New-Object IO.StreamReader($entry.Open())
    try {
        $vsixManifest = [xml]$reader.ReadToEnd()
    }
    finally { $reader.Dispose() }
}
finally { $vsix.Dispose() }

$vsixPublisher = $vsixManifest.PackageManifest.Metadata.Identity.Publisher
if ($vsixPublisher -cne $marketplacePublisher) {
    throw "VSIX publisher '$vsixPublisher' does not match Marketplace publisher '$marketplacePublisher' in '$PublishManifestPath'."
}

& $PublisherPath publish -payload $VsixPath -publishManifest $PublishManifestPath -personalAccessToken $PersonalAccessToken
if ($LASTEXITCODE -ne 0) {
    throw "VsixPublisher failed with exit code $LASTEXITCODE; the Visual Studio Marketplace did not accept '$VsixPath'."
}
