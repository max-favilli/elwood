param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$')][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required to record bundle provenance.' }
if (& git -C $root status --porcelain) { throw 'Commit or isolate working changes before packaging a versioned consumer bundle.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ".private/releases/$Version-$($commit.Substring(0,8))" }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output directory already exists; choose a new directory to avoid mixing releases.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projects = [ordered]@{ 'functions' = 'Elwood.Runtime.Azure.Functions'; 'api' = 'Elwood.Runtime.Api'; 'ingress' = 'Elwood.Runtime.Azure.Ingress' }
foreach ($entry in $projects.GetEnumerator()) {
    $publish = Join-Path $output "publish-$($entry.Key)"
    $project = Join-Path $root "dotnet/src/$($entry.Value)/$($entry.Value).csproj"
    $projectVersion = (& dotnet msbuild $project -getProperty:Version).Trim()
    if ($LASTEXITCODE -ne 0 -or $projectVersion -ne $Version) { throw "Requested version does not match $($entry.Value): $projectVersion." }
    # Do not override Version globally: Core and other unchanged dependencies
    # intentionally keep their own package/assembly versions.
    & dotnet publish $project -c Release --no-self-contained -o $publish --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($entry.Key) failed." }
    $forbidden = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -in @('secrets.json','local.settings.json') -or $_.Extension -in @('.pfx','.pem') -or $_.FullName -match '[\\/]\.elwood[\\/]' }
    if ($forbidden) { throw "Publishing $($entry.Key) included a private configuration/certificate file. Inspect the local output; nothing was uploaded." }
    if ($entry.Key -eq 'functions') {
        foreach ($required in @('host.json','worker.config.json','functions.metadata','.azurefunctions')) {
            if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Functions package is missing $required." }
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $publish 'LICENSE')
    # ZipFile includes the hidden .azurefunctions directory on Unix; Compress-Archive does not.
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, (Join-Path $output "elwood-$($entry.Key)-$Version.zip"))
}
$kit = Join-Path $output 'consumer-kit'
New-Item -ItemType Directory -Path $kit | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $kit 'LICENSE')
Set-Content -LiteralPath (Join-Path $kit 'README.md') -Value "# Elwood $Version installation kit`n`nStart with [the pilot handoff](docs/pilot-handoff.md). Check release.json and SHA256SUMS.txt from the release before deploying. This kit contains installation resources; the separate runtime ZIPs contain the applications." -Encoding utf8
$tracked = [string]::Join("`n", @(& git -C $root ls-files -z -- infra docs scripts))
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked consumer resources.' }
foreach ($relative in $tracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
    $destination = Join-Path $kit $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
}
$manifest = @{ version=$Version; gitCommit=$commit; dotnet='10.0'; artifacts=@($projects.Keys); createdAt=[DateTimeOffset]::UtcNow.ToString('o') }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $kit 'release.json') -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($kit, (Join-Path $output "elwood-consumer-kit-$Version.zip"))
Get-ChildItem -LiteralPath $output -Filter '*.zip' | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Runtime packages and checksums prepared in $output"
