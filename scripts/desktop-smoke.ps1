param([string]$Executable = 'src/VideoDedup.Launcher/bin/Release/net10.0-windows/VideoDedup.Launcher.exe', [string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path -Parent $PSScriptRoot
$inputDir = Join-Path $root 'TestFixtures/generated/ui-input'
New-Item -ItemType Directory -Force $inputDir | Out-Null
Copy-Item (Join-Path $root 'TestFixtures/generated/sample.mkv') (Join-Path $inputDir 'ui-sample.mkv') -Force
$dataDir = $DataDirectory
if (-not $dataDir) { $dataDir = Join-Path $root ('TestResults/ui-data-' + [Guid]::NewGuid().ToString('N')) }
$process = Start-Process -FilePath (Join-Path $root $Executable) -ArgumentList @('--tools', (Join-Path $root 'tools/media'), '--data', $dataDir) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(40)
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
    if ($null -eq $folder) { throw 'Folder input inaccessible' }
    $pattern = $folder.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($inputDir)
    $scan = Find-Name 'Scan and process'
    $scan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = (Get-Date).AddSeconds(40)
    $complete = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ($null -ne (Find-Name 'Complete')) { $complete = $true; break }
    }
    if (-not $complete) { throw 'UI did not display completed processing' }
    Write-Host 'WPF UI smoke passed: folder entered, scan invoked, completed result displayed.'
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit(20000)) { throw 'Desktop did not shut down cleanly' }
    if ($process.ExitCode -ne 0) { throw "Desktop exit code $($process.ExitCode)" }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
