# Tạo chứng thư số tự ký để ký iTest dùng NỘI BỘ trong trường (không được Windows SmartScreen tin cậy
# trên máy khác cho tới khi chứng thư được cài vào "Trusted Root" và "Trusted Publishers" của các máy phòng thi).
# Để phát hành rộng rãi, hãy mua chứng thư ký mã OV/EV hoặc dùng Azure Trusted Signing.
#   ./installer/new-selfsigned-cert.ps1 -Password "MatKhauManh!"
param([Parameter(Mandatory = $true)][string]$Password, [string]$Name = "iTest - Truong hoc")
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=$Name" -KeyUsage DigitalSignature `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) -HashAlgorithm SHA256
$sec = ConvertTo-SecureString $Password -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath "itest-codesign.pfx" -Password $sec | Out-Null
Export-Certificate -Cert $cert -FilePath "itest-codesign.cer" | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes("itest-codesign.pfx")) | Set-Content "itest-codesign.pfx.base64.txt"
Write-Host "Đã tạo:"
Write-Host "  itest-codesign.pfx              — giữ bí mật, KHÔNG đưa lên GitHub"
Write-Host "  itest-codesign.pfx.base64.txt   — dán vào secret CODESIGN_PFX_BASE64 của repo"
Write-Host "  itest-codesign.cer              — cài vào Trusted Root + Trusted Publishers trên máy phòng thi (GPO hoặc certutil)"
