param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DriveLink.csproj')
if (!$Version) { $Version = [string]$project.Project.PropertyGroup.Version }
if ($Version -ne [string]$project.Project.PropertyGroup.Version) { throw 'Version phải khớp mã nguồn.' }
Push-Location $projectRoot
try {
 $output = Join-Path $projectRoot "artifacts\updates-v$Version"
 if (Test-Path -LiteralPath $output) { throw 'Gói cập nhật đã tồn tại; tăng phiên bản trước khi đóng gói lại.' }
 & ./scripts/Publish-Portable.ps1 -Version $Version
 & dotnet tool restore
 if ($LASTEXITCODE -ne 0) { throw 'Không khôi phục được công cụ đóng gói.' }
 & dotnet vpk pack --packId QuangHuy.DriveLink --packVersion $Version --packDir "bin/Portable-v$Version-win-x64" --mainExe DriveLink.exe --packAuthors 'Quang Huy' --packTitle 'DriveLink — Quang Huy' --icon Assets/DriveLink.ico --runtime win-x64 --channel win-x64 --releaseNotes RELEASE-NOTES.md --outputDir $output
 if ($LASTEXITCODE -ne 0) { throw 'Không tạo được bộ cài và gói cập nhật.' }
 $hashes = Get-ChildItem -LiteralPath $output -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
 $hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
 Write-Output "Bộ cài và nguồn cập nhật: $output"
} finally { Pop-Location }
