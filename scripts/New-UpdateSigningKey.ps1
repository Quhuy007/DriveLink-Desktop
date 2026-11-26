#Requires -Version 7
<#
Tạo khóa ký bản cập nhật DriveLink (ECDSA P-256). Chạy MỘT LẦN trên máy phát hành.
- Khóa riêng được mã hóa DPAPI theo người dùng Windows, lưu ngoài thư mục dự án và KHÔNG bao giờ đưa lên GitHub.
- Khóa công khai được in ra để dán vào Services/UpdateTrust.cs (TrustedPublicKeys); app chỉ cài bản cập nhật ký bằng khóa này.
- -BackupPath: xuất thêm bản sao lưu khóa riêng mã hóa bằng mật khẩu (PKCS#8, AES-256). Hãy tự chạy lệnh này và cất bản sao lưu
  ở nơi an toàn ngoài máy (USB, kho mật khẩu). Mất khóa riêng thì các máy đã cài sẽ không nhận bản cập nhật mới nữa.
#>
param(
    [string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'DriveLink-Signing'),
    [string]$BackupPath = '',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('DriveLink update signing key v1')
$keyPath = Join-Path $KeyDirectory 'update-signing-key.dat'
$publicPath = Join-Path $KeyDirectory 'update-signing-public.txt'

if ((Test-Path -LiteralPath $keyPath) -and -not $Force) {
    if (-not $BackupPath) { throw "Đã có khóa ký tại $keyPath. Dùng -BackupPath để sao lưu, hoặc -Force nếu thật sự muốn thay khóa (các máy đã cài sẽ không nhận bản ký bằng khóa mới)." }
    $pkcs8 = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $entropy, 'CurrentUser')
    $ecdsa = [Security.Cryptography.ECDsa]::Create()
    $ecdsa.ImportPkcs8PrivateKey($pkcs8, [ref]0) | Out-Null
} else {
    New-Item -ItemType Directory -Path $KeyDirectory -Force | Out-Null
    $ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $pkcs8 = $ecdsa.ExportPkcs8PrivateKey()
    [IO.File]::WriteAllBytes($keyPath, [Security.Cryptography.ProtectedData]::Protect($pkcs8, $entropy, 'CurrentUser'))
    $public = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    Set-Content -LiteralPath $publicPath -Value $public -Encoding ascii
    Write-Output "Đã tạo khóa ký: $keyPath"
}
[Array]::Clear($pkcs8, 0, $pkcs8.Length)

if ($BackupPath) {
    $password = Read-Host -AsSecureString 'Mật khẩu cho bản sao lưu khóa (ít nhất 12 ký tự)'
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($password))
    if ($plain.Length -lt 12) { throw 'Mật khẩu sao lưu cần ít nhất 12 ký tự.' }
    $parameters = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
    [IO.File]::WriteAllText($BackupPath, $ecdsa.ExportEncryptedPkcs8PrivateKeyPem($plain, $parameters))
    Write-Output "Đã xuất bản sao lưu khóa riêng (mã hóa bằng mật khẩu): $BackupPath"
}
Write-Output ("public_key: " + [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo()))
$ecdsa.Dispose()
