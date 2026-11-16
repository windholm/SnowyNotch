# Builds a SnowyNotch release.
# Run from the project folder:  powershell -ExecutionPolicy Bypass -File .\build-release.ps1
# Output goes to ..\SnowyNotch-release\
#   Release\            - exe files to attach to a GitHub Release
#   GitHub\SnowyNotch\  - clean source tree for the repository (no bin/obj)

$ErrorActionPreference = 'Stop'
$proj    = $PSScriptRoot
$version = ([xml](Get-Content "$proj\SnowyNotch.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out     = Join-Path (Split-Path $proj -Parent) 'SnowyNotch-release'
$rel     = Join-Path $out 'Release'
$src     = Join-Path $out 'GitHub\SnowyNotch'
$tmp     = Join-Path $out '_publish'

Get-Process 'SnowyNotch*' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $rel, $src | Out-Null

# 1) Full: single exe with the .NET runtime bundled, nothing to install.
dotnet publish "$proj\SnowyNotch.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$tmp\full"
if ($LASTEXITCODE -ne 0) { throw 'publish (full) failed' }
Copy-Item "$tmp\full\SnowyNotch.exe" "$rel\SnowyNotch-$version-win-x64.exe"

# 2) Lite: small exe, requires the .NET 8 Desktop Runtime.
dotnet publish "$proj\SnowyNotch.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o "$tmp\lite"
if ($LASTEXITCODE -ne 0) { throw 'publish (lite) failed' }
Copy-Item "$tmp\lite\SnowyNotch.exe" "$rel\SnowyNotch-$version-win-x64-lite.exe"

Remove-Item $tmp -Recurse -Force

# 3) Source for GitHub: everything except build output and private notes.
$skip = @('bin', 'obj', '.vs', '.git', 'docs')
Get-ChildItem $proj -Force | Where-Object { $skip -notcontains $_.Name -and $_.Name -notlike '*.user' } |
    Copy-Item -Destination $src -Recurse -Force

Get-FileHash "$rel\*.exe" -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $(Split-Path $_.Path -Leaf)" } |
    Set-Content "$rel\SHA256SUMS.txt" -Encoding ascii

Write-Host "`nDone: $out" -ForegroundColor Green
Get-ChildItem $rel | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
