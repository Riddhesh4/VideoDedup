param([string]$Executable = 'src/VideoDedup.Launcher/bin/Release/net10.0-windows/VideoDedup.Launcher.exe', [string]$DataDirectory = '',
    # Empty means "pass no tool arguments", which is how a tester starts the packaged build: the launcher then uses
    # <application folder>\media and <application folder>\audio.
    [string]$ToolsDirectory = 'tools/media')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path -Parent $PSScriptRoot
$inputDir = Join-Path $root 'TestFixtures/generated/ui-input'
New-Item -ItemType Directory -Force $inputDir | Out-Null
Copy-Item (Join-Path $root 'TestFixtures/generated/sample.mkv') (Join-Path $inputDir 'ui-sample.mkv') -Force
$dataDir = $DataDirectory
if (-not $dataDir) { $dataDir = Join-Path $root ('TestResults/ui-data-' + [Guid]::NewGuid().ToString('N')) }
# An absolute path lets the same check run against an extracted release package, not just the checkout build.
# IsPathRooted is used instead of IsPathFullyQualified because this script also runs on Windows PowerShell 5.1.
$executablePath = if ([System.IO.Path]::IsPathRooted($Executable)) { $Executable } else { Join-Path $root $Executable }
$arguments = @('--data', $dataDir)
if ($ToolsDirectory) { $arguments = @('--tools', (Join-Path $root $ToolsDirectory)) + $arguments }
$process = Start-Process -FilePath $executablePath -ArgumentList $arguments -PassThru
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

    # The worker grid binds Application-level properties by name ({Binding Name}); if obfuscation renamed them the
    # columns would render empty, so their presence proves XAML bindings still resolve in this build.
    foreach ($worker in @('Probe', 'Hash', 'Visual', 'Audio', 'Match')) {
        if ($null -eq (Find-Name $worker)) { throw "Bound worker '$worker' is missing from the worker grid" }
    }

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
    # Privacy and data panel: the released build must expose the local-storage summary and the guarded index actions,
    # which is also the interface an end user tests when checking where their data lives.
    $privacyTab = Find-Name 'Privacy and data section'
    if ($null -eq $privacyTab) { throw 'Privacy and data tab is not exposed to UI Automation' }
    $privacyTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 1
    foreach ($name in @('Privacy notice', 'Application storage usage', 'Application data folder', 'Refresh storage usage',
            'Index export file path', 'Include file names and paths', 'Export index', 'Delete index data')) {
        if ($null -eq (Find-Name $name)) { throw "Privacy control '$name' is missing from UI Automation" }
    }
    if ($null -eq (Find-Name 'Privacy notice')) { throw 'Privacy notice is missing' }
    Write-Host 'WPF UI smoke passed: processing completed, review controls are reachable, and the privacy panel is present.'
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit(20000)) { throw 'Desktop did not shut down cleanly' }
    if ($process.ExitCode -ne 0) { throw "Desktop exit code $($process.ExitCode)" }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
