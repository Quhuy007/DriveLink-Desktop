#Requires -Version 7
<#
Phát hành một bản DriveLink đã build và KÝ trên máy này lên GitHub Releases.
Thứ tự: Build-Release.ps1 → Build-UpdateRelease.ps1 → Sign-Release.ps1 → Publish-Release.ps1.
1. Kiểm lại chữ ký của artifacts/updates-v<phiên bản> (dừng nếu sai).
2. Đưa mã nguồn của bản đó (versions/v<phiên bản>/source-v<phiên bản>.zip) lên nhánh main của repo, gắn tag v<phiên bản>.
3. Tạo Release và tải lên đúng các tệp đã ký. GitHub Actions chỉ chạy kiểm thử, không tự phát hành.
Dùng đăng nhập GitHub đã lưu trong Git Credential Manager; token chỉ nằm trong bộ nhớ của script, không in ra, không ghi xuống đĩa.
#>
param(
    [string]$Version = '',
    [string]$Repository = 'Quhuy007/DriveLink-Desktop',
    [string]$Clone = (Join-Path $env:LOCALAPPDATA 'DriveLink-Release\repo')
)
$ErrorActionPreference = 'Stop'
$env:GIT_TERMINAL_PROMPT = '0'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DriveLink.csproj')
if (!$Version) { $Version = [string]$project.Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version không hợp lệ.' }
$tag = "v$Version"
$updates = Join-Path $projectRoot "artifacts\updates-v$Version"
$source = Join-Path $projectRoot "versions\v$Version\source-v$Version.zip"

& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Folder $updates
$signed = (Get-Content -LiteralPath (Join-Path $updates 'DriveLink-release-manifest.json') -Raw | ConvertFrom-Json).files.PSObject.Properties.Name
$assets = @(Get-ChildItem -LiteralPath $updates -File | Where-Object { $_.Name -in $signed -or $_.Name -like 'DriveLink-release-manifest.json*' })

if (!(Test-Path -LiteralPath (Join-Path $Clone '.git'))) {
    New-Item -ItemType Directory -Path (Split-Path $Clone) -Force | Out-Null
    git clone -q "https://github.com/$Repository.git" $Clone
    if ($LASTEXITCODE -ne 0) { throw 'Không clone được repo.' }
}
git -C $Clone checkout -q main; git -C $Clone pull -q --ff-only
if ($LASTEXITCODE -ne 0) { throw 'Không cập nhật được bản clone (nhánh main đã bị đổi ngoài ý muốn?). Kiểm tra trước khi phát hành.' }
if (git -C $Clone tag --list $tag) { throw "Tag $tag đã tồn tại. Không ghi đè bản đã phát hành; hãy tăng số phiên bản." }
# Repo phải khớp đúng mã nguồn đã ký: bỏ mọi tệp đang theo dõi rồi giải nén, để tệp đã xóa (ví dụ workflow phát hành cũ) cũng bị xóa trên GitHub.
git -C $Clone rm -r -q --cached . | Out-Null
Get-ChildItem -LiteralPath $Clone -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
Expand-Archive -LiteralPath $source -DestinationPath $Clone -Force
[xml]$cloned = Get-Content -LiteralPath (Join-Path $Clone 'DriveLink.csproj')
if ([string]$cloned.Project.PropertyGroup.Version -ne $Version) { throw 'Mã nguồn trong ZIP không khớp số phiên bản.' }
git -C $Clone add -A
git -C $Clone -c core.autocrlf=true commit -q -m "Release ${tag}" -m (Get-Content -LiteralPath (Join-Path $projectRoot 'RELEASE-NOTES.md') -Raw)
if ($LASTEXITCODE -ne 0) { throw 'Không tạo được commit (không có thay đổi?).' }
git -C $Clone tag $tag
git -C $Clone push -q origin main; if ($LASTEXITCODE -ne 0) { throw 'Không đẩy được nhánh main.' }
git -C $Clone push -q origin $tag; if ($LASTEXITCODE -ne 0) { throw 'Không đẩy được tag.' }

$credential = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($credential | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if (!$token) { throw 'Chưa có đăng nhập GitHub trong Git Credential Manager. Đăng nhập bằng git hoặc GitHub Desktop rồi chạy lại.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
try {
    $body = @{ tag_name = $tag; name = "DriveLink $tag — Quang Huy"; body = (Get-Content -LiteralPath (Join-Path $projectRoot 'RELEASE-NOTES.md') -Raw); draft = $true } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repository/releases" -Headers $headers -Body $body -ContentType 'application/json; charset=utf-8'
    $upload = $release.upload_url -replace '\{.*\}$', ''
    foreach ($asset in $assets) {
        Invoke-RestMethod -Method Post -Uri ($upload + '?name=' + [Uri]::EscapeDataString($asset.Name)) -Headers $headers -InFile $asset.FullName -ContentType 'application/octet-stream' | Out-Null
        Write-Output "  đã tải lên $($asset.Name)"
    }
    # Chỉ công bố sau khi mọi tệp đã lên đủ, để máy khách không thấy một bản thiếu tệp chữ ký.
    $published = Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" -Headers $headers -Body (@{ draft = $false; make_latest = 'true' } | ConvertTo-Json) -ContentType 'application/json'
    Write-Output "Đã phát hành: $($published.html_url)"
} finally { $token = $null; $headers = $null }
