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
    $scan = Find-Name 'Scan and process folder'
    if ($null -eq $scan) { throw 'Scan button is not exposed to UI Automation' }
    $scan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = (Get-Date).AddSeconds(40)
    $complete = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ($null -ne (Find-Name 'Complete')) { $complete = $true; break }
    }
    if (-not $complete) { throw 'UI did not display completed processing' }
    Write-Host 'WPF UI smoke: folder entered, scan invoked, completed result displayed.'

    # Review tab accessibility: every review control must be reachable by name from UI Automation,
    # which is also what a screen reader uses.
    $reviewTab = Find-Name 'Review section'
    if ($null -eq $reviewTab) { throw 'Review tab is not exposed to UI Automation' }
    $reviewTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 1
    foreach ($name in @('Refresh review groups', 'Keep selected copy', 'Reject selected copy',
            'Mark selected copy for review', 'Quarantine group files', 'Undo last quarantine',
            'Undecided groups only', 'Duplicate groups', 'Copies in the selected group',
            'Load aligned previews', 'Quarantine history', 'Preview position in seconds')) {
        if ($null -eq (Find-Name $name)) { throw "Review control '$name' is missing from UI Automation" }
    }
    $quarantineButton = Find-Name 'Quarantine group files'
    if ($quarantineButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Current.IsEnabled) {
        throw 'Quarantine must stay disabled until an actionable group is selected'
    }
    Write-Host 'WPF UI smoke passed: processing completed and the accessible review controls are reachable.'
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit(20000)) { throw 'Desktop did not shut down cleanly' }
    if ($process.ExitCode -ne 0) { throw "Desktop exit code $($process.ExitCode)" }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
