# AiLink: read and write the Excel workbooks the user has open (AiLink SPEC).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Book "<name or full path>" -Sheet "<sheet>" -Range "B3:F20"
#       any sheet of any open workbook (not the ones the user keeps from the AI, on the web, or hidden)
#   When reading, -Book and -Sheet may be left out only when the user chose exactly one sheet in the Excel list.
#   When writing, both are required.
#   Without -Range: the used range of the sheet. The workbook and sheet in front in Excel are never used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Write -Book "<book>" -Sheet "<sheet>" -Range "B3"
#       writes the cells in excel-write-<AILINK_NAME>.tsv in AILINK_WORK (next to this script without it) (UTF-8, tab separated, one line = one row; removed once sent, kept when nothing was sent) from B3.
#       -File <file> reads another file. Ask the user before writing (the instructions of the AI pane).
#       Cells are entered as typed: "=" starts a formula (English function names, "," separators).
#       It does not save the workbook. Excel loses its undo history (Ctrl+Z) when this writes.
#       To throw changes away, the user closes the workbook without saving.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Overview -Book "<book>" -Sheet "<sheet>"
#       an overview of the sheet: formats, merged cells, shapes, charts, tables, names, conditional formats, comments, links, and the cells.
#       AiLink writes it to a file and this prints the path (-Book and -Sheet as when reading).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Script [-Timeout 60]
#       runs the PowerShell script in excel-script-<AILINK_NAME>.ps1 in AILINK_WORK (next to this script without it) (UTF-8; removed once sent, kept when nothing was sent) in another PowerShell process,
#       with $excel = the running Excel.Application. Whatever the script prints is printed here. -File <file> reads another file.
#       AiLink refuses a script that contains save (as part of any word), Close, Quit, ChangeFileAccess, Stop-Process, taskkill,
#       SendKeys, Kill, DisplayAlerts, .Open..., .Run, RunAutoMacros, ExecuteExcel4Macro, Workbooks.Add with a path, FollowHyperlink,
#       Invoke-Item, Start-Process, or a method called by a quoted or variable name (the user saves, closes and opens workbooks
#       and runs macros), or that names a folder the user keeps from the AI. -Timeout: up to 540.
#
# Reading: AiLink reads Excel and writes the cells to a file (tab separated); this prints the path of that file.
# Writing: this prints what was written.
# It goes through the pipe of the window (AILINK_PIPE, set for the AI of the AI pane). Exit code 1 when it could not read or write.
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
    [int]$Timeout = 60,
    [string]$File = ''
)

$ErrorActionPreference = 'Stop'
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
    if ($File -and -not $Write) { Fail '-File is only for -Write and -Script. Nothing was sent' }
}
if ($Overview -and $Range) { Fail '-Overview does not take -Range (it reads the whole sheet). Nothing was sent' }
if ($Write -and (-not $Book -or -not $Sheet -or -not $Range)) { Fail '-Write needs -Book, -Sheet and -Range (the top left cell, such as B3). Nothing was sent' }

$pipe = $env:AILINK_PIPE
if (-not $pipe) { Fail 'AILINK_PIPE is not set. Run this from the AI pane of AiLink (a conversation started before this feature needs a restart).' }

# the files the AI writes (excel-write-<name>.tsv, excel-script-<name>.ps1) are in the working folder of the conversation (AILINK_WORK),
# where the AI may write; next to this script otherwise
$work = if ($env:AILINK_WORK) { $env:AILINK_WORK } else { $PSScriptRoot }
# one file per conversation tab (AILINK_NAME, e.g. tab1): two tabs writing at once never swap their cells or scripts
$suffix = if ($env:AILINK_NAME) { '-' + $env:AILINK_NAME } else { $null }

$tab = [char]9
# strict UTF-8: a file that is not UTF-8 is refused instead of sending broken characters to Excel
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$default = $false
if ($Write -or $Script) {
    $default = -not $File
    if ($default) {
        if ($null -eq $suffix) { Fail ('AILINK_NAME is not set: open a new conversation tab in AiLink, or give -File') }
        $File = Join-Path $work ($(if ($Script) { 'excel-script' } else { 'excel-write' }) + $suffix + $(if ($Script) { '.ps1' } else { '.tsv' }))
    }
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write it first)') }
}

$unsure = ' (it may or may not have changed Excel: read it to check before trying again)'
$changes = $Write -or $Script
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
$sent = $false
$claimed = $null
try {
    # connect first: when AiLink cannot be reached, the file is left as it is and the same call can be tried again
    try {
        $client.Connect(15000)
    } catch {
        Fail ('could not reach AiLink: ' + $_.Exception.Message + '. Nothing was sent')
    }

    $content = $null
    if ($Write -or $Script) {
        $source = $File
        if ($default) {
            # take the default file by moving it to a name of its own: only the call that moved it sends it,
            # and an old content is never sent again (it is removed after sending, whatever the answer)
            $claimed = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($File) + '-sending-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + [System.IO.Path]::GetExtension($File))
            try {
                [System.IO.File]::Move($File, $claimed)
            } catch {
                $claimed = $null
                Fail ('could not take ' + $File + ': ' + $_.Exception.Message + '. Nothing was sent')
            }
            $source = $claimed
        }
        try {
            $content = [System.IO.File]::ReadAllText($source, $utf8)
        } catch {
            # put the default file back so it can be fixed (unless a new one was written meanwhile)
            if ($claimed -and -not (Test-Path -LiteralPath $File)) {
                try { [System.IO.File]::Move($claimed, $File) } catch { }
            }
            # not sent: keep it under either name (a moved file that could not be put back is cleaned up later)
            $claimed = $null
            Fail ('could not read ' + $File + ' as UTF-8: ' + $_.Exception.Message + '. Nothing was sent')
        }
    }

    if ($Script) {
        $request = 'excel-script' + $tab + (B64 $content) + $tab + (B64 ([string]$Timeout))
    } elseif ($Overview) {
        $request = 'excel-overview' + $tab + (B64 $Book) + $tab + (B64 $Sheet)
    } elseif ($Write) {
        $request = 'excel-write' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range) + $tab + (B64 $content)
    } else {
        $request = 'excel-read' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range)
    }

    # from here a failure may come after AiLink got the request: a change may or may not have been made
    $sent = $true
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    # give up before the tool that runs this does (the instructions give it -Timeout + 30 seconds for a script, 120 otherwise),
    # so the AI gets the message below instead of being cut off; AiLink may still be working then
    $limit = if ($Script) { $Timeout + 20 } else { 90 }
    $pending = $reader.ReadLineAsync()
    if (-not $pending.Wait($limit * 1000)) { Fail ('no answer from AiLink in ' + $limit + ' seconds' + $(if ($changes) { $unsure } else { '' })) }
    $answer = $pending.Result
} catch {
    if ($sent) { Fail ('no answer from AiLink: ' + $_.Exception.GetBaseException().Message + $(if ($changes) { $unsure } else { '' })) }
    Fail ('could not send to AiLink: ' + $_.Exception.GetBaseException().Message + '. Nothing was sent')
} finally {
    $client.Dispose()
    if ($claimed) { Remove-Item -LiteralPath $claimed -ErrorAction SilentlyContinue }
}

if (-not $answer) { Fail ('no answer from AiLink' + $(if ($changes) { $unsure } else { '' })) }
$parts = $answer.Split([char]9, 2)
if ($parts[0] -ne 'ok' -and $parts[0] -ne 'error') { Fail ('an answer from AiLink that could not be read' + $(if ($changes) { $unsure } else { '' })) }
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
# the answer to a script is Base64 (its output has many lines)
if ($Script -and $message) {
    try {
        $message = $utf8.GetString([Convert]::FromBase64String($message))
    } catch {
        Fail ('an answer from AiLink that could not be read' + $unsure)
    }
}
if ($parts[0] -eq 'ok') {
    # reading and the overview answer with the path of the file; a script may print nothing
    if (-not $Script -and -not $Write -and -not $message) { Fail 'an answer from AiLink without the file path' }
    [Console]::Out.WriteLine($message)
    exit 0
}
Fail $message
