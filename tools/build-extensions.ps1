# Builds every extension under Extensions/<Publisher>/<Name>/ and makes the two files ASD downloads:
#   dist/extensions.zip   one folder per extension (its DLL, extension.json, LICENSE*, icon.png, screenshots/)
#   dist/extensions.txt   line 1: SHA-256 of extensions.zip;  then one line per extension:  id | version | name | description
param([string]$Out = 'dist')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root $Out
$stage = Join-Path $dist 'stage'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function Clean([string]$s) { return ([string]$s -replace '[\r\n|]+', ' ').Trim() }

$lines = @()
$manifests = Get-ChildItem (Join-Path $root 'Extensions') -Recurse -Filter extension.json
if (-not $manifests) { throw 'No extension.json found under Extensions/.' }

foreach ($m in $manifests) {
    $dir = $m.DirectoryName
    $info = Get-Content $m.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($f in 'id', 'name', 'version', 'assembly', 'apiVersion') {
        if (-not $info.$f) { throw "$($m.FullName): missing ""$f""." }
    }
    $proj = Get-ChildItem $dir -Filter *.csproj | Select-Object -First 1
    if (-not $proj) { throw "$dir has no .csproj." }

    Write-Host "== building $($info.id) $($info.version)"
    $bin = Join-Path $dist ('bin-' + $info.id)
    dotnet build $proj.FullName -c Release -o $bin --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build of $($info.id) failed." }

    $dll = Join-Path $bin $info.assembly
    if (-not (Test-Path $dll)) { throw "$($info.id): $($info.assembly) was not produced." }

    $target = Join-Path $stage $info.id
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item $dll $target                                   # only the extension's own DLL: ASD already has the contract and Windows App SDK
    Copy-Item $m.FullName $target
    foreach ($extra in 'icon.png') { if (Test-Path (Join-Path $dir $extra)) { Copy-Item (Join-Path $dir $extra) $target } }
    Get-ChildItem $dir -Filter 'LICENSE*' -File | ForEach-Object { Copy-Item $_.FullName $target }
    if (Test-Path (Join-Path $dir 'screenshots')) { Copy-Item (Join-Path $dir 'screenshots') $target -Recurse }

    $lines += ('{0} | {1} | {2} | {3}' -f (Clean $info.id), (Clean $info.version), (Clean $info.name), (Clean $info.description))
}

$zip = Join-Path $dist 'extensions.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$txt = @($hash) + ($lines | Sort-Object)
[IO.File]::WriteAllLines((Join-Path $dist 'extensions.txt'), $txt, (New-Object Text.UTF8Encoding($false)))

Get-ChildItem $dist -Directory | Remove-Item -Recurse -Force   # leave only the two files
Write-Host "== done: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB), sha256 $hash"
