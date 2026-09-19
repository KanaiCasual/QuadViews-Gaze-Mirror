# Opens an MSI read-only and prints what it would do: registry rows (in table order), launch conditions, registry
# searches, the OBS plugin components' flags, custom actions, and a file summary. Installs nothing.
param([Parameter(Mandatory)][string]$Path)

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path $Path).Path, 0))

function Invoke-MsiQuery([string]$Sql, [int]$Columns) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Sql))
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rows = New-Object System.Collections.Generic.List[object]
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $row = New-Object string[] $Columns
        for ($i = 1; $i -le $Columns; $i++) {
            $row[$i - 1] = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, [object[]]@([int]$i))
        }
        $rows.Add($row)
    }
    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    return , $rows
}

'--- Registry table (in the table''s own row order)'
foreach ($r in (Invoke-MsiQuery 'SELECT `Registry`, `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`' 6)) {
    '  {0,-40} root={1}  {2} :: {3} = {4}   [{5}]' -f $r[0], $r[1], $r[2], $r[3], $r[4], $r[5]
}
'--- Launch conditions'
foreach ($r in (Invoke-MsiQuery 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2)) { '  require: {0}' -f $r[0]; '     else: {0}' -f $r[1] }
'--- Registry searches'
foreach ($r in (Invoke-MsiQuery 'SELECT `Signature_`, `Root`, `Key`, `Name`, `Type` FROM `RegLocator`' 5)) {
    '  {0,-24} root={1} {2} | name={3} | type={4}' -f $r[0], $r[1], $r[2], $r[3], $r[4]
}
'--- Conditioned components (attributes: 16 = permanent, 128 = never overwrite, 256 = 64-bit)'
foreach ($r in (Invoke-MsiQuery 'SELECT `Component`, `Attributes`, `Condition`, `Directory_` FROM `Component`' 4)) {
    if ($r[2]) {
        $a = [int]$r[1]
        '  {0,-20} permanent={1} neverOverwrite={2} x64={3} condition={4} dir={5}' -f $r[0], [bool]($a -band 16), [bool]($a -band 128), [bool]($a -band 256), $r[2], $r[3]
    }
}
'--- Custom actions'
$actions = Invoke-MsiQuery 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' 4
foreach ($r in $actions) { '  {0,-34} type={1,-5} source={2} target={3}' -f $r[0], $r[1], $r[2], $r[3] }
"  ($($actions.Count) rows)"
'--- Files'
$files = Invoke-MsiQuery 'SELECT `FileName` FROM `File`' 1
$names = $files | ForEach-Object { ($_[0] -split '\|')[-1] }
"  $($files.Count) files. Key ones: " + (($names | Where-Object { $_ -match 'quad_views_foveated\.dll|OBSMirror\.(dll|json)|openxr-api-layer\.json|win-openxr\.dll|^QuadViewsGazeMirror\.(exe|dll)$' }) -join ', ')
'--- Shortcuts'
foreach ($r in (Invoke-MsiQuery 'SELECT `Name`, `Target` FROM `Shortcut`' 2)) { '  {0} -> {1}' -f $r[0], $r[1] }
'--- Properties'
foreach ($r in (Invoke-MsiQuery 'SELECT `Property`, `Value` FROM `Property`' 2)) {
    if ($r[0] -in 'ProductName', 'ProductVersion', 'Manufacturer', 'ALLUSERS', 'UpgradeCode') { '  {0} = {1}' -f $r[0], $r[1] }
}
