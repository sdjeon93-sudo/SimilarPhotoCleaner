# 개발용: 자동 업데이트 점검. 낮은 버전으로 만든 exe를 띄워 알림 → 업데이트 → 새 버전으로 다시 열리는지 본다.
# 사용법: updatetest.ps1 -Exe <낮은 버전 exe 경로>
param([string]$Exe)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function Find($id, $tries = 100) { for ($i = 0; $i -lt $tries; $i++) { $e = $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)); if ($e) { return $e }; Start-Sleep -Milliseconds 100 } }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }

"시작 버전: $((Get-Item $Exe).VersionInfo.ProductVersion)"
$proc = Start-Process $Exe -PassThru
for ($i = 0; $i -lt 100 -and -not $win; $i++) { Start-Sleep -Milliseconds 100; $win = $AE::RootElement.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $proc.Id)) }
"왼쪽 아래 표시: $((Find 'VersionText').Current.Name)"

# 자동 확인(12시간에 한 번)이 이미 지났을 수 있으니 '업데이트 확인'을 직접 누른다 → 알림 줄
Invoke (Find 'CheckUpdateButton')
$banner = $null
# 알림 줄(Border)은 UI 자동화에 나오지 않아서 안의 글자로 확인한다.
for ($i = 0; $i -lt 60; $i++) { $b = Find 'UpdateText' 1; if ($b -and $b.Current.Name -and -not $b.Current.IsOffscreen) { $banner = $b; break }; Start-Sleep -Milliseconds 500 }
if (-not $banner) { "알림이 뜨지 않음"; $proc.Kill(); return }
"알림: $((Find 'UpdateText').Current.Name)"

Invoke (Find 'UpdateNowButton')
"확인 창: $((Find 'InfoTitle').Current.Name)"
Invoke (Find 'InfoPrimaryButton')

# 받기 진행 → 새 버전으로 다시 열림
$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not $proc.HasExited -and $clock.Elapsed.TotalSeconds -lt 180) {
    try { $t = (Find 'UpdateText' 1).Current.Name; if ($t -ne $last) { "  $t"; $last = $t } } catch { }
    Start-Sleep -Milliseconds 700
}
"예전 프로그램 종료: $($proc.HasExited) ($([math]::Round($clock.Elapsed.TotalSeconds))초)"
Start-Sleep -Seconds 4
$new = Get-Process SimilarPhotoCleaner -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }
"새로 열린 프로그램: $(if ($new) { '있음' } else { '없음' })"
"바뀐 exe 버전: $((Get-Item $Exe).VersionInfo.ProductVersion)"
"남은 .old.exe: $(Test-Path ([IO.Path]::ChangeExtension($Exe, '.old.exe')))"
$new | Stop-Process
