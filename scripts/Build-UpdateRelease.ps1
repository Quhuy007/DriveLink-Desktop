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
 # Tải bản phát hành trước vào thư mục đầu ra để Velopack tạo gói delta: máy đã cài chỉ tải phần thay đổi.
 & dotnet vpk download github --repoUrl 'https://github.com/Quhuy007/DriveLink-Desktop' --channel win-x64 -o $output
 if ($LASTEXITCODE -ne 0) { Write-Warning 'Không tải được bản trước; bản này sẽ không có gói delta.' }
 # App không kèm .NET: Setup và trình cập nhật tự cài .NET Desktop Runtime 10 nếu máy chưa có (hỏi người dùng trước).
 & dotnet vpk pack --framework net10.0-x64-desktop --packId QuangHuy.DriveLink --packVersion $Version --packDir "bin/Portable-v$Version-win-x64" --mainExe DriveLink.exe --packAuthors 'Quang Huy' --packTitle 'DriveLink — Quang Huy' --icon Assets/DriveLink.ico --runtime win-x64 --channel win-x64 --releaseNotes RELEASE-NOTES.md --outputDir $output
 if ($LASTEXITCODE -ne 0) { throw 'Không tạo được bộ cài và gói cập nhật.' }
 # Gói của bản trước chỉ dùng để tạo delta, không thuộc bản này nên không ký và không tải lên lại.
 Get-ChildItem -LiteralPath $output -Filter '*.nupkg' -File | Where-Object { $_.Name -notlike "*-$Version-*" } | Remove-Item
 $hashes = Get-ChildItem -LiteralPath $output -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
 $hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
 Write-Output "Bộ cài và nguồn cập nhật: $output"
} finally { Pop-Location }
