# AiLink: the terminal under the AI pane, shared by the user and the AI (AiLink SPEC).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File terminal.ps1 -Type "show status"
#       puts the text in the input line of the terminal, without Enter (the user presses Enter)
#   powershell -NoProfile -ExecutionPolicy Bypass -File terminal.ps1 -TypeFile <file>
#       the same, with the text (several lines) read from a UTF-8 file
#   powershell -NoProfile -ExecutionPolicy Bypass -File terminal.ps1 -Read
#       writes everything the terminal keeps (up to 10000 lines) to a file and prints its path
#   powershell -NoProfile -ExecutionPolicy Bypass -File terminal.ps1 -Open
#       shows the terminal (starts PowerShell in it if needed) and waits until it runs. It does not take the keyboard
#
# It sends one line to AiLink through the pipe of the window (AILINK_PIPE, set for the AI of the AI pane)
# and prints the answer. Exit code 1 when AiLink refused.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param(
    [string]$Type,
    [string]$TypeFile,
    [switch]$Read,
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }

$pipe = $env:AILINK_PIPE
if (-not $pipe) { Fail 'AILINK_PIPE is not set. Run this from the AI pane of AiLink (a conversation started before this feature needs a restart).' }

if ($Open) {
    $request = 'shell-open'
} elseif ($Read) {
    $request = 'shell-dump'
} else {
    if ($TypeFile) { $text = [System.IO.File]::ReadAllText($TypeFile, [System.Text.Encoding]::UTF8) }
    elseif ($PSBoundParameters.ContainsKey('Type')) { $text = $Type }
    else { Fail 'usage: -Type "text" | -TypeFile <file> | -Read | -Open' }
    $request = 'shell-type' + [char]9 + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text))
}

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
