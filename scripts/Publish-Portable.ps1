param([string]$Version = '', [ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DriveLink.csproj')
$sourceVersion = [string]$project.Project.PropertyGroup.Version
if (!$Version) { $Version = $sourceVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne $sourceVersion) { throw 'Phiên bản phải khớp DriveLink.csproj.' }
$outputRoot = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$zipName = "DriveLink-v$Version-$Runtime.zip"
$zipPath = Join-Path $outputRoot $zipName
if (Test-Path -LiteralPath $zipPath) { throw 'Gói đã tồn tại. Không ghi đè bản đã đóng gói.' }
$publishPath = Join-Path $projectRoot "bin\Portable-v$Version-$Runtime"
& dotnet publish (Join-Path $projectRoot 'DriveLink.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Đóng gói Windows không thành công.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'CLOUD-OAUTH.md') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'GITHUB-RELEASES.md') -Destination $publishPath
$packageManifest = [ordered]@{ version = $Version; author = 'Quang Huy'; runtime = $Runtime; selfContained = $true; files = @{} }
Get-ChildItem -LiteralPath $publishPath -Recurse -File | Where-Object { $_.Name -ne 'release.json' } | ForEach-Object {
 $packageManifest.files[$_.FullName.Substring($publishPath.Length + 1)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$packageManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $publishPath 'release.json') -Encoding utf8
Add-Type -AssemblyName System.IO.Compression
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $zipName" | Set-Content -LiteralPath (Join-Path $outputRoot "$zipName.sha256") -Encoding ascii
Write-Output "Đã tạo gói Windows có sẵn .NET: $zipPath"
