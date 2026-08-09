# Build the Shadowgain Console plugin and drop it where Decal looks.
#
#   powershell -ExecutionPolicy Bypass -File shadowgain\tools\plugin-build.ps1
#
# Copies ONLY ShadowgainConsole.dll. The build output also contains Decal.Adapter.dll and
# VirindiViewService.dll, and shipping those alongside the installed copies is a version-conflict
# waiting to happen - ACBridge ships its own DLL and nothing else, which is the convention here.
#
# CLOSE THE CLIENT FIRST. Decal holds the DLL open while the client is running, so a rebuild over
# a live client fails with a file lock rather than anything informative.

$ErrorActionPreference = "Stop"

$proj   = Join-Path $PSScriptRoot "..\plugin\ShadowgainConsole\ShadowgainConsole.csproj"
$out    = Join-Path $PSScriptRoot "..\plugin\ShadowgainConsole\bin\Release\ShadowgainConsole.dll"
$dest   = "C:\Games\Decal Plugins\ShadowgainConsole"

$msb = @(
  "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
  "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $msb) { throw "No MSBuild found. The .NET SDK cannot build an old-style v2.0 csproj." }

Write-Host "==> building" -ForegroundColor Cyan
& $msb $proj /p:Configuration=Release /v:minimal /nologo

if (-not (Test-Path $out)) { throw "Build reported success but produced no DLL at $out" }

# The two silent-failure modes, checked every build rather than once:
#   - view XML not embedded  -> plugin loads and never appears in game
#   - built AnyCPU/x64       -> Decal cannot load it at all
$bytes = [IO.File]::ReadAllBytes($out)
$txt   = [Text.Encoding]::UTF8.GetString($bytes)

if ($txt -notmatch 'ShadowgainConsole\.mainView\.xml') {
    throw "mainView.xml is NOT embedded - check it is an EmbeddedResource. The plugin would load but never show."
}
if ($txt -notmatch 'DecalControls\.Notebook') {
    throw "view XML content missing from the assembly."
}

$fs = [IO.File]::OpenRead($out); $br = New-Object IO.BinaryReader($fs)
$fs.Position = 0x3C; $pe = $br.ReadInt32(); $fs.Position = $pe + 4
$machine = $br.ReadUInt16(); $fs.Close()

if ($machine -ne 0x014C) {
    throw ("PE machine is 0x{0:X4}, expected 0x014C (32-bit). Decal.Adapter is x86." -f $machine)
}

Write-Host "    view embedded, 32-bit - OK" -ForegroundColor Green

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $out $dest -Force

# poi.tsv rides along: the plugin reads it from beside the DLL at startup to fill the POI
# dropdown. Shipping it here keeps the deployed copy in step with the exported one - otherwise
# the dropdown silently reflects whatever was copied by hand months ago.
$poi = Join-Path $PSScriptRoot "..\gui\poi.tsv"
if (Test-Path $poi) {
    Copy-Item $poi $dest -Force
    Write-Host ("    poi.tsv deployed ({0} destinations)" -f (Get-Content $poi).Count) -ForegroundColor Green
} else {
    Write-Warning "poi.tsv not found - the POI dropdown will be empty."
}

Write-Host "==> deployed to $dest" -ForegroundColor Cyan
Get-Item (Join-Path $dest "ShadowgainConsole.dll") | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
