# Tsuzuri: read and write the Excel workbooks the user has open (SPEC 19).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Book "<name or full path>" -Sheet "<sheet>" -Range "B3:F20"
#       any sheet of any open workbook (not the ones the user keeps from the AI, on the web, or hidden)
#   When reading, -Book and -Sheet may be left out only when the user chose exactly one sheet in the Excel list of the launcher.
#   When writing, both are required.
#   Without -Range: the used range of the sheet. The workbook and sheet in front in Excel are never used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Write -Book "<book>" -Sheet "<sheet>" -Range "B3"
#       writes the cells in excel-write-<TSUZURI_AI_NAME>.tsv next to this script (UTF-8, tab separated, one line = one row; removed once read) from B3.
#       -File <file> reads another file. Ask the user before writing (the instructions of the AI pane).
#       Cells are entered as typed: "=" starts a formula (English function names, "," separators).
#       It does not save the workbook. Excel loses its undo history (Ctrl+Z) when this writes.
#       To throw changes away, the user closes the workbook without saving.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Overview -Book "<book>" -Sheet "<sheet>"
#       an overview of the sheet: formats, merged cells, shapes, charts, tables, names, conditional formats, comments, links, and the cells.
#       The editor writes it to a file and this prints the path (-Book and -Sheet as when reading).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Script [-Timeout 60]
#       runs the PowerShell script in excel-script-<TSUZURI_AI_NAME>.ps1 next to this script (UTF-8; removed once read) in another PowerShell process,
#       with $excel = the running Excel.Application. Whatever the script prints is printed here. -File <file> reads another file.
#       The editor refuses a script that contains save (as part of any word), Close, Quit, ChangeFileAccess, Stop-Process, taskkill,
#       SendKeys, Kill, DisplayAlerts, .Open..., .Run, RunAutoMacros, ExecuteExcel4Macro, Workbooks.Add with a path, FollowHyperlink,
#       Invoke-Item, Start-Process, or a method called by a quoted or variable name (the user saves, closes and opens workbooks
#       and runs macros), or that names a folder the user keeps from the AI. -Timeout: up to 540.
#
# Reading: the editor reads Excel and writes the cells to a file (tab separated); this prints the path of that file.
# Writing: this prints what was written.
# It goes through the pipe of the window (TSUZURI_AI_PIPE, set for the claude of the AI pane). Exit code 1 when it could not read or write.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

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

$pipe = $env:TSUZURI_AI_PIPE
if (-not $pipe) { Fail 'TSUZURI_AI_PIPE is not set. Run this from claude in the AI pane of Tsuzuri (a conversation started before this feature needs a restart).' }

$tab = [char]9
# one file per conversation (TSUZURI_AI_NAME, e.g. tab1): two conversations writing at once never swap their cells or scripts
$suffix = if ($env:TSUZURI_AI_NAME) { '-' + $env:TSUZURI_AI_NAME } else { $null }
if ($Script) {
    $default = -not $File
    if ($default) {
        if ($null -eq $suffix) { Fail 'TSUZURI_AI_NAME is not set: open a new conversation tab in the AI pane (a conversation started before this feature needs a restart), or give -File' }
        $File = Join-Path $PSScriptRoot ('excel-script' + $suffix + '.ps1')
    }
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write the script to it first)') }
    $content = [System.IO.File]::ReadAllText($File, [System.Text.Encoding]::UTF8)
    if ($default) { Remove-Item -LiteralPath $File -ErrorAction SilentlyContinue }
    $request = 'excel-script' + $tab + (B64 $content) + $tab + (B64 ([string]$Timeout))
} elseif ($Overview) {
    $request = 'excel-overview' + $tab + (B64 $Book) + $tab + (B64 $Sheet)
} elseif ($Write) {
    $default = -not $File
    if ($default) {
        if ($null -eq $suffix) { Fail 'TSUZURI_AI_NAME is not set: open a new conversation tab in the AI pane (a conversation started before this feature needs a restart), or give -File' }
        $File = Join-Path $PSScriptRoot ('excel-write' + $suffix + '.tsv')
    }
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write the cells to it first)') }
    $content = [System.IO.File]::ReadAllText($File, [System.Text.Encoding]::UTF8)
    # the default file is removed once read, so an old content is never written again and the next Write creates it anew
    if ($default) { Remove-Item -LiteralPath $File -ErrorAction SilentlyContinue }
    $request = 'excel-write' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range) + $tab + (B64 $content)
} else {
    $request = 'excel-read' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range)
}

$unsure = ' (it may or may not have changed Excel: read it to check before trying again)'
$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
$sent = $false
try {
    $client.Connect(15000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $sent = $true
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $answer = $reader.ReadLine()
} catch {
    # once the request is sent, a failure is the same as no answer: the editor may have written
    if ($sent) { Fail ('no answer from the editor: ' + $_.Exception.Message + $(if ($Write -or $Script) { $unsure } else { '' })) }
    Fail ('could not reach the editor: ' + $_.Exception.Message)
} finally {
    $client.Dispose()
}

# with -Write the request was sent: the cells may or may not have been written
if (-not $answer) { Fail ('no answer from the editor' + $(if ($Write -or $Script) { $unsure } else { '' })) }
$parts = $answer.Split([char]9, 2)
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
# the answer to a script is Base64 (its output has many lines)
if ($Script -and $message) { $message = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($message)) }
if ($parts[0] -eq 'ok') { [Console]::Out.WriteLine($message); exit 0 }
Fail $message
