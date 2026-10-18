# Measures the controller process overhead of the Python vs C# implementation.
#
# Total wall time is bounded by the deliberate realtime pacing, so the metric that
# actually shows the porting gain is CPU time and memory of the *controller* process
# (the FFmpeg children are identical in both runs).
#
# Uses .Arguments (a single string) because Windows PowerShell 5.1 runs on
# .NET Framework, where ProcessStartInfo.ArgumentList does not exist.

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string[]]$ExeArgs,
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$CsvPath = ""
)

function Quote-Arg([string]$a) {
    if ($a -match '[\s"]') { return '"' + ($a -replace '"', '\"') + '"' }
    return $a
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ExePath
$psi.Arguments = (($ExeArgs | ForEach-Object { Quote-Arg $_ }) -join ' ')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = [System.Diagnostics.Process]::Start($psi)
$stdoutTask = $proc.StandardOutput.ReadToEndAsync()
$stderrTask = $proc.StandardError.ReadToEndAsync()

$peakBytes = 0
$wsSum = 0
$samples = 0
while (-not $proc.WaitForExit(100)) {
    try {
        $proc.Refresh()
        if ($proc.PeakWorkingSet64 -gt $peakBytes) { $peakBytes = $proc.PeakWorkingSet64 }
        $wsSum += $proc.WorkingSet64
        $samples++
    } catch { break }
}
$proc.WaitForExit()
$sw.Stop()

$cpu = $proc.TotalProcessorTime.TotalSeconds
$avgWs = if ($samples -gt 0) { $wsSum / $samples } else { 0 }
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()

$fresh = ""
$repeated = ""
$videoKbps = ""
if ($stdout -match 'Fresh frames\s*:\s*(\d+)') { $fresh = $Matches[1] }
if ($stdout -match 'Repeated slots\s*:\s*(\d+)') { $repeated = $Matches[1] }
if ($stdout -match 'Video average\s*:\s*([\d.]+) kbps') { $videoKbps = $Matches[1] }

$row = [PSCustomObject]@{
    Label        = $Label
    WallSeconds  = [math]::Round($sw.Elapsed.TotalSeconds, 3)
    CpuSeconds   = [math]::Round($cpu, 3)
    PeakMB       = [math]::Round($peakBytes / 1MB, 1)
    AvgWorkingMB = [math]::Round($avgWs / 1MB, 1)
    Samples      = $samples
    Fresh        = $fresh
    Repeated     = $repeated
    VideoKbps    = $videoKbps
    ExitCode     = $proc.ExitCode
    StderrTail   = if ($stderr.Length -gt 120) { $stderr.Substring($stderr.Length - 120) } else { $stderr }
}

if ($CsvPath -ne "") {
    $row | Export-Csv -Path $CsvPath -NoTypeInformation -Append -Encoding UTF8
}
$row | Format-List | Out-String
