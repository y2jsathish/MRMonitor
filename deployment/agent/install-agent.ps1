<#
.SYNOPSIS  Installs / upgrades the ATM Nexus agent as a Windows service. Run elevated on the ATM (or via the bank's software distribution tool).
.PARAMETER Package   Path to the signed agent zip (output of the release pipeline: publish win-x86 self-contained).
.PARAMETER AtmCode   Terminal ID; used as OAuth client_id.
.PARAMETER CertThumbprint  Thumbprint of the device certificate already present in LocalMachine\My (issued by the bank PKI / SCEP).
#>
param(
  [Parameter(Mandatory)] [string] $Package,
  [Parameter(Mandatory)] [string] $AtmCode,
  [Parameter(Mandatory)] [string] $CertThumbprint,
  [string] $GatewayBaseUrl = "https://nexus-gw.example.bank",
  [string] $TokenEndpoint  = "https://nexus-idp.example.bank/connect/token",
  [string] $CommandKeyId, [string] $CommandPublicKeySpkiBase64,
  [string] $AtmAppService, [string] $XfsService,
  [string] $InstallDir = "C:\Program Files\ATMNexus\Agent",
  [string] $DataDir    = "C:\ProgramData\ATMNexus"
)
$ErrorActionPreference = "Stop"
$svc = "ATMNexusAgent"

# 1. verify the package signature BEFORE touching the machine
$sig = Get-AuthenticodeSignature -FilePath (Join-Path (Split-Path $Package) "AtmNexus.Agent.exe") -ErrorAction SilentlyContinue
if ($Package -like "*.zip") {
  $tmp = Join-Path $env:TEMP "nexus-agent-$([guid]::NewGuid().ToString('N'))"
  Expand-Archive -Path $Package -DestinationPath $tmp
  $sig = Get-AuthenticodeSignature -FilePath (Join-Path $tmp "AtmNexus.Agent.exe")
  if ($sig.Status -ne "Valid") { throw "Agent binary signature is $($sig.Status); refusing to install." }
  $src = $tmp
} else { throw "Package must be a .zip" }

# 2. device certificate must exist and be valid
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $CertThumbprint
if (-not $cert -or $cert.NotAfter -lt (Get-Date).AddDays(7)) { throw "Device certificate $CertThumbprint missing or expiring within 7 days." }

# 3. stop old service, deploy files
if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force; (Get-Service $svc).WaitForStatus("Stopped","00:01:00") }
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir, "$DataDir\logs" | Out-Null
Copy-Item "$src\*" $InstallDir -Recurse -Force

# 4. per-ATM configuration (no secrets: the identity is the device certificate)
$local = @{
  Agent    = @{ AtmCode = $AtmCode; GatewayBaseUrl = $GatewayBaseUrl }
  Security = @{ ClientCertThumbprint = $CertThumbprint; TokenEndpoint = $TokenEndpoint }
  Commands = @{ AtmCode = $AtmCode; AtmApplicationService = $AtmAppService; XfsServiceName = $XfsService }
}
if ($CommandKeyId) { $local.Commands.TrustedKeys = @{ $CommandKeyId = $CommandPublicKeySpkiBase64 } }
$local | ConvertTo-Json -Depth 6 | Set-Content -Path "$DataDir\agent.local.json" -Encoding UTF8

# 5. lock down ACLs: only SYSTEM and Administrators may modify the agent or its config
icacls $InstallDir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" | Out-Null
icacls $DataDir    /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null

# 6. register service: automatic (delayed) start, restart on failure, least-privilege virtual account
if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
  New-Service -Name $svc -BinaryPathName "`"$InstallDir\AtmNexus.Agent.exe`"" -DisplayName "ATM Nexus Agent" -StartupType Automatic `
              -Description "Monitoring and management agent" | Out-Null
}
sc.exe config $svc start= delayed-auto | Out-Null
sc.exe failure $svc reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe sidtype $svc unrestricted | Out-Null
Start-Service $svc
Write-Host "ATM Nexus Agent installed: $((Get-Item "$InstallDir\AtmNexus.Agent.exe").VersionInfo.ProductVersion)"
