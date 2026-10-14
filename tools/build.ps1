# 배포용 실행 파일을 만든다: 이 컴퓨터의 로컬 폴더에서 빌드한 뒤, 완성된 exe만 프로젝트 폴더로 복사한다.
# (구글 드라이브 폴더에 직접 빌드하면 동기화와 얽혀 파일이 깨질 수 있다)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $env:LOCALAPPDATA 'SimilarPhotoCleaner-build\publish'
$exe = Join-Path $root 'SimilarPhotoCleaner.exe'
$old = Join-Path $root 'SimilarPhotoCleaner.old.exe'

dotnet publish (Join-Path $root 'SimilarPhotoCleaner') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

# 지난번에 실행 중이라 비켜 둔 예전 exe가 있으면 지운다(아직 실행 중이면 그대로 둔다).
if (Test-Path $old) { try { Remove-Item $old -Force } catch { } }

$new = Join-Path $out 'SimilarPhotoCleaner.exe'
# 구글 드라이브가 동기화하느라 잠깐 파일을 잡고 있을 수 있어서 몇 번 다시 시도한다.
$copied = $false
for ($i = 0; $i -lt 5 -and -not $copied; $i++) {
    try { Copy-Item $new $exe -Force -ErrorAction Stop; $copied = $true } catch { Start-Sleep -Seconds 2 }
}
if (-not $copied) {
    # 프로그램이 실행 중이면 덮어쓸 수 없다. 실행 중인 파일도 이름은 바꿀 수 있으므로 비켜 두고 새 파일을 넣는다.
    if (-not (Test-Path $exe)) { throw "exe를 복사하지 못했습니다. 잠시 뒤 다시 실행하세요." }
    if (Test-Path $old) { throw "SimilarPhotoCleaner.exe와 .old.exe가 모두 사용 중입니다. 프로그램을 닫고 다시 실행하세요." }
    Rename-Item $exe (Split-Path $old -Leaf)
    Copy-Item $new $exe
    "실행 중인 프로그램이 있어 예전 exe를 SimilarPhotoCleaner.old.exe로 바꿔 두었습니다. 프로그램을 닫은 뒤 지워도 됩니다."
}
if ((Get-FileHash $new).Hash -ne (Get-FileHash $exe).Hash) { throw "복사한 파일이 원본과 다릅니다" }
"완료: $exe ($((Get-Item $exe).VersionInfo.ProductVersion))"
