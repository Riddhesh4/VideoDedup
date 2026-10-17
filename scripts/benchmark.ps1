param(
    [int]$Files = 12,
    [string]$Label = 'local',
    [string]$Executable = 'src/VideoDedup.Launcher/bin/Release/net10.0-windows/VideoDedup.Launcher.exe',
    [int]$TimeoutSeconds = 1800)
# Measures the real product interface: the published Release app, driven through UI Automation, with the queue
# drained through the same database the workers use. Nothing here simulates the pipeline.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$inputDir = Join-Path $root 'TestResults/bench-input'
$data = Join-Path $root 'TestResults/bench-data'
$results = Join-Path $root 'TestResults/benchmarks'
Remove-Item $inputDir, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $inputDir, $data, $results | Out-Null

# Deterministic fixture set: identical copies of one generated clip (exact duplicates) plus a re-encoded pair,
# which is what the matching stage has to separate from unrelated content.
1..$Files | ForEach-Object { Copy-Item 'TestFixtures/generated/sample.mkv' (Join-Path $inputDir ('bench-{0:D3}.mkv' -f $_)) }
Copy-Item 'TestFixtures/generated/visual-resized.mp4' (Join-Path $inputDir 'bench-resized.mp4')
Copy-Item 'TestFixtures/generated/visual-unrelated.mkv' (Join-Path $inputDir 'bench-unrelated.mkv')
$fileCount = (Get-ChildItem $inputDir -File).Count
$bytes = (Get-ChildItem $inputDir -File | Measure-Object -Property Length -Sum).Sum

$database = Join-Path $data 'videodedup.db'
$process = Start-Process -FilePath (Join-Path $root $Executable) -PassThru -ArgumentList @(
    '--tools', (Join-Path $root 'tools/media'),
    '--audio-tools', (Join-Path $root 'tools/audio'),
    '--data', $data)
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$peakAppBytes = 0L
$peakChildBytes = 0L
try {
    $deadline = (Get-Date).AddSeconds(60)
    $window = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        if ($process.HasExited) { throw "Desktop exited: $($process.ExitCode)" }
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $window) { break }
    }
    if ($null -eq $window) { throw 'WPF window did not appear' }

    function Find-Name($name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    $editCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $folder = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    $folder.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($inputDir)
    $scan = Find-Name 'Scan and process folder'
    if ($null -eq $scan) { throw 'Scan button is not exposed to UI Automation' }
    $scan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # Poll the index until the queue is drained, sampling memory and CPU of the app and its child processes.
    $poll = Start-Process -FilePath 'python' -ArgumentList @(
        (Join-Path $root 'scripts/wait-for-queue-drain.py'), $database, '--timeout', $TimeoutSeconds,
        '--idle-exit', '5') -PassThru -RedirectStandardOutput (Join-Path $results 'drain.json')
    while (-not $poll.HasExited) {
        Start-Sleep -Seconds 2
        $process.Refresh()
        if (-not $process.HasExited -and $process.PeakWorkingSet64 -gt $peakAppBytes) { $peakAppBytes = $process.PeakWorkingSet64 }
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction SilentlyContinue)
        foreach ($child in $children) {
            $childProcess = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $childProcess -and $childProcess.WorkingSet64 -gt $peakChildBytes) { $peakChildBytes = $childProcess.WorkingSet64 }
        }
    }
    $watch.Stop()
    $process.Refresh()
    # The poller's own report is the result; its process exit code is not reliable enough to gate a measurement.
    $drain = Get-Content (Join-Path $results 'drain.json') -Raw | ConvertFrom-Json
    $drained = $null -ne $drain -and $drain.queued -eq 0 -and $drain.leased -eq 0 -and $drain.completed -gt 0 -and
        -not ($drain.PSObject.Properties.Name -contains 'timedOut')
    if (-not $drained) { throw "The queue did not drain within $TimeoutSeconds seconds: $($drain | ConvertTo-Json -Compress)" }
    $cpuSeconds = 0
    try { $cpuSeconds = $process.TotalProcessorTime.TotalSeconds } catch { Write-Warning "CPU time unavailable: $($_.Exception.Message)" }
    $logBytes = (Get-ChildItem (Join-Path $data 'logs') -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    $quarantineBytes = (Get-ChildItem (Join-Path $data 'Quarantine') -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum

    $summary = [ordered]@{
        label               = $Label
        recordedUtc         = (Get-Date).ToUniversalTime().ToString('o')
        app                 = $Executable
        fixtureFiles        = $fileCount
        fixtureBytes        = [long]$bytes
        fixtureNote         = "$Files identical copies of sample.mkv plus one resized re-encode and one unrelated clip"
        tools               = 'pinned LGPL FFmpeg/FFprobe and acoustid fpcalc'
        wallClockSeconds    = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        wallClockNote       = 'process start to a drained queue, including UI Automation driving and a 5 second idle confirmation window'
        queueDrainSeconds   = $drain.elapsedSeconds
        queueDrainNote      = 'scan click to a drained queue, measured by the poller against the index'
        jobsCompleted       = $drain.completed
        jobsDeadLetter      = $drain.deadLetter
        jobsPerMinute       = [math]::Round(($drain.completed / [math]::Max($watch.Elapsed.TotalSeconds, 0.001)) * 60, 1)
        filesWithIndexRows  = $drain.filesIndexOfRecordRows
        matchRows           = $drain.matchRows
        averageCpuCores     = [math]::Round(($cpuSeconds / [math]::Max($watch.Elapsed.TotalSeconds, 0.001)), 2)
        peakAppMemoryMiB    = [math]::Round($peakAppBytes / 1MB, 1)
        peakLargestChildMemoryMiB = [math]::Round($peakChildBytes / 1MB, 1)
        logBytes            = [long]$logBytes
        quarantineBytes     = [long]$quarantineBytes
        jobsByTypeAndState  = $drain.byTypeAndState
    }
    $path = Join-Path $results ("benchmark-$Label-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    $summary | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding utf8
    Write-Host "Benchmark recorded: $path"
    $summary | ConvertTo-Json -Depth 5
} catch {
    Write-Host "Benchmark failed: $($_.Exception.Message)"
    throw
} finally {
    # Tearing the window down is best effort: a measurement already recorded must never be lost to a UIA hiccup.
    try { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    if ($null -ne $process) {
        try { [void]$process.WaitForExit(30000) } catch { }
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    }
}
