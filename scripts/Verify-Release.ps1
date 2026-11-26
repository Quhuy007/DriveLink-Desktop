#Requires -Version 7
<#
Kiểm một bản DriveLink đã tải về (dành cho người nhận, không cần khóa riêng):
  pwsh scripts/Verify-Release.ps1 -Folder <thư mục chứa các tệp tải từ Release>
Thư mục cần có DriveLink-release-manifest.json, tệp .sig đi kèm và các tệp muốn kiểm (Setup, Portable, mã nguồn…).
Kết quả "HỢP LỆ" nghĩa là tệp do chủ ứng dụng ký và không bị sửa sau khi ký, kể cả khi tài khoản GitHub đã bị chiếm.
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    # Khóa công khai của chủ ứng dụng; trùng với Services/UpdateTrust.cs.
    [string]$PublicKey = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGcCkgUBaf6aFeerRkradAL6D3gkpwrE0kCbZw9YrLeadh/71OsrMoHgkVHumH+Ksx6CBN0l20rZgMf69/RArng=='
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $Folder 'DriveLink-release-manifest.json'
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$signature = [Convert]::FromBase64String((Get-Content -LiteralPath "$manifestPath.sig" -Raw).Trim())
$ecdsa = [Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey), [ref]0) | Out-Null
if (!$ecdsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'KHÔNG HỢP LỆ: chữ ký không khớp khóa của chủ ứng dụng. Đừng cài các tệp này.' }
$manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
Write-Output "Chữ ký HỢP LỆ cho DriveLink $($manifest.version) (ký lúc $($manifest.created))."
$bad = 0
foreach ($entry in $manifest.files.PSObject.Properties) {
    $path = Join-Path $Folder $entry.Name
    if (!(Test-Path -LiteralPath $path)) { Write-Output "  -  $($entry.Name): không có trong thư mục (bỏ qua)"; continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $entry.Value) { Write-Output "  OK $($entry.Name)" } else { Write-Output "  SAI $($entry.Name): đã bị sửa sau khi ký"; $bad++ }
}
if ($bad -gt 0) { throw "KHÔNG HỢP LỆ: $bad tệp không khớp danh sách đã ký. Đừng cài các tệp này." }
Write-Output 'Tất cả tệp có mặt đều khớp danh sách đã ký.'
