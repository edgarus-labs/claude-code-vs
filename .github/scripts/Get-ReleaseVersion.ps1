param(
    [Parameter(Mandatory)]
    [string] $Tag,
    [Parameter(Mandatory)]
    [AllowEmptyCollection()]
    [string[]] $ExistingTags
)

$ErrorActionPreference = 'Stop'

$tagPattern = '\Av([0-9]+\.[0-9]+\.[0-9]+)\z'
if ($Tag -cnotmatch $tagPattern) {
    throw "Tag '$Tag' does not match the required vX.Y.Z format."
}
$version = $Matches[1]
$current = [version]$version
$previous = $ExistingTags |
    Where-Object { $_ -cne $Tag -and $_ -cmatch $tagPattern } |
    ForEach-Object { [version]$_.Substring(1) } |
    Sort-Object -Descending |
    Select-Object -First 1

if ($null -ne $previous -and $current -le $previous) {
    throw "Tag version $current is not newer than the previous release tag $previous."
}

$version
