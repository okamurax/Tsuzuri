# Tsuzuri and AiLink: read and write the Excel workbooks the user has open (SPEC 19, AiLink SPEC).
# One file for both apps (AiLink links this file). The environment tells which app runs it:
#   Tsuzuri: TSUZURI_AI_PIPE, TSUZURI_AI_NAME; the AI writes its files next to this script. Messages call it 'the editor'.
#   AiLink: AILINK_PIPE, AILINK_NAME, AILINK_WORK; the AI writes its files in AILINK_WORK. Messages call it 'AiLink'.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Book "<name or full path>" -Sheet "<sheet>" -Range "B3:F20"
#       any sheet of any open workbook (not the ones the user keeps from the AI, on the web, or hidden)
#   When reading, -Book and -Sheet may be left out only when the user chose exactly one sheet in the Excel list.
#   When writing, both are required.
#   Without -Range: the used range of the sheet. The workbook and sheet in front in Excel are never used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Write -Book "<book>" -Sheet "<sheet>" -Range "B3"
#       writes the cells in excel-write-<name>.tsv in the work folder (see above) (UTF-8, tab separated, one line = one row; removed once sent, kept when nothing was sent) from B3.
#       Ask the user before writing (the instructions of the AI pane).
#       Cells are entered as typed: "=" starts a formula (English function names, "," separators).
#       It does not save the workbook. Excel loses its undo history (Ctrl+Z) when this writes.
#       To throw changes away, the user closes the workbook without saving.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Overview -Book "<book>" -Sheet "<sheet>"
#       an overview of the sheet: formats, merged cells, shapes, charts, tables, names, conditional formats, comments, links, and the cells.
#       The app writes it to a file and this prints the path (-Book and -Sheet as when reading).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Script [-Timeout 60]
#       runs the PowerShell script in excel-script-<name>.ps1 in the work folder (see above) (UTF-8; removed once sent, kept when nothing was sent) in another PowerShell process,
#       with $excel = the running Excel.Application. Whatever the script prints is printed here.
#       The app checks the script with the PowerShell parser first (Ai/excel-guard.ps1) and runs only allowed forms:
#       variables, if/foreach/for/while/try, Excel members reached from $excel and called by a fixed name, a few commands
#       (Write-Output, ForEach-Object { }, Where-Object { }, Select-Object, Sort-Object ...) and a few types (object[,] ...).
#       Files, other programs, the network, & and Invoke-Expression, names built from strings or variables, and Excel members
#       that save, close, open, run macros or reach files are refused. It also refuses the words in its list (save, Close, Quit ...)
#       and full paths of folders the user keeps from the AI. -Timeout: up to 540.
#   There is no -File: the AI always writes the file of its own conversation (removed 2026-11-26: it let a call send any file).
#   A sheet or workbook name that starts with - is given as -Sheet:'-name'.
#
# Reading: the app reads Excel and writes the cells to a file (tab separated); this prints the path of that file.
# Writing: this prints what was written.
# It goes through the pipe of the window (TSUZURI_AI_PIPE or AILINK_PIPE, set for the AI of the AI pane). Exit code 1 when it could not read or write.
# A wrong call (two modes, an unknown parameter, -Write without -Book/-Sheet/-Range, a -Timeout over 540) fails before sending anything.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Book = '',
    [string]$Sheet = '',
    [string]$Range = '',
    [switch]$Write,
    [switch]$Script,
    [switch]$Overview,
    [int]$Timeout = 60
)

$ErrorActionPreference = 'Stop'
# the tool that runs this waits -Timeout + 30 seconds for a script and 120 otherwise (the instructions): give up before it does,
# counting from the start of this PowerShell process (its start-up can take seconds on a slow PC)
$started = (Get-Process -Id $PID).StartTime
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }
function B64([string]$text) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text)) }

# check the arguments before touching any file: a wrong call fails here and sends nothing
# ([CmdletBinding()] refuses unknown parameters and values without a parameter name such as a misspelled -Wirte, which would otherwise read instead of write)
$modes = @($Write, $Script, $Overview) | Where-Object { $_ }
if (@($modes).Count -gt 1) { Fail 'give only one of -Write, -Script and -Overview. Nothing was sent' }
$given = $PSBoundParameters
if ($Script) {
    foreach ($name in 'Book', 'Sheet', 'Range') { if ($given.ContainsKey($name)) { Fail ('-Script does not take -' + $name + ' (the script itself names the workbook and sheet). Nothing was sent') } }
    if ($Timeout -lt 1 -or $Timeout -gt 540) { Fail '-Timeout is from 1 to 540 seconds. Nothing was sent' }
} else {
    if ($given.ContainsKey('Timeout')) { Fail '-Timeout is only for -Script. Nothing was sent' }
}
if ($Overview -and $Range) { Fail '-Overview does not take -Range (it reads the whole sheet). Nothing was sent' }
if ($Write -and (-not $Book -or -not $Sheet -or -not $Range)) { Fail '-Write needs -Book, -Sheet and -Range (the top left cell, such as B3). Nothing was sent' }

# which app: AiLink sets AILINK_PIPE, Tsuzuri sets TSUZURI_AI_PIPE (the words below are the ones the instructions of each app quote)
if ($env:AILINK_PIPE) {
    $pipe = $env:AILINK_PIPE
    $app = 'AiLink'
    $name = $env:AILINK_NAME
    $noName = 'AILINK_NAME is not set: open a new conversation tab in AiLink'
    # the files the AI writes are in the working folder of the conversation (AILINK_WORK), where the AI may write; next to this script otherwise
    $work = if ($env:AILINK_WORK) { $env:AILINK_WORK } else { $PSScriptRoot }
} elseif ($env:TSUZURI_AI_PIPE) {
    $pipe = $env:TSUZURI_AI_PIPE
    $app = 'the editor'
    $name = $env:TSUZURI_AI_NAME
    $noName = 'TSUZURI_AI_NAME is not set: open a new conversation tab in the AI pane (a conversation started before this feature needs a restart)'
    $work = $PSScriptRoot
} else {
    Fail 'TSUZURI_AI_PIPE is not set and AILINK_PIPE is not set. Run this from the AI pane of Tsuzuri or AiLink (a conversation started before this feature needs a restart).'
}
# one file per conversation (e.g. tab1): two conversations writing at once never swap their cells or scripts
$suffix = if ($name) { '-' + $name } else { $null }

$tab = [char]9
# strict UTF-8: a file that is not UTF-8 is refused instead of sending broken characters to Excel
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$unsure = ' (it may or may not have changed Excel: read it to check before trying again)'
$changes = $Write -or $Script
$File = $null
$claimed = $null

# put the work file back when nothing was sent, so the same call can be tried again (unless a new one was written meanwhile;
# a taken file that could not be put back is cleaned up later)
function Restore {
    if ($script:claimed -and -not (Test-Path -LiteralPath $script:File)) {
        try { [System.IO.File]::Move($script:claimed, $script:File) } catch { }
    }
    $script:claimed = $null
}

$content = $null
if ($changes) {
    if ($null -eq $suffix) { Fail $noName }
    $File = Join-Path $work ($(if ($Script) { 'excel-script' } else { 'excel-write' }) + $suffix + $(if ($Script) { '.ps1' } else { '.tsv' }))
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write it first)') }
    # take the work file by moving it to a name of its own: only the call that moved it sends it,
    # and an old content is never sent again (it is removed after sending, whatever the answer)
    $claimed = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($File) + '-sending-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + [System.IO.Path]::GetExtension($File))
    try {
        [System.IO.File]::Move($File, $claimed)
    } catch {
        $claimed = $null
        Fail ('could not take ' + $File + ': ' + $_.Exception.Message + '. Nothing was sent')
    }
    try {
        $content = [System.IO.File]::ReadAllText($claimed, $utf8)
    } catch {
        Restore
        Fail ('could not read ' + $File + ' as UTF-8: ' + $_.Exception.Message + '. Nothing was sent (the file is kept)')
    }
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
$sent = $false
try {
    # the file is taken and read before connecting: a call that connects always sends a request
    try {
        $client.Connect(15000)
    } catch {
        Restore
        Fail ('could not reach ' + $app + ': ' + $_.Exception.Message + '. Nothing was sent' + $(if ($changes) { ' (the file is kept)' } else { '' }))
    }

    if ($Script) {
        $request = 'excel-script' + $tab + (B64 $content) + $tab + (B64 ([string]$Timeout)) + $tab + 'end'
    } elseif ($Overview) {
        $request = 'excel-overview' + $tab + (B64 $Book) + $tab + (B64 $Sheet)
    } elseif ($Write) {
        $request = 'excel-write' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range) + $tab + (B64 $content) + $tab + 'end'
    } else {
        $request = 'excel-read' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range)
    }

    # from here a failure may come after the app got the request: a change may or may not have been made
    $sent = $true
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    # give up before the tool that runs this does, so the AI gets the message below instead of being cut off;
    # the app may still be working then (an overview takes up to about 90 seconds when Excel is busy)
    $limit = if ($Script) { $Timeout + 25 } elseif ($Overview) { 105 } else { 90 }
    $left = [Math]::Max(1000, [int](($limit - ((Get-Date) - $started).TotalSeconds) * 1000))
    $pending = $reader.ReadLineAsync()
    if (-not $pending.Wait($left)) { Fail ('no answer from ' + $app + ' in ' + $limit + ' seconds' + $(if ($changes) { $unsure } else { '' })) }
    $answer = $pending.Result
} catch {
    if ($sent) { Fail ('no answer from ' + $app + ': ' + $_.Exception.GetBaseException().Message + $(if ($changes) { $unsure } else { '' })) }
    Restore
    Fail ('could not send to ' + $app + ': ' + $_.Exception.GetBaseException().Message + '. Nothing was sent')
} finally {
    $client.Dispose()
    if ($claimed) { Remove-Item -LiteralPath $claimed -ErrorAction SilentlyContinue }
}

if (-not $answer) { Fail ('no answer from ' + $app + $(if ($changes) { $unsure } else { '' })) }
$parts = $answer.Split([char]9, 2)
if ($parts[0] -ne 'ok' -and $parts[0] -ne 'error') { Fail ('an answer from ' + $app + ' that could not be read' + $(if ($changes) { $unsure } else { '' })) }
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
# the answer to a script is Base64 (its output has many lines)
if ($Script -and $message) {
    try {
        $message = $utf8.GetString([Convert]::FromBase64String($message))
    } catch {
        Fail ('an answer from ' + $app + ' that could not be read' + $unsure)
    }
}
if ($parts[0] -eq 'ok') {
    # reading and the overview answer with the path of the file; a script may print nothing
    if (-not $Script -and -not $Write -and -not $message) { Fail ('an answer from ' + $app + ' without the file path') }
    [Console]::Out.WriteLine($message)
    exit 0
}
Fail $message
