# AiLink: the current state for the AI of a conversation tab (AiLink SPEC).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File state.ps1
#       prints which conversation this is, the other conversations, and the Excel sheets the user chose in the Excel list.
#       Run it only when the current request came without the current state (the instructions say when).
#
# It sends one line to AiLink through the pipe of the window (AILINK_PIPE and AILINK_TAB, set for the AI of the AI pane).
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }

$pipe = $env:AILINK_PIPE
if (-not $pipe) { Fail 'AILINK_PIPE is not set. Run this from a conversation tab of AiLink.' }
$tabId = [string]$env:AILINK_TAB

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine('state' + [char]9 + $tabId)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $answer = $reader.ReadLine()
} catch {
    Fail ('could not reach AiLink: ' + $_.Exception.Message)
} finally {
    $client.Dispose()
}

if (-not $answer) { Fail 'no answer from AiLink' }
$parts = $answer.Split([char]9, 2)
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
if ($parts[0] -eq 'ok') { [Console]::Out.WriteLine([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($message))); exit 0 }
Fail $message
