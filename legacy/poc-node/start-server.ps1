$ErrorActionPreference = 'Stop'

# 1) IP du même sous-réseau que la passerelle par défaut (le vrai réseau LAN)
$ip = $null
$gw = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
  Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
if ($gw) {
  $octets = ([ipaddress]$gw.NextHop).IPAddressToString.Split('.')
  $prefix = "$($octets[0]).$($octets[1]).$($octets[2])."
  $addr = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like "$prefix*" -and $_.IPAddress -notlike "$prefix0" -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -First 1
  if ($addr) { $ip = $addr.IPAddress }
}

# 2) Repli : n'importe quelle IP privée sur un adaptateur physique (pas vEthernet/VirtualBox/WSL)
if (-not $ip) {
  $nets = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
    $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and
    $_.PrefixOrigin -ne 'WellKnown' -and $_.InterfaceAlias -notlike '*vEthernet*' -and
    $_.InterfaceAlias -notlike '*VirtualBox*' -and $_.InterfaceAlias -notlike '*WSL*'
  }
  foreach ($n in $nets) {
    if ($n.IPAddress -like '192.168.*' -or $n.IPAddress -like '10.*' -or $n.IPAddress -like '172.*') {
      $ip = $n.IPAddress
      break
    }
  }
  if (-not $ip) { $ip = ($nets | Select-Object -First 1).IPAddress }
}

if (-not $ip) {
  Write-Output 'Aucune IP LAN détectée.'
  exit 1
}

Set-Content -Path "$PSScriptRoot\.env" -Value "SONNUAGE_PUBLIC_IP=$ip" -Encoding ascii
Write-Output "IP détectée : $ip (écrite dans .env)"

docker compose up -d --build
if ($LASTEXITCODE -ne 0) { Write-Output 'Échec du lancement.'; exit 1 }

Start-Sleep -Seconds 3
Write-Output '---'
Write-Output "Téléphone -> http://$ip`:3000"
docker ps --filter name=sonnuage --format '{{.Names}}  {{.Status}}  {{.Ports}}'
