# 개발용: 폴더 목록·주소 표시줄 점검(UI Automation). 사진은 지우지 않는다.
# 사용법: navtest.ps1 -Exe <exe> -Folder <시작 폴더(하위 폴더가 있는 시험 폴더의 한 하위 폴더)> -ShotDir <스크린샷 폴더>
param([string]$Exe, [string]$Folder, [string]$ShotDir)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function Find($id) { for ($i = 0; $i -lt 50; $i++) { $e = $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)); if ($e) { return $e }; Start-Sleep -Milliseconds 100 } }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900 }
function Text($id) { (Find $id).Current.Name }
function Crumbs { ((Find 'Crumbs').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name }) -join ' › ' }
function Overlay { $o = Find 'InfoOverlay'; if ($o.Current.IsOffscreen) { '(안내 없음)' } else { Text 'InfoTitle' } }
function TreeItem($name) {
    for ($i = 0; $i -lt 50; $i++) {
        $items = (Find 'FolderTree').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::TreeItem)))
        foreach ($t in $items) { if (($t.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -contains $name) { return $t } }
        Start-Sleep -Milliseconds 200
    }
}
function SelectTree($name) { (TreeItem $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1200 }
function Answer($code) {
    for ($i = 0; $i -lt 30; $i++) {
        $dlg = $win.FindFirst($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))
        if ($dlg) { [W.U]::PostMessage([IntPtr]$dlg.Current.NativeWindowHandle, 0x0111, [IntPtr]$code, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 1200; return }
        Start-Sleep -Milliseconds 200
    }
    'no dialog'
}

$proc = Start-Process $Exe -ArgumentList "`"$Folder`"" -PassThru
for ($i = 0; $i -lt 100 -and -not $win; $i++) { Start-Sleep -Milliseconds 100; $win = $AE::RootElement.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $proc.Id)) }
for ($i = 0; $i -lt 100; $i++) { if ((Find 'SelectionText').Current.Name) { break }; Start-Sleep -Milliseconds 100 }
Start-Sleep -Seconds 2
"시작:        $(Crumbs) | $(Text 'SelectionText')"

# 1) 정리하지 않은 선택을 만든 뒤 폴더 목록의 '사진'을 누르고 '취소' → 원래 폴더 그대로여야 한다
Invoke (Find 'SelectButton')
SelectTree '사진'
Answer 2 | Out-Null
"취소 후:     $(Crumbs) | 목록에서 '사진' 선택됨: $((TreeItem '사진').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected)"

# 2) 다시 '사진'을 누르고 '아니요'(정리하지 않고 이동) → '사진' 폴더로 이동, 바로 안에 사진이 없다는 안내
SelectTree '사진'
Answer 7 | Out-Null
Start-Sleep -Seconds 2
"사진 폴더:   $(Crumbs) | 안내: $(Overlay)"
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\snap.ps1" $proc.Id "$ShotDir\shot-tree.png" | Out-Null

# 3) 시작했던 시험 폴더를 다시 열고, 상위 폴더 버튼
Invoke (Find 'InfoPrimaryButton')
$proc2 = $null
$win2 = $null
$proc.Kill(); Start-Sleep -Milliseconds 500
$proc = Start-Process $Exe -ArgumentList "`"$Folder`"" -PassThru
$win = $null
for ($i = 0; $i -lt 100 -and -not $win; $i++) { Start-Sleep -Milliseconds 100; $win = $AE::RootElement.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $proc.Id)) }
for ($i = 0; $i -lt 100; $i++) { if ((Find 'SelectionText').Current.Name) { break }; Start-Sleep -Milliseconds 100 }
Invoke (Find 'UpButton')
Start-Sleep -Seconds 3
"상위 폴더:   $(Crumbs) | $(Text 'SelectionText') | 안내: $(Overlay)"
"요약: $((Text 'GroupSummaryText') -replace "`n", ' / ')"
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\snap.ps1" $proc.Id "$ShotDir\shot-up.png" | Out-Null
$proc.Kill()
