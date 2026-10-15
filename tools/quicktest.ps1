# 개발용 UI 점검: '빠르게 정리하기'를 눌러 본다(골라 두기만 → 정리 → 되돌리기 → 바로 정리).
# 사용법: quicktest.ps1 -Exe <exe> -Folder <사진 폴더> -ShotDir <스크린샷 폴더>
# 설정을 '폴더로 옮기기'로 바꿔 시험하고, 끝나면 설정 파일을 되돌린다.
param([string]$Exe, [string]$Folder, [string]$ShotDir)
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
# 경로가 260자를 넘어도 세도록 .NET에 \\?\ 경로를 준다(PowerShell 5.1 명령은 긴 경로에서 틀리게 센다).
function Count { @([IO.Directory]::GetFiles("\\?\$Folder", '*', 'AllDirectories') | Where-Object { $_ -notlike '*\_지운 사진\*' }).Count }
function Moved { $m = "\\?\$Folder\_지운 사진"; if ([IO.Directory]::Exists($m)) { [IO.Directory]::GetFiles($m, '*', 'AllDirectories').Count } else { 0 } }
# 메시지 상자의 본문과 제목을 읽고 버튼 명령을 보낸다(1 = 확인, 6 = 예, 7 = 아니요)
function Answer($code) {
    for ($i = 0; $i -lt 150; $i++) {
        $dlg = $win.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
        if ($dlg) {
            $body = ($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
                ForEach-Object { $_.Current.Name }) -join ' '
            [W.U]::PostMessage([IntPtr]$dlg.Current.NativeWindowHandle, 0x0111, [IntPtr]$code, [IntPtr]::Zero) | Out-Null
            Start-Sleep -Milliseconds 800
            return "$($dlg.Current.Name): $($body -replace "`r?`n", ' / ')"
        }
        Start-Sleep -Milliseconds 200
    }
    throw "no dialog"
}

$settings = Join-Path $env:APPDATA 'SimilarPhotoCleaner\settings.json'
$backup = if (Test-Path $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
$cacheDir = Join-Path $env:LOCALAPPDATA 'SimilarPhotoCleaner\cache'
$cacheBefore = if (Test-Path $cacheDir) { @(Get-ChildItem $cacheDir -File | ForEach-Object Name) } else { @() }
try {
    $s = if ($backup) { [Text.Encoding]::UTF8.GetString($backup).TrimStart([char]0xFEFF) | ConvertFrom-Json } else { [pscustomobject]@{} }
    $s | Add-Member -Force RemoveMode 1
    $s | Add-Member -Force IncludeSubfolders $true
    $s | Add-Member -Force Level 3
    New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
    $s | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8

    $proc = Start-Process $Exe -ArgumentList "`"$Folder`"" -PassThru
    for ($i = 0; $i -lt 300 -and -not $win; $i++) {
        Start-Sleep -Milliseconds 100
        $win = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    }
    for ($i = 0; $i -lt 600; $i++) { if ((Find $win 'SelectionText').Current.Name) { break }; Start-Sleep -Milliseconds 50 }
    "loaded | files=$(Count) | $((Text 'GroupSummaryText') -replace "`n", ' / ')"

    # 1) 골라 두기만: 파일은 그대로, 모든 묶음이 '선택됨'
    Invoke (Find $win 'QuickButton')
    "quick(no) dialog: $(Answer 7)"
    "  -> files=$(Count) moved=$(Moved) | $(Text 'BatchButtonText') | $(Text 'SelectionText')"
    Start-Sleep -Seconds 1
    Snap 'quick-picked'

    # 2) 고른 대로 정리
    Invoke (Find $win 'BatchButton')
    "batch dialog: $(Answer 1)"
    Start-Sleep -Seconds 1
    "  -> files=$(Count) moved=$(Moved) | $(Text 'InfoTitle')"

    # 3) 되돌리기 → 4) 빠르게 정리하기에서 바로 정리
    Invoke (Find $win 'UndoButton')
    "undo -> files=$(Count) moved=$(Moved)"
    Invoke (Find $win 'QuickButton')
    "quick(yes) dialog: $(Answer 6)"
    Start-Sleep -Seconds 1
    "  -> files=$(Count) moved=$(Moved) | $(Text 'InfoTitle') | $((Text 'GroupSummaryText') -replace "`n", ' / ')"
    Snap 'quick-cleaned'

    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 2
    "exited: $($proc.HasExited)"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    if ($backup) { [IO.File]::WriteAllBytes($settings, $backup) } else { Remove-Item -LiteralPath $settings -ErrorAction SilentlyContinue }
    # 시험 폴더의 분석 결과 저장 파일은 지운다(사용자의 다른 저장 파일은 그대로).
    if (Test-Path $cacheDir) { Get-ChildItem $cacheDir -File | Where-Object { $cacheBefore -notcontains $_.Name } | Remove-Item }
}
