param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DriveLink.csproj')
if (!$Version) { $Version = [string]$project.Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.]+)?$') { throw 'Version không hợp lệ.' }
$releasePath = Join-Path $projectRoot "versions\v$Version"
if (Test-Path -LiteralPath $releasePath) { throw "Đã tồn tại v$Version. Chọn số phiên bản mới để giữ nguyên các bản trước." }
$defaultsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Services\CloudBuildSettings.cs') -Raw
if ($defaultsSource -notmatch 'const string Payload = "[A-Za-z0-9+/=]+"') { throw 'Chưa có Cloud mặc định. Chạy scripts/Set-CloudDefaults.ps1 với hồ sơ kết nối công khai trước khi phát hành.' }
$stagePath = Join-Path $projectRoot "bin\Staging-v$Version"
& dotnet build (Join-Path $projectRoot 'DriveLink.csproj') -c Release "-p:Version=$Version" -o $stagePath
if ($LASTEXITCODE -ne 0) { throw 'Biên dịch không thành công, chưa tạo phiên bản.' }
New-Item -ItemType Directory -Path $releasePath | Out-Null
Get-ChildItem -LiteralPath $stagePath -File | Copy-Item -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $projectRoot 'CLOUD-OAUTH.md') -Destination $releasePath
$sourceNames = @('App.xaml','App.xaml.cs','AppBrand.cs','DriveLink.csproj','GlobalUsings.cs','MainWindow.xaml','MainWindow.xaml.cs','LoginStatusWindow.xaml','LoginStatusWindow.xaml.cs','Models.cs','Dialogs.cs','CloudConfigWindow.cs','README.md','CHANGELOG.md','Assets\DriveLink.png','Assets\DriveLink-web.png','Assets\DriveLink.ico','tests\Program.cs','tests\DriveLink.Checks.csproj','scripts\Build-Release.ps1')
$sourceNames += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'supabase') -File | ForEach-Object { "supabase\$($_.Name)" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'supabase') -Destination (Join-Path $releasePath 'supabase') -Recurse
$sourceNames += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Services') -Filter '*.cs' -File | ForEach-Object { "Services\$($_.Name)" }
$sourceNames += @('tests\CloudChecks.cs')
$sourceNames += @('scripts\Set-CloudDefaults.ps1')
$sourceNames += @('CLOUD-OAUTH.md')
$sourceNames += @('AdminSignInWindow.cs')
$sourceNames += @('global.json','GITHUB-RELEASES.md','scripts\Publish-Portable.ps1','.github\workflows\release.yml','.gitignore')
$sourceNames += @('UpdateWindow.cs','tests\UpdateChecks.cs','.config\dotnet-tools.json','scripts\Build-UpdateRelease.ps1','RELEASE-NOTES.md')
$sourceNames += @('UiPresentation.cs','FileWindows.cs','tests\FileActionChecks.cs','tests\LayoutChecks.cs')
Add-Type -AssemblyName System.IO.Compression
$archivePath = Join-Path $releasePath "source-v$Version.zip"
$archive = [System.IO.Compression.ZipFile]::Open($archivePath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
 foreach ($relative in $sourceNames) {
  $sourcePath = Join-Path $projectRoot $relative
  if (Test-Path -LiteralPath $sourcePath) { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sourcePath, $relative.Replace('\','/')) | Out-Null }
 }
} finally { $archive.Dispose() }
$manifest = [ordered]@{ version = $Version; author = 'Quang Huy'; createdUtc = [DateTime]::UtcNow.ToString('o'); runtime = '.NET Desktop Runtime 10'; files = @{} }
Get-ChildItem -LiteralPath $releasePath -File -Recurse | ForEach-Object { $manifest.files[$_.FullName.Substring($releasePath.Length + 1)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releasePath 'release.json') -Encoding utf8
Write-Output "Đã lưu bản v${Version}: $releasePath"
