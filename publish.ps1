#!/usr/bin/env pwsh
# SimplyShare - Release 게시 스크립트

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$csproj = Join-Path $projectRoot 'src\SimplyShare\SimplyShare.csproj'
$outputDir = Join-Path $projectRoot 'publish'

function Bump-ThirdVersionNumber([string]$projectFile) {
    if (-not (Test-Path $projectFile)) {
        throw "csproj not found: $projectFile"
    }

    $content = Get-Content -Path $projectFile -Raw -Encoding UTF8
    $m = [regex]::Match($content, '(?s)(<Version>)([^<]+)(</Version>)')
    if (-not $m.Success) {
        throw "<Version> tag not found in: $projectFile"
    }

    $current = $m.Groups[2].Value.Trim()
    $parts = $current.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 3) {
        throw "Version must have at least 3 parts (x.y.z). Current: '$current'"
    }

    [int]$major = 0
    [int]$minor = 0
    [int]$patch = 0

    if (-not [int]::TryParse($parts[0], [ref]$major)) { throw "Invalid major version: '$($parts[0])'" }
    if (-not [int]::TryParse($parts[1], [ref]$minor)) { throw "Invalid minor version: '$($parts[1])'" }
    if (-not [int]::TryParse($parts[2], [ref]$patch)) { throw "Invalid patch version: '$($parts[2])'" }

    $patch++

    $new = if ($parts.Count -ge 4) {
        # 4파트 버전이라도 '세번째 숫자'를 올리고 나머지는 보존
        "$major.$minor.$patch.$($parts[3])"
    } else {
        "$major.$minor.$patch"
    }

    $updated = [regex]::Replace(
        $content,
        '(?s)(<Version>)([^<]+)(</Version>)',
        "`${1}$new`${3}",
        1)

    Set-Content -Path $projectFile -Value $updated -Encoding UTF8
    return [pscustomobject]@{ Old = $current; New = $new }
}

function Remove-PublishDirectory([string]$dir) {
    if (-not (Test-Path $dir)) {
        return
    }

    Write-Host '기존 publish 폴더 제거...' -ForegroundColor Yellow

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -Path $dir -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $msg = $_.Exception.Message
            Write-Host "publish 폴더 제거 실패(시도 $attempt/3): $msg" -ForegroundColor Yellow

            # publish\SimplyShare.exe가 실행 중이면 해당 프로세스만 종료 후 재시도
            try {
                $procs = Get-CimInstance Win32_Process -Filter "Name='SimplyShare.exe'" -ErrorAction SilentlyContinue
                foreach ($p in $procs) {
                    if ($p.ExecutablePath -and $p.ExecutablePath.StartsWith($dir, [System.StringComparison]::OrdinalIgnoreCase)) {
                        Write-Host "실행 중인 publish 앱 종료: pid=$($p.ProcessId)" -ForegroundColor Yellow
                        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
                    }
                }
            }
            catch {
                # 프로세스 경로 조회 실패 등은 여기서 삼키고, 다음 제거 시도로 판단
            }

            Start-Sleep -Milliseconds 500
        }
    }

    throw "publish 폴더를 제거할 수 없습니다. 실행 중인 SimplyShare를 모두 종료한 뒤 다시 시도해 주세요: $dir"
}

Write-Host '=== SimplyShare Release 게시 ===' -ForegroundColor Cyan

# 기존 publish 폴더 정리 (성공해야만 버전 증가)
Remove-PublishDirectory -dir $outputDir

# 버전 자동 증가 (x.y.z의 z를 +1)
$bumped = Bump-ThirdVersionNumber -projectFile $csproj
Write-Host "버전 업데이트: $($bumped.Old) -> $($bumped.New)" -ForegroundColor Green

# Release 게시
Write-Host '게시 중...' -ForegroundColor Yellow
dotnet publish $csproj -c Release -o $outputDir

if ($LASTEXITCODE -eq 0) {
    $exe = Get-ChildItem -Path $outputDir -Filter '*.exe' | Select-Object -First 1

    Write-Host ''
    Write-Host '게시 완료!' -ForegroundColor Green
    Write-Host "  경로: $outputDir" -ForegroundColor White
    if ($exe) {
        $sizeMB = [math]::Round($exe.Length / 1MB, 1)
        Write-Host "  파일: $($exe.Name) ($sizeMB MB)" -ForegroundColor White
    }
} else {
    Write-Host '게시 실패!' -ForegroundColor Red
    exit 1
}
