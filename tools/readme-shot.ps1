# README용 스크린샷: 앱을 띄워 묶음 두 개를 고르고(하나는 '나중에') 화면을 찍는다. 파일은 지우지 않는다.
param([string]$Exe, [string]$Folder, [string]$Out)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Find($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    for ($i = 0; $i -lt 100; $i++) { $e = $win.FindFirst($TS::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
}
function Invoke($id) { (Find $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700 }

$proc = Start-Process $Exe -ArgumentList "`"$Folder`"" -PassThru
for ($i = 0; $i -lt 100 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 100
    $win = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
}
for ($i = 0; $i -lt 100; $i++) { if ((Find 'SelectionText').Current.Name) { break }; Start-Sleep -Milliseconds 100 }
Invoke 'SelectButton'
Invoke 'DeferButton'
Invoke 'SelectButton'
Start-Sleep -Seconds 2
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\snap.ps1" $proc.Id $Out
$proc.Kill()
