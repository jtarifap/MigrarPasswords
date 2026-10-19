param([Parameter(Mandatory = $true)][string] $ScratchRoot)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ScratchRoot -PathType Container)) { throw 'ScratchRoot must exist.' }
$folder = Join-Path $ScratchRoot ('MigrarPasswords-standalone-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
foreach ($name in @('MigrarPasswords.exe', 'BCrypt.Net-Next.dll', 'THIRD-PARTY.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\' + $name)) -Destination (Join-Path $folder $name)
}
# Only synthetic configuration is written to the temporary directory, never real credentials.
$config = New-Object System.Xml.XmlDocument
$config.LoadXml('<configuration><connectionStrings><clear/><add name="Users_ConnectionString" connectionString="Data Source=standalone-test.invalid;Initial Catalog=VVTecnics;Integrated Security=True" providerName="System.Data.SqlClient"/></connectionStrings><startup><supportedRuntime version="v2.0.50727"/></startup></configuration>')
$configPath = Join-Path $folder 'MigrarPasswords.exe.config'
$config.Save($configPath)

function Invoke-Offline([string] $argument) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $folder 'MigrarPasswords.exe'
    $info.Arguments = $argument
    $info.WorkingDirectory = $ScratchRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        # Never confirm migration. Even a failed config guard cannot reach SQL.
        if ($argument -eq '--apply') { $process.StandardInput.WriteLine('CANCELAR') }
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return @{ Code = $process.ExitCode; Output = $stdout; Error = $stderr }
    } finally { $process.Dispose() }
}

$test = Invoke-Offline '--self-test'
if ($test.Code -ne 0 -or $test.Output -notmatch 'PASS:') { throw 'Standalone cryptographic self-test failed.' }
if ($test.Output -notmatch 'Pulse INTRO para cerrar') { throw 'Missing final pause on success.' }
$test = Invoke-Offline '--apply'
if ($test.Code -ne 1 -or $test.Output -notmatch 'standalone-test.invalid' -or $test.Output -notmatch 'Cancelado. No se ha abierto ninguna conexion.') {
    throw 'Executable did not read its adjacent configuration or did not cancel safely.'
}
$config.configuration.connectionStrings.add.SetAttribute('connectionString', 'Data Source=standalone-test.invalid;Initial Catalog=VVTecnics;Integrated Security=True')
$config.Save($configPath)
$test = Invoke-Offline '--apply'
if ($test.Code -ne 1 -or $test.Error -notmatch 'InvalidOperationException' -or $test.Output -match 'Servidor configurado') {
    throw 'Production database was not rejected before confirmation.'
}
$node = $config.SelectSingleNode('/configuration/connectionStrings/add')
[void]$node.ParentNode.RemoveChild($node)
$config.Save($configPath)
$test = Invoke-Offline '--apply'
if ($test.Code -ne 1 -or $test.Error -notmatch 'InvalidOperationException' -or $test.Output -match 'Servidor configurado') {
    throw 'Missing local connection was not rejected.'
}
if ($test.Output -notmatch 'Pulse INTRO para cerrar') { throw 'Missing final pause on error.' }
'PASS: standalone execution, adjacent config, cancellation, production guard and missing config. No SQL connections.'
