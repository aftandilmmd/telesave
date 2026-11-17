# Builds the Microsoft Store bundle from the published x64 and arm64 apps.
#   make-msix.ps1 -Version 0.4.0 -PublishRoot ..\..\publish -OutDir ..\..\dist
# Identity values come from Partner Center › the app › Product identity (CI: repository variables).
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $PublishRoot,
  [Parameter(Mandatory)] [string] $OutDir,
  [string] $IdentityName = $env:MSSTORE_IDENTITY_NAME,
  [string] $Publisher = $env:MSSTORE_PUBLISHER,
  [string] $PublisherDisplayName = $env:MSSTORE_PUBLISHER_DISPLAY_NAME
)
$ErrorActionPreference = "Stop"
if (!$IdentityName -or !$Publisher -or !$PublisherDisplayName) { throw "Store identity missing (MSSTORE_IDENTITY_NAME, MSSTORE_PUBLISHER, MSSTORE_PUBLISHER_DISPLAY_NAME)" }

$here = $PSScriptRoot
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (!$sdk) { throw "makeappx.exe not found (Windows SDK)" }
$makeappx = $sdk.FullName

# Store versions have four parts and the last must be 0.
$parts = @(($Version -split '[-+]')[0] -split '\.') + @('0', '0', '0')
$storeVersion = ($parts[0..2] -join '.') + '.0'

$work = Join-Path ([IO.Path]::GetTempPath()) "telesave-msix"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$work\packages" | Out-Null
New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($arch in "x64", "arm64") {
  $layout = "$work\layout-$arch"
  Copy-Item (Join-Path $PublishRoot "win-$arch") $layout -Recurse
  Copy-Item "$here\Assets" "$layout\Assets" -Recurse
  (Get-Content "$here\AppxManifest.xml" -Raw).
    Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace('$Version$', $storeVersion).Replace('$Arch$', $arch) |
    Set-Content "$layout\AppxManifest.xml" -Encoding utf8
  & $makeappx pack /o /d $layout /p "$work\packages\TeleSave-$arch.msix" | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch" }
}
# Unsigned is fine: Partner Center signs Store packages.
& $makeappx bundle /o /bv $storeVersion /d "$work\packages" /p (Join-Path $OutDir "TeleSave-$Version-windows.msixbundle") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed" }
Get-ChildItem $OutDir -Filter *.msixbundle | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
