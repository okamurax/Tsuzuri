# AiLink: relays a Codex hook to AiLink (AiLink SPEC).
#
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File hook.ps1 <EventName>
#
# Reads the hook JSON from stdin, sends "hook <event> <tab> <base64 json>" (tab separated) to the pipe of AiLink
# (AILINK_PIPE, AILINK_TAB: set by AiLink for the Codex of a conversation tab), and prints what AiLink answers
# ("ok <base64 stdout>"). Codex reads that output (JSON) as the answer of the hook.
# When it is not run by AiLink (no AILINK_TAB) or AiLink does not answer, it prints nothing and exits 0 (the hook does nothing).
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param([string]$EventName = '')

$ErrorActionPreference = 'Stop'
try {
    $buffer = New-Object System.IO.MemoryStream
    [Console]::OpenStandardInput().CopyTo($buffer)
    $json = $buffer.ToArray()
} catch {
    exit 0
}

$pipe = $env:AILINK_PIPE
$tabId = $env:AILINK_TAB
if (-not $pipe -or -not $tabId -or -not $EventName) { exit 0 }

$answer = $null
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine('hook' + [char]9 + $EventName + [char]9 + $tabId + [char]9 + [Convert]::ToBase64String($json))
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $answer = $reader.ReadLine()
} catch {
    exit 0
} finally {
    $client.Dispose()
}

try {
    if (-not $answer) { exit 0 }
    $parts = $answer.Split([char]9, 2)
    if ($parts[0] -ne 'ok' -or $parts.Count -lt 2 -or -not $parts[1]) { exit 0 }
    $bytes = [Convert]::FromBase64String($parts[1])
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
} catch {
}
exit 0
