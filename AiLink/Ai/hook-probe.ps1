# AiLink: a temporary hook to learn how Codex hooks behave on Windows (2026-11-25. Remove when the real hooks are in place).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File hook-probe.ps1 <EventName>
#
# Writes what the hook received to %LOCALAPPDATA%\AiLink\hook-probe\<time>-<event>.txt:
# the event name, the stdin JSON, a few environment values, the PowerShell version, and whether the AiLink pipe answered.
# For SessionStart and UserPromptSubmit it prints one line to stdout (Codex adds plain stdout of these events to the context),
# so that the reply of the AI shows whether the text arrived.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param([string]$EventName = 'unknown')

$ErrorActionPreference = 'Continue'
$log = Join-Path $env:LOCALAPPDATA 'AiLink\hook-probe'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('event: ' + $EventName)
$lines.Add('time: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'))

try {
    $buffer = New-Object System.IO.MemoryStream
    [Console]::OpenStandardInput().CopyTo($buffer)
    $lines.Add('stdin: ' + [System.Text.Encoding]::UTF8.GetString($buffer.ToArray()))
} catch {
    $lines.Add('stdin error: ' + $_.Exception.Message)
}

$lines.Add('AILINK_TAB: ' + [string]$env:AILINK_TAB)
$lines.Add('AILINK_PIPE: ' + [string]$env:AILINK_PIPE)
$lines.Add('AILINK_WORK: ' + [string]$env:AILINK_WORK)
$lines.Add('cwd: ' + (Get-Location).Path)
$lines.Add('user: ' + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
$lines.Add('restricted token: ' + [string]([System.Security.Principal.WindowsIdentity]::GetCurrent().Groups.Count))
$lines.Add('powershell: ' + [string]$PSVersionTable.PSVersion)
$lines.Add('parent process: ' + [string](Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID) -ErrorAction SilentlyContinue).ParentProcessId)

if ($env:AILINK_PIPE) {
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $env:AILINK_PIPE, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect(3000)
        $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
        $writer.WriteLine('probe')
        $writer.Flush()
        $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
        $lines.Add('pipe: answered: ' + $reader.ReadLine())
        $client.Dispose()
    } catch {
        $lines.Add('pipe error: ' + $_.Exception.Message)
    }
}

try {
    New-Item -ItemType Directory -Force -Path $log | Out-Null
    $file = Join-Path $log ((Get-Date -Format 'HHmmss-fff') + '-' + $EventName + '.txt')
    [System.IO.File]::WriteAllLines($file, $lines, (New-Object System.Text.UTF8Encoding($false)))
} catch {
    [Console]::Error.WriteLine('hook-probe could not write the log: ' + $_.Exception.Message)
}

if ($EventName -eq 'UserPromptSubmit' -or $EventName -eq 'SessionStart') {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::Out.WriteLine('[hook-probe] ' + $EventName + ' hook text arrived. At the end of your reply, write one line: HOOK-OK ' + $EventName)
}
exit 0
