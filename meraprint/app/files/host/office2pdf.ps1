# MeraPrint: Word / Excel / PowerPoint file ko PDF banata hai (MS Office se), taaki print ke saare options chalein.
# host.js chalata hai: env MP_IN, MP_OUT, MP_KIND (word|excel|ppt)
$ErrorActionPreference = 'Stop'
$in = $env:MP_IN; $out = $env:MP_OUT; $kind = $env:MP_KIND
$app = $null
try {
    if ($kind -eq 'word') {
        $app = New-Object -ComObject Word.Application
        $app.Visible = $false; $app.DisplayAlerts = 0
        $d = $app.Documents.Open($in, $false, $true)
        $d.ExportAsFixedFormat($out, 17)
        $d.Close(0)
    } elseif ($kind -eq 'excel') {
        $app = New-Object -ComObject Excel.Application
        $app.Visible = $false; $app.DisplayAlerts = $false
        $w = $app.Workbooks.Open($in, 0, $true)
        $w.ExportAsFixedFormat(0, $out)
        $w.Close($false)
    } elseif ($kind -eq 'ppt') {
        $app = New-Object -ComObject PowerPoint.Application
        $p = $app.Presentations.Open($in, -1, 0, 0)
        $p.SaveAs($out, 32)
        $p.Close()
    } else { Write-Output 'kind?'; exit 3 }
    if (Test-Path $out) { exit 0 } else { Write-Output 'PDF nahi bani'; exit 1 }
} catch {
    Write-Output $_.Exception.Message
    exit 1
} finally {
    if ($app) { try { $app.Quit() } catch {} ; try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch {} }
}
