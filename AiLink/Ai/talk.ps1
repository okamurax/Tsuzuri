# AiLink: leave a message for the AI of another conversation tab (AiLink SPEC).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File talk.ps1 -To "#1" -Text "message"
#   powershell -NoProfile -ExecutionPolicy Bypass -File talk.ps1 -To "#1" -TextFile <file>
#       AiLink keeps the message for that tab, headed with the name of this tab, and marks the tab.
#       The user puts it in the input line of that tab with a button, reads it and presses Enter; then the AI of that tab reads it.
#
# It sends one line to AiLink through the pipe of the window (AILINK_PIPE and AILINK_TAB, set for the AI of the AI pane).
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param(
    [string]$To,
    [string]$Text,
    [string]$TextFile
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }
function B64([string]$value) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($value)) }

$pipe = $env:AILINK_PIPE
if (-not $pipe) { Fail 'AILINK_PIPE is not set. Run this from a conversation tab of AiLink.' }
if (-not $To) { Fail 'usage: -To "#1" -Text "message" | -TextFile <file>' }
if ($TextFile) {
    if (-not (Test-Path -LiteralPath $TextFile)) { Fail ('no such file: ' + $TextFile) }
    $Text = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8)
}
if (-not $Text) { Fail 'no message: give -Text or -TextFile' }

$request = 'talk' + [char]9 + [string]$env:AILINK_TAB + [char]9 + (B64 $To) + [char]9 + (B64 $Text)
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
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
if ($parts[0] -eq 'ok') { [Console]::Out.WriteLine($message); exit 0 }
Fail $message
