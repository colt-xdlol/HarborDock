param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$sourceDirectory = $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 x64 is required.' }
$assemblyDirectory = Join-Path $env:WINDIR 'Microsoft.NET\assembly'
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll')
foreach ($assemblyName in @('WindowsBase', 'PresentationCore', 'PresentationFramework', 'System.Runtime')) {
    $assemblyFile = Get-ChildItem -LiteralPath $assemblyDirectory -Filter ($assemblyName + '.dll') -Recurse |
        Where-Object { $_.FullName -match 'v4\.0_' -and $_.FullName -notmatch 'GAC_32' } | Select-Object -First 1
    if (!$assemblyFile) { throw "Missing assembly: $assemblyName" }
    $references += $assemblyFile.FullName
}
$references += Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Devices.winmd'
$references += Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Foundation.winmd'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001',
    ('/out:' + (Join-Path $OutputDirectory 'HarborDock.exe')), ('/win32manifest:' + (Join-Path $sourceDirectory 'app.manifest')))
$iconPath = Join-Path $sourceDirectory 'app.ico'
if (Test-Path -LiteralPath $iconPath) { $arguments += '/win32icon:' + $iconPath }
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw "Compiler failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'HarborDock.exe.config') -Destination $OutputDirectory -Force
Write-Output (Join-Path $OutputDirectory 'HarborDock.exe')
