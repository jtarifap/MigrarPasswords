$ErrorActionPreference = 'Stop'
'[1/5] Comprobando compilador .NET 3.5 y carpetas...'
if (-not (Test-Path -LiteralPath $PSScriptRoot -PathType Container)) { throw 'Missing project directory.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v3.5\vbc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Install .NET Framework 3.5 (VB compiler required).' }
$output = Join-Path $PSScriptRoot 'bin'
$packages = Join-Path $PSScriptRoot 'packages'
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($packages) | Out-Null
$package = Join-Path $packages 'bcrypt.net-next.4.0.2.nupkg'
$expectedHash = 'LxXl19IBYTEscb86Gx5l8t2RUgxjinARRsou6m3KsANHbS92sNLBA59eYlKK1DBzzwDzXMQHjE0EZ7+BgAeTTQ=='
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    '[2/5] Descargando BCrypt de NuGet (timeout: 60 segundos)...'
    $previousProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Uri 'https://api.nuget.org/v3-flatcontainer/bcrypt.net-next/4.0.2/bcrypt.net-next.4.0.2.nupkg' -OutFile $package
    } finally {
        [Net.ServicePointManager]::SecurityProtocol = $previousProtocol
    }
} else {
    '[2/5] Usando el paquete BCrypt local, sin descargar...'
}
'[3/5] Verificando checksum, extrayendo DLL y comprobando CLR...'
$sha = [Security.Cryptography.SHA512]::Create()
$stream = [IO.File]::OpenRead($package)
try { $actualHash = [Convert]::ToBase64String($sha.ComputeHash($stream)) }
finally { $stream.Dispose(); $sha.Dispose() }
if ($actualHash -cne $expectedHash) { throw 'BCrypt package checksum mismatch. Build stopped.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('lib/net35/BCrypt.Net-Next.dll')
    if ($null -eq $entry) { throw 'Missing net35 dependency.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $output 'BCrypt.Net-Next.dll'), $true)
} finally { $archive.Dispose() }
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $output 'BCrypt.Net-Next.dll'))
if ($assembly.ImageRuntimeVersion -ne 'v2.0.50727') { throw 'BCrypt must run on CLR 2.0 (.NET 3.5).' }
'[4/5] Copiando App.config a bin (sobrescribe la configuracion del ejecutable)...'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination (Join-Path $output 'MigrarPasswords.exe.config')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.txt') -Destination (Join-Path $output 'THIRD-PARTY.txt')
$arguments = @('/nologo', '/target:exe', '/optionstrict+', '/optionexplicit+', '/optioninfer+',
    '/reference:System.dll,System.Data.dll,System.Configuration.dll',
    ('/reference:' + (Join-Path $output 'BCrypt.Net-Next.dll')),
    ('/out:' + (Join-Path $output 'MigrarPasswords.exe')),
    (Join-Path $PSScriptRoot 'Program.vb'))
'[5/5] Compilando MigrarPasswords.exe. La utilidad debe estar cerrada...'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'VB.NET 3.5 compilation failed.' }
'Build OK: bin\MigrarPasswords.exe. No migration has been executed.'
