# Ký số Authenticode cho các file .exe bằng chứng thư số trong biến môi trường
#   CODESIGN_PFX_BASE64 (nội dung file .pfx mã hoá base64) và CODESIGN_PASSWORD.
# Dùng trong GitHub Actions hoặc chạy tay trên Windows:
#   $env:CODESIGN_PFX_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))
#   $env:CODESIGN_PASSWORD = "..."
#   ./installer/sign.ps1 -Files dist\iTest-Setup.exe
param([Parameter(Mandatory = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'

if (-not $env:CODESIGN_PFX_BASE64) { Write-Host "Không có chứng thư số — bỏ qua bước ký."; exit 0 }
$tmpDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$pfx = Join-Path $tmpDir ("codesign_" + [guid]::NewGuid().ToString("N") + ".pfx")
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CODESIGN_PFX_BASE64))

try {
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $signtool) { throw "Không tìm thấy signtool.exe (cần Windows SDK)." }

  $servers = @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com', 'http://timestamp.globalsign.com/tsa/r6advanced1')
  foreach ($f in $Files) {
    $ok = $false
    foreach ($ts in $servers) {
      & $signtool.FullName sign /f $pfx /p $env:CODESIGN_PASSWORD /fd SHA256 /tr $ts /td SHA256 /d "iTest" $f
      if ($LASTEXITCODE -eq 0) { $ok = $true; break }
      Write-Host "Máy chủ dấu thời gian $ts lỗi, thử máy chủ khác…"
      Start-Sleep -Seconds 3
    }
    if (-not $ok) { throw "Ký số thất bại: $f" }
    Write-Host "Đã ký: $f"
  }
}
finally { Remove-Item $pfx -Force -ErrorAction SilentlyContinue }
