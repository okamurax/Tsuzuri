# Checks a COM script the AI wants to run on Excel (excel.ps1 -Script) before it runs (2026-11-26. The user's decision).
#
# The script runs as the user, outside any sandbox, so a word list is not enough ('Sa'+'ve', [IO.File]::Delete, & cmd ...).
# This parses the script with the PowerShell parser and lets through only allowed forms (an allow list, not a deny list):
#   variables, assignments, if / foreach / for / while / do / switch / try / break / continue / return / throw,
#   operators, strings, numbers, arrays, hashtables, script blocks,
#   members and methods named directly and found in $AllowedMembers (the Excel object model for editing cells, formats,
#   sheets, tables, charts, pivot tables, and members of strings, numbers, dates, arrays and lists),
#   a few commands ($AllowedCommands) and a few types ($AllowedTypes).
# Refused: other commands, & and . , functions (a name defined anywhere in the script would let the real command run first),
# exit (the app uses exit codes to tell what happened), file redirection, drive variables ($env:, ${C:\...}),
# $ExecutionContext and $Host, members named by a string or a variable, members not in the list, -as / -is with a type
# given as a string, property names given to Select-Object and the like that are built, wildcards or not in the list,
# Excel collections (Workbooks, Sheets ...) kept in a variable, Workbooks.Add with a file, Sheets.Add with a template,
# and tables and pivot caches from outside sources.
# What stays possible through Excel itself: formulas written into cells (an external reference, WEBSERVICE ...) are content,
# not code, and are not checked here (the instructions tell the AI not to write them).
#
# Used by the app: the wrapper that runs a script includes this file, calls Test-ExcelScript on the text,
# and runs the same text only when it returns nothing. Shared by Tsuzuri and AiLink (AiLink links this file).
# tools/excel-guard-test.ps1 checks it with scripts that must pass and scripts that must be refused.
# Keep this file ASCII only.

function Test-ExcelScript([string]$Text) {
    $AllowedAsts = @(
        'ScriptBlockAst', 'NamedBlockAst', 'StatementBlockAst', 'PipelineAst', 'CommandExpressionAst', 'CommandAst',
        'CommandParameterAst', 'AssignmentStatementAst', 'IfStatementAst', 'ForEachStatementAst', 'ForStatementAst',
        'WhileStatementAst', 'DoWhileStatementAst', 'DoUntilStatementAst', 'SwitchStatementAst', 'BreakStatementAst',
        'ContinueStatementAst', 'ReturnStatementAst', 'ThrowStatementAst', 'TryStatementAst', 'CatchClauseAst',
        'ParenExpressionAst', 'SubExpressionAst', 'ArrayExpressionAst', 'ArrayLiteralAst', 'HashtableAst',
        'BinaryExpressionAst', 'UnaryExpressionAst', 'ConstantExpressionAst', 'StringConstantExpressionAst',
        'ExpandableStringExpressionAst', 'VariableExpressionAst', 'MemberExpressionAst', 'InvokeMemberExpressionAst',
        'IndexExpressionAst', 'ScriptBlockExpressionAst', 'ConvertExpressionAst', 'TypeExpressionAst', 'TypeConstraintAst',
        'MergingRedirectionAst')
    $AllowedCommands = @(
        'write-output', 'echo', 'write-host', 'write-warning', 'out-string', 'out-null',
        'foreach-object', '%', 'foreach', 'where-object', '?', 'where',
        'select-object', 'select', 'sort-object', 'sort', 'measure-object', 'measure', 'group-object', 'group',
        'format-table', 'ft', 'format-list', 'fl', 'get-date', 'start-sleep', 'sleep', 'new-object')
    # the parameters each command may take (no common parameters such as -OutVariable / -PipelineVariable, which set variables).
    # A parameter may be shortened (-Expand) when only one of these starts with it
    $writeParameters = @('inputobject', 'noenumerate', 'nonewline', 'separator', 'foregroundcolor', 'backgroundcolor', 'object', 'message', 'width', 'stream')
    $loopParameters = @('process', 'begin', 'end', 'filterscript')
    $propertyParameters = @('property', 'expandproperty', 'excludeproperty', 'first', 'last', 'skip', 'skiplast', 'unique', 'descending', 'sum', 'average',
        'maximum', 'minimum', 'autosize', 'wrap', 'index', 'casesensitive', 'noelement', 'asstring', 'ashashtable', 'line', 'word', 'character',
        'groupby', 'hidetableheaders', 'inputobject', 'top', 'bottom')
    $CommandParameters = @{
        'write-output' = $writeParameters; 'echo' = $writeParameters; 'write-host' = $writeParameters; 'write-warning' = $writeParameters
        'out-string' = $writeParameters; 'out-null' = $writeParameters
        'foreach-object' = $loopParameters; '%' = $loopParameters; 'foreach' = $loopParameters
        'where-object' = $loopParameters; '?' = $loopParameters; 'where' = $loopParameters
        'get-date' = @('format', 'uformat', 'date', 'year', 'month', 'day', 'hour', 'minute', 'second', 'millisecond')
        'start-sleep' = @('seconds', 'milliseconds'); 'sleep' = @('seconds', 'milliseconds')
        'new-object' = @('typename', 'argumentlist')
    }
    foreach ($command in 'select-object', 'select', 'sort-object', 'sort', 'measure-object', 'measure', 'group-object', 'group', 'format-table', 'ft', 'format-list', 'fl') {
        $CommandParameters[$command] = $propertyParameters
    }
    # commands that take property names (each name must be written directly and be in $AllowedMembers)
    $PropertyCommands = @('select-object', 'select', 'sort-object', 'sort', 'measure-object', 'measure', 'group-object', 'group', 'format-table', 'ft', 'format-list', 'fl')
    # types are compared in lower case, with every "system." removed
    $AllowedTypes = @(
        'int', 'int16', 'int32', 'int64', 'long', 'uint32', 'uint64', 'byte', 'double', 'single', 'float', 'decimal',
        'string', 'char', 'bool', 'boolean', 'datetime', 'timespan', 'math', 'regex', 'object', 'array', 'void', 'dbnull',
        'hashtable', 'ordered', 'pscustomobject',
        'object[]', 'object[,]', 'string[]', 'string[,]', 'int[]', 'int[,]', 'long[]', 'double[]', 'double[,]', 'bool[]', 'char[]', 'datetime[]',
        'collections.arraylist', 'collections.hashtable', 'collections.generic.list[object]', 'collections.generic.list[string]',
        'collections.generic.list[int]', 'collections.generic.list[double]', 'text.stringbuilder', 'globalization.cultureinfo',
        'exception', 'runtime.interopservices.comexception', 'management.automation.runtimeexception',
        'management.automation.methodinvocationexception', 'management.automation.setvalueinvocationexception',
        'runtime.interopservices.marshal', 'type', 'reflection.missing', 'ref')
    # types used only for one static member ([Type]::Missing for an omitted COM argument; Marshal only to release COM objects)
    $LimitedStatics = @{
        'runtime.interopservices.marshal' = @('releasecomobject', 'finalreleasecomobject')
        'type' = @('missing')
        'reflection.missing' = @('value')
    }
    # $PSDefaultParameterValues would give commands parameters the check does not see (ForEach-Object:MemberName ...)
    $DeniedVariables = @('executioncontext', 'host', 'pscmdlet', 'myinvocation', 'pshome', 'profile', 'shellid', 'psscriptroot', 'pscommandpath', 'input',
        'psdefaultparametervalues', 'psmoduleautoloadingpreference', 'pssessionoption', 'pssessionconfigurationname', 'pssessionapplicationname')
    # Excel collections: used directly in one expression ($wb.Worksheets.Item('x'), foreach ($s in $wb.Worksheets), $wb.Worksheets | ...),
    # never kept in a variable, so that their Add / Create can be checked where they are called
    $Collections = @('workbooks', 'sheets', 'worksheets', 'charts', 'listobjects', 'pivotcaches')

    # every member a script may use (lower case). Members of the Excel object model for editing, and of .NET strings, numbers,
    # dates, arrays, lists and exceptions. Not here (refused): open, save, close, quit, run, macros, VBA, events, add-ins,
    # files, pictures from files, printing, mail, links, connections, queries, web, command bars, DDE, Evaluate, ActiveX, windows.
    $AllowedMembers = @(
        # objects and collections
        'application', 'parent', 'workbooks', 'worksheets', 'sheets', 'charts', 'item', 'count', 'name', 'codename', 'index',
        'fullname', 'readonly', 'worksheetfunction', 'calculate', 'calculatefull', 'intersect', 'union',
        'cutcopymode', 'version', 'decimalseparator', 'thousandsseparator', 'usesystemseparators', 'international',
        # sheets
        'cells', 'range', 'rows', 'columns', 'usedrange', 'visible', 'delete', 'copy', 'move', 'add', 'select', 'activate', 'tab',
        'autofiltermode', 'showalldata', 'filtermode', 'standardwidth', 'standardheight', 'type', 'next', 'previous',
        'displaypagebreaks', 'protectcontents', 'protectstructure', 'multiuserediting', 'prefixcharacter', 'date1904', 'pivottable',
        # ranges
        'value', 'value2', 'formula', 'formular1c1', 'formulaarray', 'formula2', 'formula2r1c1', 'formulalocal', 'text',
        'address', 'addresslocal', 'row', 'column', 'offset', 'resize', 'end', 'currentregion', 'currentarray', 'entirerow',
        'entirecolumn', 'areas', 'clear', 'clearcontents', 'clearformats', 'clearcomments', 'clearnotes', 'clearoutline', 'insert',
        'cut', 'autofit', 'merge', 'unmerge', 'mergecells', 'mergearea', 'horizontalalignment', 'verticalalignment',
        'wraptext', 'orientation', 'indentlevel', 'shrinktofit', 'readingorder', 'addindent', 'numberformat', 'numberformatlocal',
        'font', 'interior', 'borders', 'borderaround', 'columnwidth', 'rowheight', 'hidden', 'sort', 'autofilter', 'find',
        'findnext', 'findprevious', 'replace', 'specialcells', 'filldown', 'fillright', 'fillup', 'fillleft', 'autofill',
        'hasformula', 'hasarray', 'formatconditions', 'validation', 'addcomment', 'addcommentthreaded', 'comment',
        'commentthreaded', 'comments', 'notetext', 'worksheet', 'characters', 'removeduplicates', 'texttocolumns', 'subtotal',
        'removesubtotal', 'group', 'ungroup', 'outlinelevel', 'showdetail', 'locked', 'formulahidden', 'style', 'styles', 'top',
        'left', 'width', 'height', 'displayformat', 'precedents', 'dependents', 'directprecedents', 'directdependents', 'spillingtorange',
        'hasspill', 'spillparent', 'count', 'countlarge', 'cells', 'show', 'dirty', 'table', 'goalseek', 'calculate',
        'dataseries', 'createnames', 'applynames', 'listheaderrows', 'iserror', 'errors', 'phonetic', 'previous', 'next',
        # font, fill, borders
        'size', 'bold', 'italic', 'underline', 'color', 'colorindex', 'strikethrough', 'superscript', 'subscript', 'themecolor',
        'tintandshade', 'fontstyle', 'themefont', 'pattern', 'patterncolor', 'patterncolorindex', 'patternthemecolor', 'gradient',
        'linestyle', 'weight', 'rgb', 'forecolor', 'backcolor', 'transparency', 'solid', 'line', 'fill', 'dashstyle', 'visible',
        # conditional formats, validation, sort, filter
        'addcolorscale', 'adddatabar', 'addiconsetcondition', 'addtop10', 'addaboveaverage', 'adduniquevalues', 'formula1',
        'formula2', 'operator', 'stopiftrue', 'priority', 'appliesto', 'modifyappliestorange', 'setfirstpriority', 'setlastpriority',
        'colorscalecriteria', 'iconset', 'iconsets', 'iconcriteria', 'barcolor', 'modify', 'ignoreblank', 'incelldropdown',
        'inputtitle', 'inputmessage', 'errortitle', 'errormessage', 'showinput', 'showerror', 'alertstyle', 'sortfields',
        'setrange', 'header', 'matchcase', 'sortmethod', 'apply', 'key', 'order', 'dataoption', 'sorton', 'customorder',
        'filters', 'criteria1', 'criteria2', 'on', 'autofilter', 'range',
        # tables
        'listrows', 'listcolumns', 'databodyrange', 'headerrowrange', 'totalsrowrange', 'showtotals', 'tablestyle',
        'listobjects', 'showautofilter', 'showheaders', 'showtablestyle*', 'unlist', 'totalscalculation', 'listobject', 'tablestyles',
        # charts and shapes
        'chartobjects', 'chart', 'setsourcedata', 'charttype', 'hastitle', 'charttitle', 'seriescollection', 'newseries',
        'values', 'xvalues', 'axes', 'hasaxis', 'axistitle', 'hasdatalabels', 'datalabels', 'legend', 'haslegend', 'position',
        'format', 'plotarea', 'chartarea', 'chartstyle', 'setelement', 'applylayout', 'minimumscale', 'maximumscale',
        'minimumscaleisauto', 'maximumscaleisauto', 'majorunit', 'minorunit', 'ticklabels', 'hasmajorgridlines', 'hasminorgridlines',
        'majorgridlines', 'charttype', 'axisgroup', 'plotby', 'markerstyle', 'markersize', 'smooth', 'points', 'barshape',
        'gapwidth', 'overlap', 'chartgroups', 'showvalue', 'showcategoryname', 'showpercentage', 'showseriesname',
        'numberformatlinked', 'shapes', 'addchart2', 'addshape', 'addtextbox', 'addline', 'addconnector', 'textframe',
        'textframe2', 'textrange', 'shaperange', 'rotation', 'placement', 'zorder', 'bottomrightcell', 'topleftcell',
        'alternativetext', 'autoshapetype', 'horizontalalignment', 'verticalalignment', 'marginleft', 'marginright',
        'margintop', 'marginbottom', 'autosize', 'lockaspectratio', 'scaleheight', 'scalewidth', 'incrementleft', 'incrementtop',
        # pivot tables (from a range only)
        'pivottables', 'pivotcaches', 'create', 'createpivottable', 'pivotfields', 'adddatafield', 'function', 'refreshtable',
        'pivotcache', 'refresh', 'rowgrand', 'columngrand', 'tablerange1', 'tablerange2', 'datafields', 'rowfields',
        'columnfields', 'pagefields', 'pivotitems', 'caption', 'subtotals', 'rowaxislayout', 'manualupdate', 'clearallfilters',
        'currentpage', 'sourcedata', 'datarange', 'labelrange', 'pivotfilters', 'compactlayoutrowheader', 'repeatalllabels',
        # names, page setup, hyperlinks (created, not followed)
        'names', 'refersto', 'referstorange', 'referstolocal', 'hyperlinks', 'subaddress', 'screentip', 'texttodisplay',
        'pagesetup', 'printarea', 'printtitlerows', 'printtitlecolumns', 'zoom', 'fittopageswide', 'fittopagestall',
        'leftmargin', 'rightmargin', 'topmargin', 'bottommargin', 'headermargin', 'footermargin', 'centerhorizontally',
        'centervertically', 'papersize', 'centerheader', 'leftheader', 'rightheader', 'centerfooter', 'leftfooter', 'rightfooter',
        'printgridlines', 'printheadings', 'hpagebreaks', 'vpagebreaks', 'resetallpagebreaks',
        # worksheet functions
        'sum', 'sumif', 'sumifs', 'sumproduct', 'average', 'averageif', 'averageifs', 'counta', 'countif', 'countifs', 'countblank',
        'max', 'min', 'maxifs', 'minifs', 'match', 'xmatch', 'vlookup', 'hlookup', 'xlookup', 'lookup', 'round', 'roundup',
        'rounddown', 'trim', 'large', 'small', 'rank', 'rank_eq', 'median', 'mode', 'stdev', 'stdev_s', 'stdev_p', 'var', 'and',
        'or', 'isna', 'iserr', 'isnumber', 'istext', 'unique', 'filter', 'transpose', 'textjoin', 'concat', 'eomonth', 'edate',
        'networkdays', 'workday', 'weekday', 'weeknum', 'percentile', 'quartile', 'frequency', 'proper', 'clean', 'substitute',
        # .NET: strings, numbers, dates, arrays, lists, hashtables, regex, exceptions, PowerShell results
        'length', 'substring', 'trimstart', 'trimend', 'split', 'toupper', 'tolower', 'toupperinvariant', 'tolowerinvariant',
        'indexof', 'lastindexof', 'contains', 'startswith', 'endswith', 'padleft', 'padright', 'tostring', 'equals', 'compareto',
        'normalize', 'tochararray', 'chars', 'keys', 'containskey', 'containsvalue', 'remove', 'removeat', 'removerange',
        'append', 'appendline', 'appendformat', 'addrange', 'toarray', 'reverse', 'getlength', 'getupperbound', 'getlowerbound',
        'getvalue', 'setvalue', 'floor', 'ceiling', 'abs', 'pow', 'sqrt', 'truncate', 'sign', 'now', 'today', 'adddays', 'addmonths',
        'addyears', 'addhours', 'addminutes', 'addseconds', 'year', 'month', 'day', 'hour', 'minute', 'second', 'millisecond',
        'dayofweek', 'dayofyear', 'date', 'timeofday', 'toshortdatestring', 'tolongdatestring', 'toshorttimestring', 'tooadate',
        'fromoadate', 'daysinmonth', 'isleapyear', 'parse', 'tryparse', 'parseexact', 'join', 'isnullorempty',
        'isnullorwhitespace', 'empty', 'totaldays', 'totalhours', 'totalminutes', 'totalseconds', 'days', 'hours', 'minutes',
        'seconds', 'invariantculture', 'currentculture', 'releasecomobject', 'finalreleasecomobject', 'matches', 'ismatch',
        'groups', 'success', 'escape', 'foreach', 'where', 'message', 'exception', 'innerexception', 'hresult', 'errorcode',
        'missing', 'sum', 'average', 'maximum', 'minimum', 'property', 'group', 'new', 'pi', 'e', 'maxvalue', 'minvalue',
        'getenumerator', 'fullseriescollection', 'applydatalabels', 'location')
    # names refused even if they come into the list by mistake (and to explain the refusal). Exact names, and prefixes ending with *
    $DeniedMembers = @(
        'invoke*', 'gettype', 'getmethod*', 'getproperty', 'getproperties', 'getfield*', 'getmember*', 'getconstructor*',
        'createinstance', 'createdelegate', 'load*', 'psobject', 'psbase', 'psextended', 'psadapted', 'ast', 'module',
        'dynamicinvoke', 'gettypes', 'declared*', 'assembly', 'targetsite', 'declaringtype', 'reflectedtype', 'basetype',
        'open*', 'save*', 'close', 'quit', 'changefileaccess', 'run', 'runautomacros', 'executeexcel4macro',
        'displayalerts', 'automationsecurity', 'enableevents', 'ontime', 'onkey', 'vbe', 'vbproject', 'vbcomponents', 'sendkeys',
        'followhyperlink', 'follow', 'export*', 'printout', 'registerxll', 'addins', 'addins2', 'comaddins', 'querytable*',
        'connection*', 'webservice', 'evaluate', 'commandbars', 'executemso', 'dde*', 'activatemicrosoftapp', 'publishobjects',
        'altstartuppath', 'startuppath', 'addpicture*', 'oleobjects', 'applytheme', 'applycharttemplate', 'mergeworkbook',
        'xmlmap*', 'xmlimport*', 'queries', 'dialogs', 'windows', 'recentfiles', 'autorecover', 'model', 'slicercaches', 'customxmlparts',
        'filedialog', 'consolidate', 'protect', 'unprotect', 'password', 'writepassword',
        # Excel-wide settings stay changed for the user when a script stops halfway (the wrapper cannot restore them)
        'screenupdating', 'calculation', 'enablecalculation', 'interactive', 'cursor',
        'paste', 'pastespecial')   # paste reads the user's clipboard (text or files copied in other programs)

    function InList([string]$name, [string[]]$list) {
        # a leading _ (hidden COM members such as _SaveAs) is not a way around the lists
        $lower = $name.ToLowerInvariant().TrimStart('_')
        foreach ($item in $list) {
            if ($item.EndsWith('*')) { if ($lower.StartsWith($item.TrimEnd('*'))) { return $true } }
            elseif ($lower -eq $item) { return $true }
        }
        return $false
    }
    function TypeKey([string]$fullName) {
        return (($fullName.ToLowerInvariant() -replace '\s', '') -replace 'system\.', '')
    }
    function At($ast) {
        $text = $ast.Extent.Text -replace '\s+', ' '
        return 'line ' + $ast.Extent.StartLineNumber + ' (' + $text.Substring(0, [Math]::Min(60, $text.Length)) + '): '
    }
    function MemberProblem([string]$name, [bool]$read) {
        if (InList $name $DeniedMembers) { return 'the member ' + $name + ' is not allowed (open, save, close, macros, files, network, reflection ...)' }
        if (-not (InList $name $AllowedMembers)) { return 'the member ' + $name + ' is not in the list of members a script may use (Excel cells, formats, sheets, tables, charts, pivot tables; strings, numbers, dates, lists). If the work needs it, tell the user it cannot be done by a script' }
        return $null
    }
    # a property name given to Select-Object and the like: written directly, no wildcard, in the list
    function PropertyProblem($element) {
        if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            if ($element.Value -match '[\*\?\[\]]') { return 'a property name must not have a wildcard' }
            return MemberProblem $element.Value $false
        }
        if ($element -is [System.Management.Automation.Language.ArrayLiteralAst]) {
            foreach ($item in $element.Elements) { $problem = PropertyProblem $item; if ($problem) { return $problem } }
            return $null
        }
        if ($element -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { return $null }
        if ($element -is [System.Management.Automation.Language.ConstantExpressionAst]) { return $null }
        if ($element -is [System.Management.Automation.Language.HashtableAst]) {
            # a calculated property: @{ Name = 'label'; Expression = { ... } } (the expression given as a string is a property name)
            foreach ($pair in $element.KeyValuePairs) {
                $key = if ($pair.Item1 -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $pair.Item1.Value.ToLowerInvariant() } else { '' }
                if (-not $key.StartsWith('e')) { continue }
                $value = $pair.Item2
                if ($value -is [System.Management.Automation.Language.PipelineAst] -and $value.PipelineElements.Count -eq 1 -and
                    $value.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) {
                    $inner = $value.PipelineElements[0].Expression
                    if ($inner -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { continue }
                    if ($inner -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $problem = PropertyProblem $inner; if ($problem) { return $problem }; continue }
                }
                return 'the expression of a calculated property must be a script block { } or a property name'
            }
            return $null
        }
        return 'a property name must be written directly (not built from strings or variables)'
    }
    # the full name of a parameter of a command (it may be shortened), or $null when the command does not take it
    function ParameterOf([string]$command, [string]$given) {
        $lower = $given.ToLowerInvariant()
        $names = $CommandParameters[$command]
        if ($names -contains $lower) { return $lower }
        $matched = @($names | Where-Object { $_.StartsWith($lower) })
        if ($lower.Length -gt 0 -and $matched.Count -eq 1) { return $matched[0] }
        return $null
    }
    function TypeProblem([string]$fullName, [bool]$static) {
        $key = TypeKey $fullName
        if ($AllowedTypes -notcontains $key) { return 'the type [' + $fullName + '] is not allowed' }
        if ($LimitedStatics.ContainsKey($key) -and -not $static) { return 'the type [' + $fullName + '] is only for ' + ($LimitedStatics[$key] -join ', ') }
        return $null
    }

    $tokens = $null
    $errors = $null
    $root = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) { return 'line ' + $errors[0].Extent.StartLineNumber + ': the script does not parse: ' + $errors[0].Message }

    foreach ($ast in $root.FindAll({ $true }, $true)) {
        $kind = $ast.GetType().Name
        if ($kind -eq 'FileRedirectionAst' -and $ast.Location -is [System.Management.Automation.Language.VariableExpressionAst] -and $ast.Location.VariablePath.UserPath -eq 'null') {
            continue   # 2>$null throws the output away (no file)
        }
        if ($kind -eq 'SwitchStatementAst' -and ([string]$ast.Flags) -match 'File') { return (At $ast) + 'switch -File reads a file: not allowed' }
        if ($kind -eq 'HashtableAst') {
            # keys written directly: a built key hides what a calculated property reads
            foreach ($pair in $ast.KeyValuePairs) {
                if (-not ($pair.Item1 -is [System.Management.Automation.Language.StringConstantExpressionAst])) { return (At $ast) + 'hashtable keys must be written directly' }
            }
        }
        if ($AllowedAsts -notcontains $kind) {
            if ($kind -eq 'FileRedirectionAst') { return (At $ast) + 'redirection to a file (>) is not allowed' }
            if ($kind -eq 'FunctionDefinitionAst') { return (At $ast) + 'functions are not allowed (write the steps in place, or use a script block in a loop)' }
            if ($kind -eq 'ExitStatementAst') { return (At $ast) + 'exit is not allowed (use throw to stop with an error)' }
            if ($kind -eq 'AttributeAst') { return (At $ast) + 'attributes are not allowed' }
            if ($kind -eq 'ParamBlockAst' -or $kind -eq 'ParameterAst') { return (At $ast) + 'param is not allowed' }
            return (At $ast) + 'this form (' + $kind + ') is not allowed'
        }

        if ($kind -eq 'CommandAst') {
            if ($ast.InvocationOperator -ne 'Unknown') { return (At $ast) + 'calling with & or . is not allowed' }
            $first = $ast.CommandElements[0]
            if (-not ($first -is [System.Management.Automation.Language.StringConstantExpressionAst]) -or $first.StringConstantType -ne 'BareWord') {
                return (At $ast) + 'a command must be named directly'
            }
            $name = $first.Value.ToLowerInvariant()
            if ($AllowedCommands -notcontains $name) {
                return (At $ast) + 'the command ' + $first.Value + ' is not allowed (only Write-Output, ForEach-Object { }, Where-Object { }, Select-Object, Sort-Object, Measure-Object, Group-Object, Format-Table, Format-List, Out-String, Out-Null, Get-Date, Start-Sleep, and New-Object of an allowed type)'
            }
            $rest = @($ast.CommandElements | Select-Object -Skip 1)
            foreach ($element in $rest) {
                if ($element -is [System.Management.Automation.Language.VariableExpressionAst] -and $element.Splatted) { return (At $ast) + 'splatting (@args) is not allowed' }
                if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and -not (ParameterOf $name $element.ParameterName)) {
                    return (At $ast) + $first.Value + ' does not take -' + $element.ParameterName
                }
            }
            if ('foreach-object', '%', 'foreach', 'where-object', '?', 'where' -contains $name) {
                # only script blocks, at least one: a name (ForEach-Object Save) would call a method by a string
                if (-not ($rest | Where-Object { $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -or ($_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.Argument -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) })) {
                    return (At $ast) + $first.Value + ' needs a script block { }'
                }
                foreach ($element in $rest) {
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                        if ($element.Argument -and -not ($element.Argument -is [System.Management.Automation.Language.ScriptBlockExpressionAst])) { return (At $ast) + $first.Value + ' takes only script blocks { }' }
                    } elseif (-not ($element -is [System.Management.Automation.Language.ScriptBlockExpressionAst])) {
                        return (At $ast) + $first.Value + ' takes only script blocks { }'
                    }
                }
            }
            if ($PropertyCommands -contains $name) {
                # property names are read by name: written directly, no wildcard, in the member list
                $propertyNext = $false
                foreach ($element in $rest) {
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                        $parameter = ParameterOf $name $element.ParameterName
                        $takesName = 'property', 'expandproperty', 'excludeproperty', 'groupby' -contains $parameter
                        if ($element.Argument) {
                            if ($takesName) { $problem = PropertyProblem $element.Argument; if ($problem) { return (At $ast) + $problem } }
                            $propertyNext = $false
                        } else {
                            $propertyNext = $takesName
                            if ('first', 'last', 'skip', 'skiplast', 'index', 'inputobject', 'top', 'bottom' -contains $parameter) { $propertyNext = $null }
                        }
                    } elseif ($null -eq $propertyNext) {
                        # the value of -First and the like
                        $propertyNext = $false
                    } else {
                        $problem = PropertyProblem $element
                        if ($problem) { return (At $ast) + $problem }
                        $propertyNext = $false
                    }
                }
            }
            if ($name -eq 'new-object') {
                # New-Object <allowed type> [arguments]; never -ComObject or -Property
                $typeGiven = $false
                for ($i = 0; $i -lt $rest.Count; $i++) {
                    $element = $rest[$i]
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                        $parameter = ParameterOf $name $element.ParameterName
                        if ($parameter -eq 'typename') {
                            $value = if ($element.Argument) { $element.Argument } elseif ($i + 1 -lt $rest.Count) { $rest[$i + 1] } else { $null }
                            if (-not ($value -is [System.Management.Automation.Language.StringConstantExpressionAst])) { return (At $ast) + 'New-Object needs the type name written directly' }
                            $problem = TypeProblem $value.Value $false
                            if ($problem) { return (At $ast) + $problem }
                            $typeGiven = $true
                            if (-not $element.Argument) { $i++ }
                        }
                    } elseif (-not $typeGiven) {
                        if (-not ($element -is [System.Management.Automation.Language.StringConstantExpressionAst])) { return (At $ast) + 'New-Object needs the type name written directly' }
                        $problem = TypeProblem $element.Value $false
                        if ($problem) { return (At $ast) + $problem }
                        $typeGiven = $true
                    }
                }
                if (-not $typeGiven) { return (At $ast) + 'New-Object needs an allowed type name' }
            }
        }
        elseif ($kind -eq 'VariableExpressionAst') {
            $path = $ast.VariablePath
            if ($path.IsDriveQualified) { return (At $ast) + 'drive variables ($env:, ${C:\...}, $function: ...) are not allowed' }
            # the name without a scope ($global:host -> host); UnqualifiedPath is not public in PowerShell 5.1
            $bare = ([string]$path.UserPath -replace '^[^:]*:', '').ToLowerInvariant()
            if ($DeniedVariables -contains $bare) { return (At $ast) + 'the variable $' + $path.UserPath + ' is not allowed' }
        }
        elseif ($kind -eq 'MemberExpressionAst' -or $kind -eq 'InvokeMemberExpressionAst') {
            if (-not ($ast.Member -is [System.Management.Automation.Language.StringConstantExpressionAst])) { return (At $ast) + 'a member must be named directly (not by a variable or an expression)' }
            $member = $ast.Member.Value
            $lower = $member.ToLowerInvariant()
            $problem = MemberProblem $member $false
            if ($problem) { return (At $ast) + $problem }
            if ($ast.Static) {
                if (-not ($ast.Expression -is [System.Management.Automation.Language.TypeExpressionAst])) { return (At $ast) + 'static members only on a type written directly' }
                $key = TypeKey $ast.Expression.TypeName.FullName
                if ($LimitedStatics.ContainsKey($key) -and $LimitedStatics[$key] -notcontains $lower) { return (At $ast) + 'the member ' + $member + ' of this type is not allowed' }
            }
            if ($Collections -contains $lower) {
                # the collection is used right here: a member of it, the list of a foreach, or the start of a pipeline
                $parent = $ast.Parent
                $direct = ($parent -is [System.Management.Automation.Language.MemberExpressionAst] -and $parent.Expression -eq $ast) -or
                          ($parent -is [System.Management.Automation.Language.IndexExpressionAst] -and $parent.Target -eq $ast)
                if (-not $direct -and $parent -is [System.Management.Automation.Language.CommandExpressionAst] -and $parent.Parent -is [System.Management.Automation.Language.PipelineAst]) {
                    $pipeline = $parent.Parent
                    if ($pipeline.PipelineElements.Count -gt 1 -and $pipeline.PipelineElements[0] -eq $parent) { $direct = $true }
                    if ($pipeline.Parent -is [System.Management.Automation.Language.ForEachStatementAst] -and $pipeline.Parent.Condition -eq $pipeline) { $direct = $true }
                }
                if (-not $direct) { return (At $ast) + $member + ' must be used in the same expression ($wb.' + $member + '.Item(...), foreach ($x in $wb.' + $member + ')), not kept in a variable' }
                if ($parent -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $parent.Expression -eq $ast) {
                    $call = $parent.Member.Value.ToLowerInvariant().TrimStart('_')
                    $arguments = @($parent.Arguments)
                    if ($lower -eq 'workbooks' -and $call -eq 'add' -and $arguments.Count -gt 0) { return (At $parent) + 'Workbooks.Add with an argument (a file) is not allowed' }
                    if (('sheets', 'worksheets', 'charts' -contains $lower) -and $call -eq 'add' -and $arguments.Count -gt 3) { return (At $parent) + $member + '.Add with a template (the 4th argument) is not allowed' }
                    if (($lower -eq 'listobjects' -and $call -eq 'add') -or ($lower -eq 'pivotcaches' -and ('create', 'add' -contains $call))) {
                        # from a range only (1 = xlSrcRange / xlDatabase), never from a connection or a query
                        if ($arguments.Count -eq 0 -or -not ($arguments[0] -is [System.Management.Automation.Language.ConstantExpressionAst]) -or $arguments[0].Value -ne 1) {
                            return (At $parent) + $member + ' must come from a range: give 1 as the first argument'
                        }
                        # the source as a range object ($ws.Range('A1:D20')), never text (text can name a closed workbook by its path)
                        # (an expression that ends with a member returning a range; .Value2 or .Text would give text written in a cell)
                        $source = if ($arguments.Count -gt 1) { $arguments[1] } else { $null }
                        $isRange = $source -is [System.Management.Automation.Language.MemberExpressionAst] -and $source.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                                   ('range', 'cells', 'currentregion', 'usedrange', 'resize', 'offset', 'entirerow', 'entirecolumn', 'databodyrange', 'rows', 'columns', 'areas', 'mergearea' -contains $source.Member.Value.ToLowerInvariant().TrimStart('_'))
                        if ($arguments.Count -gt 1 -and -not $isRange) {
                            return (At $parent) + $member + ' takes its source as a range object such as $ws.Range(''A1:D20''), not as text or a variable'
                        }
                    }
                }
                if ($parent -is [System.Management.Automation.Language.IndexExpressionAst] -or ($parent -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $parent.Expression -eq $ast -and $parent.Member.Value.TrimStart('_') -eq 'Item')) {
                    # one item by a name or a number: an array would return a collection (Sheets) that a variable could keep
                    $index = if ($parent -is [System.Management.Automation.Language.IndexExpressionAst]) { @($parent.Index) } else { @($parent.Arguments) }
                    foreach ($argument in $index) {
                        $single = $argument -is [System.Management.Automation.Language.ConstantExpressionAst] -or $argument -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -or
                                  ($argument -is [System.Management.Automation.Language.ConvertExpressionAst] -and ('int', 'int32', 'string', 'long', 'double' -contains (TypeKey $argument.Type.TypeName.FullName)))
                        if (-not $single -or $index.Count -ne 1) { return (At $parent) + $member + '.Item takes one name or number written directly, or a variable as [string]$name or [int]$i' }
                    }
                }
            }
            # .ForEach('Name') / .Where('Name') call a method by a string
            if ($kind -eq 'InvokeMemberExpressionAst' -and ('foreach', 'where' -contains $lower) -and $ast.Arguments) {
                foreach ($argument in $ast.Arguments) {
                    if (-not ($argument -is [System.Management.Automation.Language.ScriptBlockExpressionAst])) { return (At $ast) + '.' + $member + '() takes only script blocks { }' }
                }
            }
        }
        elseif ($kind -eq 'TypeExpressionAst') {
            $parent = $ast.Parent
            $static = ($parent -is [System.Management.Automation.Language.MemberExpressionAst] -and $parent.Static -and $parent.Expression -eq $ast)
            $problem = TypeProblem $ast.TypeName.FullName $static
            if ($problem) { return (At $ast) + $problem }
        }
        elseif ($kind -eq 'TypeConstraintAst') {
            $problem = TypeProblem $ast.TypeName.FullName $false
            if ($problem) { return (At $ast) + $problem }
        }
        elseif ($kind -eq 'BinaryExpressionAst') {
            # -as / -is / -isnot convert or test against a type: only a type written directly (checked above), never a string
            if ('As', 'Is', 'IsNot' -contains [string]$ast.Operator -and -not ($ast.Right -is [System.Management.Automation.Language.TypeExpressionAst])) {
                return (At $ast) + '-as / -is need a type written directly ([int]), not a string'
            }
        }
    }
    return $null
}
