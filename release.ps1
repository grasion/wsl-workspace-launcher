<#
  WSL 새 버전 배포 (이 PC 에서 빌드 → 설치 패키지 생성 → GitHub Release 업로드)
  설치된 WSL 은 Release 를 감지해 자동으로 업데이트한다.

  사용법:  .\release.ps1 1.0.1
  필요:    dotnet SDK 10, vpk (dotnet tool install -g vpk --version 0.0.1298), gh 로그인 (gh auth login)
#>
param([Parameter(Mandatory = $true)][string]$Version)

# 네이티브 명령(git 등)의 stderr 경고로 중단되지 않도록 Continue — 실패 여부는 종료 코드로 판단
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$RepoUrl = 'https://github.com/grasion/wsl-workspace-launcher'
$Csproj  = 'WorkspaceLauncher\WorkspaceLauncher.csproj'
$Tag     = "v$Version"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "버전은 1.2.3 형식이어야 합니다: $Version" }

function Step($msg) { Write-Host "`n▶ $msg" -ForegroundColor Cyan }
function Run([scriptblock]$cmd) { & $cmd; if ($LASTEXITCODE -ne 0) { throw "실패 (exit $LASTEXITCODE): $cmd" } }

# 1) 버전 반영 + 커밋 + 태그 + push
Step "버전 $Version 반영 / 커밋 / 태그"
$text = [IO.File]::ReadAllText($Csproj)
$new  = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
if ($new -ne $text) { [IO.File]::WriteAllText($Csproj, $new, (New-Object Text.UTF8Encoding $false)) }

git add -A 2>$null
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Run { git commit -q -m "Release $Tag" } }
if (-not (git tag --list $Tag)) { Run { git tag $Tag } }
Run { git push -q origin HEAD }
Run { git push -q origin $Tag }

# 2) 빌드 (self-contained — .NET 미설치 PC 에서도 실행)
Step "빌드"
$work = Join-Path $PSScriptRoot 'Releases'
$pub  = Join-Path $PSScriptRoot 'publish\release'
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue
Run { dotnet publish $Csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $pub -nologo -v q }

# 3) 이전 릴리스 받기(델타 업데이트용) + 패키지 생성
Step "설치 패키지 생성"
$token = (gh auth token).Trim()
if (-not $token) { throw "gh 로그인이 필요합니다: gh auth login" }
vpk download github --repoUrl $RepoUrl --token $token -o $work 2>$null | Out-Null   # 첫 릴리스면 실패해도 무방
Run { vpk pack -u WSL -v $Version -p $pub -e WSL.exe -o $work --packTitle "WSL - Workspace Launcher" --packAuthors grasion -i WorkspaceLauncher\Assets\app.ico }

# 4) GitHub Release 업로드
Step "GitHub Release 게시"
Run { vpk upload github --repoUrl $RepoUrl --token $token -o $work --publish --releaseName "WSL $Tag" --tag $Tag }

Write-Host "`n✅ $Tag 배포 완료: $RepoUrl/releases/tag/$Tag" -ForegroundColor Green
