param([string]$ProfilePath = '', [string]$PreferencesPath = 'C:\Users\Admin\AppData\Local\DriveLink\preferences.dat')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($ProfilePath) {
 $profile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
} else {
 Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
 $clear = [System.Security.Cryptography.ProtectedData]::Unprotect([System.IO.File]::ReadAllBytes($PreferencesPath), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
 try { $profile = ([System.Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json).Cloud }
 finally { [Array]::Clear($clear, 0, $clear.Length) }
}
[Uri]$uri = $null
if (![Uri]::TryCreate([string]$profile.ProjectUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or !$uri.IsDefaultPort -or $uri.Host -notmatch '^[a-zA-Z0-9-]+\.supabase\.co$' -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Chưa có Project URL hợp lệ. Lưu kết nối trong bản 0.5.0 hoặc dùng -ProfilePath với hồ sơ đã xuất.' }
if ($profile.PublishableKey -cnotmatch '^sb_publishable_[A-Za-z0-9_-]{6,498}$') { throw 'Chỉ dùng publishable key. Không nhập secret/service_role.' }
$identity = @{ ProjectUrl = $uri.GetLeftPart([UriPartial]::Authority); PublishableKey = $profile.PublishableKey } | ConvertTo-Json -Compress
$aes = [System.Security.Cryptography.Aes]::Create()
try {
 $aes.GenerateKey(); $aes.GenerateIV()
 $inputBytes = [System.Text.Encoding]::UTF8.GetBytes($identity)
 $transform = $aes.CreateEncryptor()
 try { $cipher = $transform.TransformFinalBlock($inputBytes, 0, $inputBytes.Length) } finally { $transform.Dispose(); [Array]::Clear($inputBytes, 0, $inputBytes.Length) }
 $source = @"
// Generated public cloud identity. Packing is obfuscation, not a security boundary.
namespace DriveLink.Services;
internal static class CloudBuildSettings
{
    internal const string Key = "$([Convert]::ToBase64String($aes.Key))";
    internal const string IV = "$([Convert]::ToBase64String($aes.IV))";
    internal const string Payload = "$([Convert]::ToBase64String($cipher))";
}
"@
 [System.IO.File]::WriteAllText((Join-Path $projectRoot 'Services\CloudBuildSettings.cs'), $source)
 Write-Output 'Đã tạo Cloud mặc định trong mã nguồn; không in URL/key ra nhật ký.'
} finally { $aes.Dispose() }
