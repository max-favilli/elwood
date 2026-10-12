param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$StorageAccount,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$')][string]$StorageNamespace = 'elwood',
    [ValidateSet('guarded','files-25m')][string]$CapacityProfile = 'guarded',
    [string]$ControlContainer = 'control', [string]$StateContainer = 'state',
    [string]$DocumentContainer = 'documents', [string]$OutputContainer = 'outputs'
)
$ErrorActionPreference = 'Stop'
# Explicit operator bootstrap, BEFORE starting workers. Never use to repair lost ledgers.
function Invoke-Azure([string[]]$Arguments) {
    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed ($LASTEXITCODE); initialization stopped. Inspect the reported error." }
    return $result
}
$common = @('--account-name', $StorageAccount, '--subscription', $SubscriptionId, '--auth-mode', 'login', '--only-show-errors')
foreach ($container in @($ControlContainer, $StateContainer, $DocumentContainer, $OutputContainer)) {
    $existing = Invoke-Azure (@('storage','blob','list','--container-name',$container,'--prefix',"$StorageNamespace/",'--include','dv','--num-results','1','-o','json') + $common)
    if (@($existing | ConvertFrom-Json).Count -ne 0) {
        throw "Namespace already has data/versions in $container. Restore existing control data or choose a fresh namespace; do not reinitialize."
    }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('elwood-retention-init-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $snapshot = Join-Path $scratch 'empty.json'
    [IO.File]::WriteAllText($snapshot, '{"formatVersion":1,"entries":{},"lastSweepAt":null,"windowStartedAt":"0001-01-01T00:00:00+00:00","windowWrites":0,"windowBytes":0}')
    $names = if ($CapacityProfile -eq 'files-25m') { (0..63 | ForEach-Object { 'retention/{0:D2}.json' -f $_ }) + @('retention/health.json') } else { @('output-retention.json') }
    foreach ($name in $names) {
        Invoke-Azure (@('storage','blob','upload','--container-name',$ControlContainer,'--name',"$StorageNamespace/$name",'--file',$snapshot,'--if-none-match','*','--content-type','application/json','-o','none') + $common)
        Write-Output "Initialized $StorageNamespace/$name"
    }
    $layout = Join-Path $scratch 'layout.txt'
    [IO.File]::WriteAllText($layout, $(if ($CapacityProfile -eq 'files-25m') { 'partitions64-v1' } else { 'single-v1' }))
    Invoke-Azure (@('storage','blob','upload','--container-name',$ControlContainer,'--name',"$StorageNamespace/retention-layout.txt",'--file',$layout,'--if-none-match','*','--content-type','text/plain','-o','none') + $common)
    Write-Output 'Initialization complete. Start workers and require a successful retention sweep before readiness.'
} finally {
    # Known files in this invocation's scratch directory only; no recursive deletion.
    foreach ($name in @('empty.json','layout.txt')) { Remove-Item -LiteralPath (Join-Path $scratch $name) -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $scratch
}
