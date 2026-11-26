# Tests Tsuzuri/Ai/excel-guard.ps1 (the check of the AI's Excel COM scripts, used by Tsuzuri and AiLink) without Excel.
# Run on Windows (Windows PowerShell 5.1):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repository>\tools\excel-guard-test.ps1
# Scripts that an AI writes for ordinary Excel work must pass; scripts that reach files, programs, the network,
# reflection, macros, or save / close / open must be refused. Keep this file ASCII only.

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Tsuzuri\Ai\excel-guard.ps1')
$failures = 0

$pass = @(
    @('read values', @'
$ws = $excel.Workbooks.Item('book.xlsx').Worksheets.Item('Sheet1')
$values = $ws.Range('A1:C10').Value2
for ($r = 1; $r -le 10; $r++) { Write-Output ($values[$r, 1]) }
'@),
    @('write a block', @'
$ws = $excel.Workbooks.Item('book.xlsx').Worksheets.Item('Sheet1')
$data = New-Object 'object[,]' 3, 2
$data[0, 0] = 'a'; $data[0, 1] = 1
$ws.Range('B3').Resize(3, 2).Value2 = $data
'@),
    @('format and widths', @'
$wb = $excel.Workbooks.Item('book.xlsx')
$src = $wb.Worksheets.Item('Data')
$dst = $wb.Worksheets.Item('Copy')
$src.Range('A1:D20').Copy($dst.Range('F1')) | Out-Null
for ($i = 1; $i -le 4; $i++) { $dst.Columns.Item(5 + $i).ColumnWidth = $src.Columns.Item($i).ColumnWidth }
$dst.Range('F1:I1').Font.Bold = $true
$dst.Range('F2:F20').NumberFormat = 'yyyy/mm/dd'
'@),
    @('loops, pipeline, hashtable', @'
$ws = $excel.Workbooks.Item('book.xlsx').Worksheets.Item('Sheet1')
$rows = @()
foreach ($cell in $ws.Range('A1:A5').Cells) { $rows += [pscustomobject]@{ Address = $cell.Address(); Text = $cell.Text } }
$rows | Where-Object { $_.Text -ne '' } | Sort-Object Address | Select-Object Address, Text | Format-Table -AutoSize | Out-String
$names = @{}
foreach ($sheet in $excel.Workbooks.Item('book.xlsx').Worksheets) { $names[$sheet.Name] = $sheet.UsedRange.Rows.Count }
$names.Keys | ForEach-Object { "{0}: {1}" -f $_, $names[$_] }
'@),
    @('try, math, dates', @'
$ws = $excel.Workbooks.Item('book.xlsx').Worksheets.Item('Sheet1')
try { $last = $ws.Cells.Item($ws.Rows.Count, 1).End(-4162).Row } catch { Write-Output ('failed: ' + $_.Exception.Message) }
$total = [Math]::Round(($ws.Range("B2:B$last").Value2 | Measure-Object -Sum).Sum, 2)
Write-Output ("total {0} at {1}" -f $total, (Get-Date -Format 'yyyy-MM-dd'))
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($ws)
'@),
    @('sheets, sort, filter, chart', @'
$wb = $excel.Workbooks.Item('book.xlsx')
$new = $wb.Worksheets.Add()
$new.Name = 'Summary'
$ws = $wb.Worksheets.Item('Data')
$ws.Range('A1:D20').Sort($ws.Range('B1'), 2) | Out-Null
$ws.Range('A1:D20').AutoFilter(2, '>10') | Out-Null
$chart = $ws.ChartObjects().Add(300, 10, 400, 250).Chart
$chart.SetSourceData($ws.Range('A1:B20'))
$list = New-Object System.Collections.ArrayList
[void]$list.Add($chart.ChartType)
$wb.Worksheets.Item('Old').Delete()
'@),
    @('omitted arguments and COM errors', @'
$wb = $excel.Workbooks.Item('book.xlsx')
$after = $wb.Worksheets.Item([int]$wb.Worksheets.Count)
$copy = $wb.Worksheets.Add([Type]::Missing, $after)
$wb.Worksheets.Item('Data').Copy([System.Reflection.Missing]::Value, $after)
try { $copy.Name = 'Copy' } catch [System.Management.Automation.MethodInvocationException] { Write-Output $_.Exception.Message } catch [System.Runtime.InteropServices.COMException] { Write-Output 'com' }
[long[]]$ids = 1, 2
$list = New-Object 'System.Collections.Generic.List[System.Object]'
'@),
    @('calculated property and sort block', @'
$wb = $excel.Workbooks.Item('book.xlsx')
$wb.Worksheets | Select-Object @{ n = 'Sheet'; e = { $_.Name } }, Index | Sort-Object { $_.Name -eq 'Run' } -Desc | Format-Table -AutoSize | Out-String
$rows = foreach ($s in $wb.Worksheets) { @{ Title = $s.Name; Size = $s.UsedRange.Rows.Count } }
$rows | Where-Object { $_['Size'] -gt 0 } | ForEach-Object { $_['Title'] }
$counts = @{ a = 1; b = 2 }
$counts.GetEnumerator() | Sort-Object Key | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }
$first = $wb.Worksheets[1].Name
for ($i = 1; $i -le $wb.Worksheets.Count; $i++) { $wb.Worksheets.Item([int]$i).Name }
$sheetName = 'Data'; $wb.Worksheets.Item([string]$sheetName).Name; $wb.Worksheets.Item("$sheetName").Name
$n = 0; if ([int]::TryParse('12', [ref]$n)) { $n }
$wb.Worksheets.Item(1).Range('A1').Value2 2>$null
$wb.Worksheets | Select-Object -Expand Name
foreach ($sheet in $wb.Worksheets) { $sheet.Name }
$wb.Worksheets | ForEach-Object { $_.UsedRange.Address() }
$wb.Worksheets | Select-Object -First 3 -Property Name
'@),
    @('table, pivot from a range, formats', @'
$ws = $excel.Workbooks.Item('book.xlsx').Worksheets.Item('Data')
$table = $ws.ListObjects.Add(1, $ws.Range('A1:D20'), [Type]::Missing, 1)
$table.TableStyle = 'TableStyleMedium2'
$cache = $excel.Workbooks.Item('book.xlsx').PivotCaches().Create(1, $ws.Range('A1:D20'))
$pt = $cache.CreatePivotTable($ws.Range('G1'), 'Pivot1')
$cond = $ws.Range('B2:B20').FormatConditions.Add(1, 5, '=100')
$cond.Interior.Color = 255
'@)
)

$refuse = @(
    @('Remove-Item', 'Remove-Item C:\temp\a.txt'),
    @('file class', '[System.IO.File]::Delete("C:\temp\a.txt")'),
    @('file class short', '[IO.File]::ReadAllText("C:\a.txt")'),
    @('call operator', '& cmd /c dir'),
    @('dot source', '. C:\temp\x.ps1'),
    @('Invoke-Expression', 'Invoke-Expression "dir"'),
    @('iex', 'iex "dir"'),
    @('Start-Process', 'Start-Process notepad'),
    @('web', 'Invoke-WebRequest http://example.com'),
    @('web class', '(New-Object System.Net.WebClient).DownloadString("http://example.com")'),
    @('COM object', 'New-Object -ComObject WScript.Shell'),
    @('redirection', '"x" > C:\temp\a.txt'),
    @('env drive', '$env:USERPROFILE'),
    @('file variable', '${C:\temp\a.txt}'),
    @('function drive', '$function:x = { dir }'),
    @('execution context', '$ExecutionContext.InvokeCommand.InvokeScript("dir")'),
    @('host', '$Host.UI.RawUI'),
    @('dynamic member', '$m = "Save"; $excel.Workbooks.Item(1).$m()'),
    @('built member name', '$excel.Workbooks.Item(1).("Sa" + "ve")()'),
    @('quoted save', '$excel.Workbooks.Item(1)."Save"()'),
    @('save', '$excel.Workbooks.Item(1).Save()'),
    @('save as', '$excel.Workbooks.Item(1).SaveAs("C:\temp\x.xlsx")'),
    @('close', '$excel.Workbooks.Item(1).Close()'),
    @('quit', '$excel.Quit()'),
    @('open', '$excel.Workbooks.Open("C:\secret\x.xlsx")'),
    @('run macro', '$excel.Run("Macro1")'),
    @('xlm', '$excel.ExecuteExcel4Macro("EXEC(""calc"")")'),
    @('display alerts', '$excel.DisplayAlerts = $false'),
    @('vba project', '$excel.Workbooks.Item(1).VBProject.VBComponents.Add(1)'),
    @('vba by property name', '$excel.Workbooks.Item(1) | Select-Object -ExpandProperty VBProject'),
    @('workbooks add with file', '$excel.Workbooks.Add("C:\secret\x.xlsx")'),
    @('picture from file', '$excel.ActiveSheet.Shapes.AddPicture("C:\secret\a.png", 0, 1, 0, 0, 10, 10)'),
    @('export pdf', '$excel.Workbooks.Item(1).ExportAsFixedFormat(0, "C:\temp\a.pdf")'),
    @('query table', '$excel.Workbooks.Item(1).Worksheets.Item(1).QueryTables.Add("URL;http://example.com", $null)'),
    @('add-in', '$excel.RegisterXLL("C:\temp\a.xll")'),
    @('psobject', '$excel.Workbooks.Item(1).PSObject.Methods'),
    @('gettype', '"".GetType().Assembly'),
    @('reflection by exception', 'try { 1/0 } catch { $_.Exception.TargetSite.DeclaringType }'),
    @('script block invoke', '$b = { 1 }; $b.Invoke()'),
    @('foreach by name', '$excel.Workbooks | ForEach-Object Save'),
    @('foreach member name', '$excel.Workbooks | ForEach-Object -MemberName Save'),
    @('.ForEach by name', '@($excel.Workbooks.Item(1)).ForEach("Save")'),
    @('type cast', '[System.Diagnostics.Process]::Start("calc")'),
    @('scriptblock create', '[ScriptBlock]::Create("dir")'),
    @('marshal other', '[System.Runtime.InteropServices.Marshal]::GetActiveObject("WScript.Shell")'),
    @('activator', '[Activator]::CreateInstance([type]"System.Object")'),
    @('new-object other type', 'New-Object System.IO.StreamWriter "C:\temp\a.txt"'),
    @('get-content', 'Get-Content C:\secret\a.txt'),
    @('attribute', 'function f { [CmdletBinding()] param() }'),
    @('parse error', '$excel.Workbooks.Item(1'),
    @('follow hyperlink', '$excel.Workbooks.Item(1).FollowHyperlink("http://example.com")'),
    @('send keys', '$excel.SendKeys("x")'),
    @('webservice', '$excel.WorksheetFunction.WebService("http://example.com")'),
    # found by the review (2026-11-26)
    @('function shadowing a command', 'Remove-Item -Recurse "C:\temp\x"; function Remove-Item {}'),
    @('function never defined', 'if ($false) { function cmd {} }; cmd /c calc'),
    @('-as with a type string', '(''C:\temp\x'' -as ''IO.DirectoryInfo'').Delete($true)'),
    @('-as hashtable to a process', '$p = @{ StartInfo = (''calc.exe'' -as ''Diagnostics.ProcessStartInfo'') } -as ''Diagnostics.Process'''),
    @('-is with a string', '$x -is ''IO.File'''),
    @('-as [type]', '''IO.File'' -as [type]'),
    @('built property name', '$ws | Select-Object -ExpandProperty (''Query'' + ''Tables'')'),
    @('wildcard property', '$excel | Select-Object -ExpandProperty ''AddI*'''),
    @('wildcard vb', '$excel | Select-Object -Property ''VB?'''),
    @('splatting', '$a = @{ ExpandProperty = ''VBE'' }; $excel | Select-Object @a'),
    @('workbooks in a variable', '$b = $excel.Workbooks; $b.Add(''C:\any\secret.xlsm'')'),
    @('sheets add with a template', '$excel.Workbooks.Item(1).Sheets.Add($null, $null, 1, ''C:\x\y.xltx'')'),
    @('table from a query', '$excel.Workbooks.Item(1).Worksheets.Item(1).ListObjects.Add(0, ''OLEDB;x'', $true, 1, $excel.Workbooks.Item(1).Worksheets.Item(1).Range(''A1''))'),
    @('pivot from a connection', '$excel.Workbooks.Item(1).PivotCaches().Create(2, ''conn'')'),
    @('command bars', '$excel.CommandBars.ExecuteMso(''FileSave'')'),
    @('dde', '$excel.DDEInitiate(''cmd'', ''x'')'),
    @('microsoft app', '$excel.ActivateMicrosoftApp(1)'),
    @('publish', '$excel.Workbooks.Item(1).PublishObjects.Add(1, ''C:\x.htm'')'),
    @('new document from a link', '$excel.Workbooks.Item(1).Worksheets.Item(1).Hyperlinks.Item(1).CreateNewDocument(''C:\x.txt'', $false, $true)'),
    @('startup path', '$excel.AltStartupPath = ''\\host\share'''),
    @('hidden save as', '$excel.Workbooks.Item(1)._SaveAs(''C:\x.xlsx'')'),
    @('vba import', '$excel.Workbooks.Item(1).VBProject.VBComponents.Import(''C:\x.bas'')'),
    @('theme from a file', '$excel.Workbooks.Item(1).ApplyTheme(''C:\x.thmx'')'),
    @('evaluate', '$excel.Evaluate(''MyUdf()'')'),
    @('exit code', '$excel.Workbooks.Item(1).Worksheets.Item(1).Range(''A1'').Value2 = 1; exit 87'),
    @('type other member', '[Type]::GetType(''System.IO.File'')'),
    @('type cast', '[type]''System.IO.File'''),
    @('own name used as a method', '$x = @{ CreateNewDocument = 1 }; $excel.Workbooks.Item(1).Worksheets.Item(1).Hyperlinks.Item(1).CreateNewDocument(''C:\x.txt'', $false, $true)'),
    @('own name used to set', '$x = @{ AltStartupFolder = 1 }; $excel.AltStartupFolder = ''x'''),
    @('own name that is denied', '$x = @{ VBProject = 1 }; $excel.Workbooks.Item(1).VBProject'),
    # found by the second review (2026-11-26)
    @('default parameter values', '$PSDefaultParameterValues = @{ ''ForEach-Object:MemberName'' = ''Run'' }; $excel | ForEach-Object'),
    @('foreach without a block', '$excel | ForEach-Object'),
    @('common parameter', '$excel.Workbooks.Item(1) | Select-Object -Property Name -OutVariable x'),
    @('switch file', 'switch -File (''C:\x'' + ''.txt'') { default { $_ } }'),
    @('built hashtable key', '$ws | Select-Object -Property @{ n = ''qt''; (''e'' + ''xpression'') = ''QueryTables'' }'),
    @('own label then method', '($ws | Select-Object -Property @{ n = ''qt''; e = { 1 } }).qt.Add(''URL;http://example.com'')'),
    @('xml maps', '$excel.Workbooks.Item(1).XmlMaps.Add(''http://example.com/s.xsd'')'),
    @('auto recover', '$excel.AutoRecover.Path = ''\\host\s'''),
    @('queries', '$excel.Workbooks.Item(1).Queries.Item(1).Formula = ''x'''),
    @('consolidate', '$excel.Workbooks.Item(1).Worksheets.Item(1).Range(''A1'').Consolidate(@(''x''), 1)'),
    @('hidden add on tables', '$excel.Workbooks.Item(1).Worksheets.Item(1).ListObjects._Add(0, ''OLEDB;x'')'),
    @('old pivot add', '$excel.Workbooks.Item(1).PivotCaches().Add(2, ''conn'')'),
    @('protect with a password', '$excel.Workbooks.Item(1).Worksheets.Item(1).Protect(''secret'')'),
    @('redirect to a file still refused', '$excel.Version 2> C:\x.txt'),
    # found by the third review (2026-11-26)
    @('item with an array', '$s = $excel.Workbooks.Item(1).Worksheets.Item(@(1, 2)); $s.Add($null, $null, 1, ''C:\x\y.xltx'')'),
    @('item with a variable', '$i = 1, 2; $s = $excel.Workbooks.Item(1).Worksheets.Item($i)'),
    @('pivot from text', '$excel.Workbooks.Item(1).PivotCaches().Create(1, "''C:\x\[a.xlsx]Sheet1''!R1C1:R99C5")'),
    @('pivot from text in a cell', '$excel.Workbooks.Item(1).PivotCaches().Create(1, $excel.Workbooks.Item(1).Worksheets.Item(1).Range(''Z1'').Value2)'),
    @('paste the clipboard', '$excel.Workbooks.Item(1).Worksheets.Item(1).Paste()')
)

foreach ($case in $pass) {
    $problem = Test-ExcelScript $case[1]
    if ($problem) { $failures++; Write-Host ('NG  should pass: ' + $case[0] + ': ' + $problem) -ForegroundColor Red }
    else { Write-Host ('ok  passes: ' + $case[0]) }
}
foreach ($case in $refuse) {
    $problem = Test-ExcelScript $case[1]
    if (-not $problem) { $failures++; Write-Host ('NG  should be refused: ' + $case[0]) -ForegroundColor Red }
    else { Write-Host ('ok  refused: ' + $case[0] + ': ' + $problem) }
}

if ($failures -gt 0) { Write-Host ('NG: ' + $failures + ' case(s) failed') -ForegroundColor Red; exit 1 }
Write-Host 'OK' -ForegroundColor Green
