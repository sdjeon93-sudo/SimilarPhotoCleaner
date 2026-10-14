# 개발용 UI 자동 점검: 앱을 띄우고 버튼을 UI Automation으로 눌러 본다(키보드/마우스 포커스를 빼앗지 않음).
# 사용법: uitest.ps1 -Exe <exe> -Folder <사진 폴더> -ShotDir <스크린샷 폴더> [-LoadOnly]
param([string]$Exe, [string]$Folder, [string]$ShotDir, [switch]$LoadOnly, [int]$Cores = 0)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
$ErrorActionPreference = 'Stop'
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Find($root, $id, $tries = 50) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    for ($i = 0; $i -lt $tries; $i++) { $e = $root.FindFirst($TS::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
    throw "not found: $id"
}
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700 }
function Text($id) { (Find $win $id).Current.Name }
function Snap($name) { powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\snap.ps1" $proc.Id "$ShotDir\$name.png" | Out-Null }
function Count { (Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.FullName -notlike '*\_지운 사진\*' }).Count }
function Moved { $m = Join-Path $Folder '_지운 사진'; if (Test-Path -LiteralPath $m) { (Get-ChildItem -LiteralPath $m -Recurse -File).Count } else { 0 } }
# 앱이 띄운 메시지 상자에 버튼 명령을 보낸다(1 = 확인/예가 아닌 IDOK, 6 = 예, 7 = 아니요)
function Answer($code) {
    for ($i = 0; $i -lt 30; $i++) {
        $dlg = $win.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
        if ($dlg) { [W.U]::PostMessage([IntPtr]$dlg.Current.NativeWindowHandle, 0x0111, [IntPtr]$code, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 800; return $dlg.Current.Name }
        Start-Sleep -Milliseconds 200
    }
    throw "no dialog"
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process $Exe -ArgumentList "`"$Folder`"" -PassThru
if ($Cores -gt 0) { $proc.ProcessorAffinity = [IntPtr]((1 -shl $Cores) - 1) }  # 느린 PC 흉내
for ($i = 0; $i -lt 300 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 100
    $win = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
}
for ($i = 0; $i -lt 600; $i++) { if ((Find $win 'SelectionText').Current.Name) { break }; Start-Sleep -Milliseconds 50 }
"loaded in $([math]::Round($clock.Elapsed.TotalSeconds, 1)) s | $(Text 'SelectionText')"
if ($LoadOnly) { Start-Sleep -Seconds 2; $proc.Kill(); return }
Start-Sleep -Seconds 2
Snap 'shot-load'
"summary: $((Text 'GroupSummaryText') -replace "`n", ' / ')"

# 여러 장 남기기: 첫 묶음에서 2, 3번째 사진의 ✓ 단추를 누른다
$toggles = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'KeepToggle')))
Invoke $toggles[1]; Invoke $toggles[2]
"multi   -> $(Text 'SelectionText')"
Invoke $toggles[2]
"unkeep  -> $(Text 'SelectionText')"
Invoke $toggles[2]
Snap 'shot-multi'

Invoke (Find $win 'SelectButton');  "select  -> $(Text 'SelectionText')"
Invoke (Find $win 'DeferButton');   "defer   -> $(Text 'SelectionText')"
Invoke (Find $win 'SelectButton');  "select  -> $(Text 'SelectionText')"
"batch: $(Text 'BatchText')"
Snap 'shot-selected'

Invoke (Find $win 'BatchButton')
"dialog: $(Answer 1)"
Start-Sleep -Seconds 1
"after clean: files=$(Count) moved=$(Moved) | $(Text 'InfoTitle') | $((Text 'InfoBody') -replace "`n", ' / ')"
Snap 'shot-cleaned'

Invoke (Find $win 'UndoButton')
"after undo: files=$(Count) moved=$(Moved) | $((Text 'GroupSummaryText') -replace "`n", ' / ')"

# 닫기: 정리하지 않은 '선택됨' 묶음이 있으니 물어본다 → '아니요'(정리하지 않고 닫기)
$proc.CloseMainWindow() | Out-Null
"close dialog: $(Answer 7)"
Start-Sleep -Seconds 1
"exited: $($proc.HasExited) | files=$(Count) moved=$(Moved)"
if (-not $proc.HasExited) { $proc.Kill() }
