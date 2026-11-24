# Tsuzuri: read the Excel workbooks the user has open, and write to the sheet the user chose (SPEC 19).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1
#       the used range of the sheet the user chose in the Excel list of the launcher
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Range "A1:H200"
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Sheet "<sheet>" -Range "B3:F20"   (another sheet of the chosen workbook)
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Book "<name or full path>" -Sheet "<sheet>"
# The workbook and sheet in front in Excel are never used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Write -Range "B3"
#       writes the cells in excel-write.tsv next to this script (UTF-8, tab separated, one line = one row; removed once read) from B3,
#       into the sheet the user chose (only that sheet can be written). -File <file> reads another file.
#       Cells are entered as typed: "=" starts a formula (English function names, "," separators).
#       It does not save the workbook. Excel loses its undo history (Ctrl+Z) when this writes.
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
    [string]$File = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }
function B64([string]$text) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text)) }

$pipe = $env:TSUZURI_AI_PIPE
if (-not $pipe) { Fail 'TSUZURI_AI_PIPE is not set. Run this from claude in the AI pane of Tsuzuri (a conversation started before this feature needs a restart).' }

$tab = [char]9
if ($Write) {
    $default = -not $File
    if ($default) { $File = Join-Path $PSScriptRoot 'excel-write.tsv' }
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write the cells to it first)') }
    $content = [System.IO.File]::ReadAllText($File, [System.Text.Encoding]::UTF8)
    # the default file is removed once read, so an old content is never written again and the next Write creates it anew
    if ($default) { Remove-Item -LiteralPath $File -ErrorAction SilentlyContinue }
    $request = 'excel-write' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range) + $tab + (B64 $content)
} else {
    $request = 'excel-read' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range)
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(15000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $answer = $reader.ReadLine()
} catch {
    Fail ('could not reach the editor: ' + $_.Exception.Message)
} finally {
    $client.Dispose()
}

if (-not $answer) { Fail 'no answer from the editor' }
$parts = $answer.Split([char]9, 2)
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
if ($parts[0] -eq 'ok') { [Console]::Out.WriteLine($message); exit 0 }
Fail $message
