using System.Diagnostics;
using System.IO;
using System.Text;

namespace SimplyShare.Core;

/// <summary>
/// 자동 업데이트 유틸리티 — EXE 교체 후 재시작
/// </summary>
public static class AutoUpdater
{
    private static readonly string UpdateStatusPath = Path.Combine(Path.GetTempPath(), "SimplyShare_update_status.txt");

    /// <summary>마지막 업데이트 상태를 읽고 파일을 삭제</summary>
    public static string? ConsumeLastUpdateStatus()
    {
        try
        {
            if (!File.Exists(UpdateStatusPath))
                return null;

            var bytes = File.ReadAllBytes(UpdateStatusPath);
            File.Delete(UpdateStatusPath);

            if (bytes.Length == 0)
                return null;

            string text;
            // BOM 기반 판별
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                text = Encoding.Unicode.GetString(bytes);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                text = Encoding.BigEndianUnicode.GetString(bytes);
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = Encoding.UTF8.GetString(bytes);
            }
            else
            {
                // UTF-8 우선, 실패/깨짐이면 시스템 기본 인코딩으로 재시도
                text = Encoding.UTF8.GetString(bytes);
                if (text.Contains('\uFFFD') || text.Contains('?'))
                {
                    text = Encoding.Default.GetString(bytes);
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                return null;

            var code = text.Trim();

            // 상태 파일에 BOM/제로폭 문자 등이 섞여 들어오는 경우가 있어 명시적으로 제거
            code = code.Trim('\uFEFF', '\u200B', '\0');

            // 성공 상태는 사용자에게 표시하지 않는다.
            if (code.Contains("RESTART_CALLED", StringComparison.OrdinalIgnoreCase))
                return null;

            // 구버전(한글 상태) 정규화
            if (code.Contains("재시작") && code.Contains("실행"))
                code = "RESTARTING";
            if (code.Contains("앱") && (code.Contains("종료") || code.Contains("대기")))
                code = "WAITING_FOR_EXIT";
            if (code.Contains("파일") && code.Contains("교체") && code.Contains("실패"))
                code = "REPLACE_FAILED";

            return code switch
            {
                "UPDATER_SCRIPT_PREP" => "업데이트: 스크립트 시작 준비 중",
                "UPDATER_SCRIPT_STARTED" => "업데이트: 스크립트 시작됨",
                "WAITING_FOR_EXIT" => "업데이트: 앱 종료 대기 중",
                "RESTARTING" => "업데이트: 재시작 실행 중",
                "RESTART_CALLED" => null,
                "RESTART_FAILED" => "업데이트 실패: 재시작 실행 실패 (로그 확인)",
                "REPLACE_FAILED" => "업데이트 실패: 파일 교체 실패 (로그 확인)",
                "FATAL_SCRIPT_ERROR" => "업데이트 실패: 스크립트 치명 오류",
                "SCRIPT_START_FAILED" => "업데이트 실패: 스크립트 시작 실패",
                _ => code
            };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStatus(string message)
    {
        try
        {
            File.WriteAllText(UpdateStatusPath, message, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 다운로드된 새 EXE로 업데이트 실행  
    /// 1. PowerShell 스크립트를 생성하여 현재 프로세스 종료 대기  
    /// 2. 기존 EXE를 .old로 백업  
    /// 3. 새 EXE를 원래 위치로 이동  
    /// 4. 앱 재시작  
    /// 5. 스크립트 및 .old 파일 정리
    /// </summary>
    public static bool ApplyUpdate(string newExePath)
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (currentExe is null || !File.Exists(newExePath))
                return false;

            var backupPath = currentExe + ".old";
            var pid = Environment.ProcessId;

            // PowerShell 업데이트 스크립트 생성
            var scriptPath = Path.Combine(Path.GetTempPath(), $"SimplyShare_update_{Guid.NewGuid():N}.ps1");
            var logPath = Path.Combine(Path.GetTempPath(), $"SimplyShare_update_{Guid.NewGuid():N}.log");
            WriteStatus("UPDATER_SCRIPT_PREP");
            var script = $$"""
                # SimplyShare 자동 업데이트 스크립트
                $ErrorActionPreference = 'Stop'

                $currentExe = '{{currentExe.Replace("'", "''")}}'
                $newExe = '{{newExePath.Replace("'", "''")}}'
                $backupExe = '{{backupPath.Replace("'", "''")}}'
                $logPath = '{{logPath.Replace("'", "''")}}'
                $statusPath = '{{UpdateStatusPath.Replace("'", "''")}}'
                $pidToWait = {{pid}}

                function Write-Log([string]$m) {
                    Add-Content -Path $logPath -Value ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $m) -Encoding UTF8
                }

                function Set-Status([string]$s) {
                    Set-Content -Path $statusPath -Value $s -Encoding UTF8
                }

                try {
                    Write-Log "Updater started"
                    Set-Status "UPDATER_SCRIPT_STARTED"

                    # 1) 현재 프로세스 종료 대기 (최대 30초)
                    Set-Status "WAITING_FOR_EXIT"
                    for ($i = 0; $i -lt 60; $i++) {
                        $proc = Get-Process -Id $pidToWait -ErrorAction SilentlyContinue
                        if (-not $proc) { break }
                        Start-Sleep -Milliseconds 500
                    }

                    # 2) 교체 재시도
                    $replaced = $false
                    for ($try = 1; $try -le 40; $try++) {
                        try {
                            if (Test-Path $backupExe) { Remove-Item -Path $backupExe -Force -ErrorAction SilentlyContinue }
                            if (Test-Path $currentExe) { Move-Item -Path $currentExe -Destination $backupExe -Force }
                            Copy-Item -Path $newExe -Destination $currentExe -Force
                            $replaced = $true
                            Write-Log "Replace success"
                            break
                        }
                        catch {
                            Write-Log "Replace try $try failed: $($_.Exception.Message)"
                            Start-Sleep -Milliseconds 500
                        }
                    }

                    if (-not $replaced) {
                        Write-Log "Replace failed permanently"
                        Set-Status "REPLACE_FAILED"
                        exit 2
                    }

                    # 3) 재시작
                    Set-Status "RESTARTING"
                    $started = $false
                    for ($r = 1; $r -le 10; $r++) {
                        try {
                            $wd = Split-Path -Path $currentExe -Parent
                            $p = Start-Process -FilePath $currentExe -WorkingDirectory $wd -PassThru -ErrorAction Stop
                            Start-Sleep -Milliseconds 500
                            $chk = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
                            if ($chk) {
                                $started = $true
                                Write-Log "Restarted pid=$($p.Id)"
                                break
                            }
                        }
                        catch {
                            Write-Log "Restart try $r failed: $($_.Exception.Message)"
                            Start-Sleep -Milliseconds 500
                        }
                    }

                    if (-not $started) {
                        Set-Status "RESTART_FAILED"
                        exit 4
                    }

                    Set-Status "RESTART_CALLED"

                    # 4) 정리
                    Start-Sleep -Seconds 2
                    Remove-Item -Path $backupExe -Force -ErrorAction SilentlyContinue
                    Remove-Item -Path $newExe -Force -ErrorAction SilentlyContinue
                    Remove-Item -Path '{{scriptPath.Replace("'", "''")}}' -Force -ErrorAction SilentlyContinue
                }
                catch {
                    Write-Log "Fatal: $($_.Exception)"
                    Set-Status "FATAL_SCRIPT_ERROR"
                    exit 3
                }
                """;

            // Windows PowerShell 5 호환을 위해 UTF-8 BOM으로 저장
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // 스크립트 실행 (숨김 창)
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
            AppLogger.Log("AutoUpdater", $"업데이트 스크립트 시작: {scriptPath}");
            WriteStatus("UPDATER_SCRIPT_STARTED");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log("AutoUpdater", $"ApplyUpdate 실패: {ex}");
            WriteStatus("SCRIPT_START_FAILED");
            return false;
        }
    }
}
