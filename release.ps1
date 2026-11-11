<#
  새 버전 배포: 버전 번호를 올리고 태그를 push 하면 GitHub Actions 가 설치 파일을 만들어 Release 로 게시한다.
  설치된 WSL 은 몇 시간 안에 자동으로 새 버전을 받아 적용한다.

  사용법:  .\release.ps1 1.0.1
#>
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "버전은 1.2.3 형식이어야 합니다: $Version" }
if (git tag --list "v$Version") { throw "v$Version 태그가 이미 있습니다." }
if (git status --porcelain) { Write-Host "커밋되지 않은 변경사항도 함께 커밋됩니다." -ForegroundColor Yellow }

$csproj = 'WorkspaceLauncher\WorkspaceLauncher.csproj'
$text = [IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $text, (New-Object Text.UTF8Encoding $false))

git add -A
git commit -m "Release v$Version"
git tag "v$Version"
git push origin HEAD
git push origin "v$Version"

$repo = (git remote get-url origin) -replace '\.git$', ''
Write-Host "`nv$Version 태그를 push 했습니다. 빌드 진행 상황: $repo/actions" -ForegroundColor Green
