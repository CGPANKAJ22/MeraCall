# MeraPrint: Windows ke WIA se scanner chalata hai (kuch install nahi).
# host.js chalata hai:
#   MP_LIST=1                      -> scanners ki list (JSON)
#   MP_SCANNER, MP_DPI, MP_MODE (color|gray|bw), MP_OUT -> scan karke JPG
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$JPEG = '{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}'
try {
    $dm = New-Object -ComObject WIA.DeviceManager
    if ($env:MP_LIST) {
        $l = @()
        foreach ($d in $dm.DeviceInfos) {
            if ($d.Type -eq 1) { $l += [pscustomobject]@{ id = [string]$d.DeviceID; name = [string]$d.Properties.Item('Name').Value } }
        }
        ConvertTo-Json -InputObject @($l) -Compress
        exit 0
    }
    $di = $null
    foreach ($d in $dm.DeviceInfos) {
        if ($d.Type -eq 1 -and (-not $env:MP_SCANNER -or [string]$d.DeviceID -eq $env:MP_SCANNER)) { $di = $d; break }
    }
    if (-not $di) { Write-Output 'Scanner nahi mila (chalu hai? USB/WiFi juda hai?)'; exit 2 }
    $dev = $di.Connect()
    $item = $dev.Items.Item(1)
    function SetP($id, $v) { try { $item.Properties.Item([string]$id).Value = $v } catch {} }
    $intent = @{ color = 1; gray = 2; bw = 4 }[$env:MP_MODE]
    if ($intent) { SetP 6146 $intent }
    $dpi = [int]$env:MP_DPI
    if ($dpi -gt 0) { SetP 6147 $dpi; SetP 6148 $dpi }
    $img = $item.Transfer($JPEG)
    if ($img.FormatID -ne $JPEG) {
        $ip = New-Object -ComObject WIA.ImageProcess
        $ip.Filters.Add($ip.FilterInfos.Item('Convert').FilterID)
        $ip.Filters.Item(1).Properties.Item('FormatID').Value = $JPEG
        $ip.Filters.Item(1).Properties.Item('Quality').Value = 90
        $img = $ip.Apply($img)
    }
    if (Test-Path $env:MP_OUT) { Remove-Item $env:MP_OUT -Force }
    $img.SaveFile($env:MP_OUT)
    exit 0
} catch {
    Write-Output $_.Exception.Message
    exit 1
}
