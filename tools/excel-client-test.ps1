# Tests excel.ps1 (Tsuzuri/Ai and AiLink/Ai) against a fake pipe server, without Excel.
# Run on Windows (Windows PowerShell 5.1), from the repository root:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\excel-client-test.ps1
# Checks that a wrong call sends nothing and keeps the file, that the default file is sent once and removed,
# and that a broken answer is reported as a failure. Keep this file ASCII only.

$ErrorActionPreference = 'Stop'
Write-Host 'Testing excel.ps1 (about three minutes; each case waits a few seconds, and the cases that cannot connect wait 15)...'
$root = Split-Path -Parent $PSScriptRoot
$failures = 0

function Start-FakeServer([string]$pipe, [string]$answer, [string]$log) {
    # a separate powershell process with one connection: records the request line, then answers (or closes without answering when
    # $answer is empty). It gives up by itself after 30 seconds, and the test kills it at the end of each case
    # (a background job blocked in WaitForConnection cannot be stopped, which hung this test)
    $code = @'
$server = New-Object System.IO.Pipes.NamedPipeServerStream('PIPE', [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
try {
    if (-not $server.WaitForConnectionAsync().Wait(30000)) { exit 2 }
    $reader = New-Object System.IO.StreamReader($server, [System.Text.Encoding]::UTF8)
    $line = $reader.ReadLine()
    [System.IO.File]::WriteAllText('LOG', [string]$line)
    $answer = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('ANSWER'))
    if ($answer) {
        $writer = New-Object System.IO.StreamWriter($server, (New-Object System.Text.UTF8Encoding($false)))
        $writer.WriteLine($answer)
        $writer.Flush()
    }
} finally {
    $server.Dispose()
}
'@
    $code = $code.Replace('PIPE', $pipe).Replace('LOG', $log.Replace("'", "''")).Replace('ANSWER', [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes([string]$answer)))
    $start = New-Object System.Diagnostics.ProcessStartInfo('powershell.exe')
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($code))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($start)
}

function Invoke-Case([string]$app, [string]$name, [string[]]$arguments, [hashtable]$setup) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('excel-client-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item -LiteralPath (Join-Path $root ($app + '\Ai\excel.ps1')) -Destination $dir
    $pipe = 'excel-client-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $log = Join-Path $dir 'request.log'
    $prefix = if ($app -eq 'AiLink') { 'AILINK_' } else { 'TSUZURI_AI_' }
    $file = Join-Path $dir $(if ($setup.Script) { 'excel-script-tab1.ps1' } else { 'excel-write-tab1.tsv' })
    if ($null -ne $setup.Bytes) { [System.IO.File]::WriteAllBytes($file, [byte[]]$setup.Bytes) }

    $server = $null
    if ($setup.Server) { $server = Start-FakeServer $pipe $setup.Answer $log; Start-Sleep -Milliseconds 1500 }
    $lock = $null
    if ($setup.Lock) { $lock = [System.IO.File]::Open($file, 'Open', 'Read', 'Read') }   # no FileShare.Delete: moving it fails
    try {
        $start = New-Object System.Diagnostics.ProcessStartInfo('powershell.exe')
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $dir 'excel.ps1') + '" ' + ($arguments -join ' ')
        $start.UseShellExecute = $false
        $start.RedirectStandardError = $true
        $start.RedirectStandardOutput = $true
        $start.EnvironmentVariables[$(if ($app -eq 'AiLink') { 'AILINK_PIPE' } else { 'TSUZURI_AI_PIPE' })] = $(if ($setup.NoPipe) { 'excel-client-test-nobody' } else { $pipe })
        $start.EnvironmentVariables[$(if ($app -eq 'AiLink') { 'AILINK_NAME' } else { 'TSUZURI_AI_NAME' })] = 'tab1'
        if ($app -eq 'AiLink') { $start.EnvironmentVariables['AILINK_WORK'] = $dir }
        $process = [System.Diagnostics.Process]::Start($start)
        $out = $process.StandardOutput.ReadToEnd()
        $err = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $code = $process.ExitCode
    } finally {
        if ($lock) { $lock.Dispose() }
    }
    if ($server) {
        # it has written the log by now when the request was sent; otherwise it is still waiting for a connection
        if (-not $server.WaitForExit(3000)) { try { $server.Kill() } catch { } }
        $server.Dispose()
    }

    $request = if (Test-Path -LiteralPath $log) { [System.IO.File]::ReadAllText($log) } else { $null }
    $left = @(Get-ChildItem -LiteralPath $dir -Filter 'excel-*' | Where-Object { $_.Name -ne 'excel.ps1' } | ForEach-Object { $_.Name })
    $problems = @()
    if ($code -ne $setup.Exit) { $problems += ('exit ' + $code + ' (expected ' + $setup.Exit + ')') }
    if ($setup.Sent -and -not $request) { $problems += 'nothing reached the server' }
    if (-not $setup.Sent -and $request) { $problems += ('sent: ' + $request.Substring(0, [Math]::Min(40, $request.Length))) }
    if ($setup.Kept -and $left -notcontains (Split-Path -Leaf $file)) { $problems += 'the file was not kept' }
    if ($setup.Removed -and $left.Count -gt 0) { $problems += ('files left: ' + ($left -join ', ')) }
    if ($setup.Request -and ($null -eq $request -or -not $request.EndsWith([char]9 + $setup.Request))) { $problems += 'the request does not carry the file content' }
    if ($setup.Error -and $err -notmatch $setup.Error) { $problems += ('message: ' + $err.Trim()) }
    if ($problems.Count -gt 0) {
        $script:failures++
        Write-Host ('NG  ' + $app + ': ' + $name + ': ' + ($problems -join '; ')) -ForegroundColor Red
    } else {
        Write-Host ('ok  ' + $app + ': ' + $name)
    }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}

$tsv = [System.Text.Encoding]::UTF8.GetBytes("a`tb`r`n")
$b64ok = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes('done'))
foreach ($app in 'AiLink', 'Tsuzuri') {
    Invoke-Case $app 'two modes' @('-Overview', '-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = $tsv; Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = 'only one' }
    Invoke-Case $app 'misspelled -Wirte' @('-Wirte', '-Book', 'b', '-Sheet', 's') @{ Bytes = $tsv; Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = 'Wirte' }
    Invoke-Case $app 'write without target' @('-Write') @{ Bytes = $tsv; Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = 'needs -Book' }
    Invoke-Case $app 'read with -File' @('-File', 'x.tsv') @{ Server = $true; Answer = 'ok'; Exit = 1; Error = 'only for -Write' }
    Invoke-Case $app 'timeout over 540' @('-Script', '-Timeout', '541') @{ Script = $true; Bytes = $tsv; Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = '1 to 540' }
    Invoke-Case $app 'timeout with read' @('-Timeout', '5') @{ Server = $true; Answer = 'ok'; Exit = 1; Error = 'only for -Script' }
    Invoke-Case $app 'value without a parameter name' @('-Book', 'b', 'extra') @{ Server = $true; Answer = 'ok'; Exit = 1; Error = 'extra' }
    Invoke-Case $app 'write sent once' @('-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = $tsv; Server = $true; Answer = "ok`twritten"; Exit = 0; Sent = $true; Removed = $true; Request = [Convert]::ToBase64String($tsv) }
    Invoke-Case $app 'not reachable keeps the file' @('-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = $tsv; NoPipe = $true; Exit = 1; Kept = $true; Error = 'Nothing was sent' }
    Invoke-Case $app 'locked file is not sent' @('-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = $tsv; Lock = $true; Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = 'could not take' }
    Invoke-Case $app 'not UTF-8 is refused and kept' @('-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = [byte[]](0x61, 0x82, 0xA0, 0x0D, 0x0A); Server = $true; Answer = 'ok'; Exit = 1; Kept = $true; Error = 'UTF-8' }
    Invoke-Case $app 'no answer after sending' @('-Write', '-Book', 'b', '-Sheet', 's', '-Range', 'B3') @{ Bytes = $tsv; Server = $true; Answer = ''; Exit = 1; Sent = $true; Removed = $true; Error = 'may or may not' }
    Invoke-Case $app 'overview ok without a path' @('-Overview', '-Book', 'b', '-Sheet', 's') @{ Server = $true; Answer = 'ok'; Exit = 1; Sent = $true; Error = 'without the file path' }
    Invoke-Case $app 'script answer not Base64' @('-Script') @{ Script = $true; Bytes = $tsv; Server = $true; Answer = "ok`t%%%"; Exit = 1; Sent = $true; Removed = $true; Error = 'may or may not' }
    Invoke-Case $app 'script ok' @('-Script', '-Timeout', '30') @{ Script = $true; Bytes = $tsv; Server = $true; Answer = "ok`t$b64ok"; Exit = 0; Sent = $true; Removed = $true }
    Invoke-Case $app 'odd answer' @('-Book', 'b', '-Sheet', 's') @{ Server = $true; Answer = 'hello'; Exit = 1; Sent = $true; Error = 'could not be read' }
}

if ($failures -gt 0) { Write-Host ('NG: ' + $failures + ' case(s) failed') -ForegroundColor Red; exit 1 }
Write-Host 'OK' -ForegroundColor Green
