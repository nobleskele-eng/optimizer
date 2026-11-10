# verify.ps1 - checks that an MP4 carries the Quality Optimizer fingerprints
# and that its video was not degraded. Uses the ffmpeg/ffprobe sitting next to
# this script (falls back to PATH). Run via verify.bat, or:
#   powershell -ExecutionPolicy Bypass -File verify.ps1 "yourfile.mp4"
param([string]$File)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
function Tool($n) { $p = Join-Path $root ($n + '.exe'); if (Test-Path $p) { return $p } return $n }
$ffprobe = Tool 'ffprobe'
$ffmpeg  = Tool 'ffmpeg'

function Run($exe, $argStr) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $argStr
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    # Drain stderr concurrently (async) so a full pipe can't deadlock the stdout
    # read - ffmpeg can emit megabytes of error text for the junk track.
    $errTask = $p.StandardError.ReadToEndAsync()
    $o = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    $e = $errTask.Result
    return [pscustomobject]@{ Out = $o; Err = $e }
}
function NonEmptyLines($s) { if (-not $s) { return 0 } return (($s -split "`r?`n") | Where-Object { $_.Trim() -ne '' }).Count }

if (-not $File -or -not (Test-Path $File)) {
    Write-Host "File not found. Drag an MP4 onto verify.bat, or: verify.bat yourfile.mp4" -ForegroundColor Yellow
    exit 2
}
$File = (Resolve-Path $File).Path
Write-Host ""
Write-Host ("Checking: " + $File) -ForegroundColor Cyan
Write-Host ("-" * 60)

$q = "`"$File`""
$results = @()
function Check($name, $pass, $detail) {
    $script:results += [pscustomobject]@{ Name = $name; Pass = $pass; Detail = $detail }
    $tag = if ($pass) { "[PASS]" } else { "[WARN]" }
    $col = if ($pass) { "Green" } else { "Red" }
    Write-Host ("{0,-6} {1,-34} {2}" -f $tag, $name, $detail) -ForegroundColor $col
}

# 1. stream layout: 1 video + 2 audio
$types = (Run $ffprobe "-v error -show_entries stream=codec_type -of csv=p=0 $q").Out
$nv = (($types -split "`r?`n") | Where-Object { $_ -match 'video' }).Count
$na = (($types -split "`r?`n") | Where-Object { $_ -match 'audio' }).Count
Check "Stream layout" (($nv -eq 1) -and ($na -eq 2)) ("$nv video, $na audio (want 1 video + 2 audio)")

# 2. junk track has 10x the real audio's packets
$ra = [int]((Run $ffprobe "-v error -select_streams a:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 $q").Out.Trim())
$ja = [int]((Run $ffprobe "-v error -select_streams a:1 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 $q").Out.Trim())
Check "Junk track = 10x real audio" (($ra -gt 0) -and ($ja -eq 10 * $ra)) ("real a:0 = $ra packets, junk a:1 = $ja packets")

# 3. junk track is undecodable (that is the point)
$je = 0
if ($ja -gt 0) { $je = NonEmptyLines (Run $ffmpeg "-nostdin -v error -i $q -map 0:a:1 -f null -").Err }
Check "Junk track breaks decoders" (($ja -gt 0) -and ($je -gt 0)) ("$je decode-error lines on the junk track (expected: many)")

# 4. video decodes cleanly (not degraded by this tool)
$veErr = (Run $ffmpeg "-nostdin -v error -i $q -map 0:v:0 -f null -").Err
$ve = NonEmptyLines $veErr
Check "Video decodes clean" ($ve -eq 0) ("$ve video decode errors (expected: 0)")

# 5. timestamps zeroed (no creation_time anywhere)
$ct = (Run $ffprobe "-v error -show_entries format_tags=creation_time:stream_tags=creation_time -of default=nw=1 $q").Out.Trim()
$ctDetail = if ($ct -eq '') { "no creation_time tags" } else { "found: $ct" }
Check "Timestamps zeroed" ($ct -eq '') $ctDetail

# 6. trailing bytes are the repeated 00 00 00 04 00 00 00 00 pattern (a real
#    RTXFury output ends with an empty 8-byte free box after it; look past that)
$ok6 = $false; $hex = ''
try {
    $fs = [System.IO.File]::OpenRead($File)
    try {
        $len = $fs.Length
        if ($len -ge 8) {
            $t8 = New-Object byte[] 8; [void]$fs.Seek($len - 8, 'Begin'); [void]$fs.Read($t8, 0, 8)
            if ((($t8[0..3]) -join ',') -eq '0,0,0,8' -and [System.Text.Encoding]::ASCII.GetString($t8, 4, 4) -eq 'free') { $len -= 8 }
        }
        $n = [Math]::Min(64, $len)
        [void]$fs.Seek($len - $n, 'Begin')
        $buf = New-Object byte[] $n; [void]$fs.Read($buf, 0, $n)
    } finally { $fs.Close() }
    $pat = @(0, 0, 0, 4, 0, 0, 0, 0); $ok6 = $true
    for ($i = 0; $i -lt $n; $i++) { if ($buf[$i] -ne $pat[$i % 8]) { $ok6 = $false; break } }
    $hex = (($buf[0..7]) | ForEach-Object { '{0:x2}' -f $_ }) -join ' '
} catch { $ok6 = $false }
Check "Trailing junk bytes" $ok6 ("last bytes: $hex ...")

# 7. top-level layout matches the reference: ftyp, moov, mdat, junk
$ok7 = $false; $order = ''; $moovAt = [long]-1; $moovLen = [long]0
try {
    $fs = [System.IO.File]::OpenRead($File)
    try {
        $types = @(); $o = [long]0; $len = $fs.Length; $h = New-Object byte[] 16
        while ($o + 8 -le $len) {
            [void]$fs.Seek($o, 'Begin'); [void]$fs.Read($h, 0, 16)
            $size = ([long]$h[0] -shl 24) -bor ([long]$h[1] -shl 16) -bor ([long]$h[2] -shl 8) -bor [long]$h[3]
            if ($size -eq 1) {
                $size = [long]0
                for ($i = 8; $i -lt 16; $i++) { $size = ($size -shl 8) -bor [long]$h[$i] }
            } elseif ($size -eq 0) { $size = $len - $o }
            if (($size -lt 8) -or ($o + $size -gt $len)) { $types += 'junk'; break }
            $t = [System.Text.Encoding]::ASCII.GetString($h, 4, 4)
            $types += $t
            if (($t -eq 'moov') -and ($moovAt -lt 0)) { $moovAt = $o; $moovLen = $size }
            $o += $size
        }
    } finally { $fs.Close() }
    $order = $types -join ', '
    $ok7 = ($order -eq 'ftyp, moov, mdat, junk')
} catch { $ok7 = $false }
Check "Box layout" $ok7 ("$order (want ftyp, moov, mdat, junk)")

# 8. movie header matches a real RTXFury output: mvhd version 1 with unknown
#    duration, and no movie-level metadata (udta)
$ok8 = $false; $mh = 'no moov box'
try {
    if (($moovAt -ge 0) -and ($moovLen -lt 64MB)) {
        $fs = [System.IO.File]::OpenRead($File)
        try { $m = New-Object byte[] $moovLen; [void]$fs.Seek($moovAt, 'Begin'); [void]$fs.Read($m, 0, $moovLen) } finally { $fs.Close() }
        $ver = -1; $unknown = $false; $udta = $false; $o = 8
        while ($o + 8 -le $m.Length) {
            $size = ([long]$m[$o] -shl 24) -bor ([long]$m[$o+1] -shl 16) -bor ([long]$m[$o+2] -shl 8) -bor [long]$m[$o+3]
            $t = [System.Text.Encoding]::ASCII.GetString($m, $o + 4, 4)
            if (($size -lt 8) -or ($o + $size -gt $m.Length)) { break }
            if ($t -eq 'mvhd') {
                $ver = $m[$o + 8]
                $unknown = ($ver -eq 1) -and (@($m[($o + 32)..($o + 39)] | Where-Object { $_ -ne 255 }).Count -eq 0)
            }
            if ($t -eq 'udta') { $udta = $true }
            $o += $size
        }
        $ok8 = $unknown -and (-not $udta)
        $mh = "mvhd v$ver" + $(if ($unknown) { ', duration unknown' } else { ', duration set' }) + $(if ($udta) { ', has movie metadata' } else { ', no movie metadata' })
    }
} catch { $ok8 = $false }
Check "Movie header" $ok8 $mh

Write-Host ("-" * 60)
$passCount = ($results | Where-Object { $_.Pass }).Count
$total = $results.Count
if ($passCount -eq $total) {
    Write-Host ("VERDICT: looks correct - all $total checks passed.") -ForegroundColor Green
    Write-Host "This file has the quality method applied and the video is intact."
} else {
    Write-Host ("VERDICT: $passCount of $total checks passed.") -ForegroundColor Yellow
    Write-Host "A plain/un-optimized file will fail several of these - that is expected."
}
Write-Host ""
Write-Host "Note: the final real-world test is to upload it and confirm the platform"
Write-Host "serves it at original quality. That depends on the platform, not the file."
Write-Host ""
