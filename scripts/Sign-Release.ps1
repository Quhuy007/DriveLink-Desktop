#Requires -Version 7
<#
Ký một bản phát hành DriveLink trên máy phát hành (sau Build-Release.ps1 và Build-UpdateRelease.ps1).
Tạo trong artifacts/updates-v<phiên bản>/:
- DriveLink-release-manifest.json: tên và SHA-256 của mọi tệp sẽ tải lên Release (gói cập nhật, Setup, Portable, feed, mã nguồn).
- DriveLink-release-manifest.json.sig: chữ ký ECDSA P-256 của tệp trên, bằng khóa riêng tạo bởi New-UpdateSigningKey.ps1.
App chỉ cài bản cập nhật có chữ ký hợp lệ; người tải có thể tự kiểm bằng Verify-Release.ps1.
#>
param(
    [string]$Version = '',
    [string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'DriveLink-Signing')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DriveLink.csproj')
if (!$Version) { $Version = [string]$project.Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version không hợp lệ.' }
$updates = Join-Path $projectRoot "artifacts\updates-v$Version"
$source = Join-Path $projectRoot "versions\v$Version\source-v$Version.zip"
if (!(Test-Path -LiteralPath $updates) -or !(Test-Path -LiteralPath $source)) { throw "Chưa có artifacts\updates-v$Version hoặc versions\v$Version\source-v$Version.zip. Chạy Build-Release.ps1 và Build-UpdateRelease.ps1 trước." }
$keyPath = Join-Path $KeyDirectory 'update-signing-key.dat'
if (!(Test-Path -LiteralPath $keyPath)) { throw "Chưa có khóa ký tại $keyPath. Chạy scripts/New-UpdateSigningKey.ps1 trước." }

$manifestName = 'DriveLink-release-manifest.json'
$files = [ordered]@{}
Get-ChildItem -LiteralPath $updates -File | Where-Object { $_.Name -notlike "$manifestName*" } | Sort-Object Name | ForEach-Object { $files[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
$files[(Split-Path $source -Leaf)] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{ product = 'DriveLink'; version = $Version; created = [DateTime]::UtcNow.ToString('o'); files = $files }
$bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 4 -Compress))

$entropy = [Text.Encoding]::UTF8.GetBytes('DriveLink update signing key v1')
$pkcs8 = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $entropy, 'CurrentUser')
$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportPkcs8PrivateKey($pkcs8, [ref]0) | Out-Null
    $signature = [Convert]::ToBase64String($ecdsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
    if (!$ecdsa.VerifyData($bytes, [Convert]::FromBase64String($signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Chữ ký vừa tạo không tự kiểm được.' }
} finally { [Array]::Clear($pkcs8, 0, $pkcs8.Length); $ecdsa.Dispose() }

[IO.File]::WriteAllBytes((Join-Path $updates $manifestName), $bytes)
[IO.File]::WriteAllText((Join-Path $updates "$manifestName.sig"), $signature)
Copy-Item -LiteralPath $source -Destination $updates -Force
Write-Output "Đã ký $($files.Count) tệp của bản v${Version}: $updates"
