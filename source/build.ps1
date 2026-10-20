param([string]$NsisCompiler)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 Developer Pack or use a Windows machine with the Framework C# compiler.' }
$releaseRoot = Split-Path -Parent $PSScriptRoot
$portableRoot = Join-Path $releaseRoot 'portable'
New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
$source = Join-Path $PSScriptRoot 'FindLite.cs'
$theme = Join-Path $PSScriptRoot 'Theme.cs'
$icon = Join-Path $PSScriptRoot 'FindLite.ico'
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Runtime.Serialization.dll')
if (-not (Test-Path -LiteralPath $icon)) {
  $iconBuilder = Join-Path $env:TEMP ('FindLite-IconBuilder-' + [Guid]::NewGuid().ToString('N') + '.exe')
  & $compiler /nologo /target:exe /main:IconBuilder /platform:x64 /optimize+ /codepage:65001 "/out:$iconBuilder" @references $source $theme (Join-Path $PSScriptRoot 'IconBuilder.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Icon builder failed.' }
  & $iconBuilder $icon
  if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
  Remove-Item -LiteralPath $iconBuilder
}
& $compiler /nologo /target:winexe /main:Program /platform:x64 /optimize+ /codepage:65001 "/out:$portableRoot\FindLite.exe" "/win32icon:$icon" "/win32manifest:$PSScriptRoot\app.manifest" "/resource:$icon,FindLite.Icon" @references $source $theme (Join-Path $PSScriptRoot 'AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Write-Output "Built $portableRoot\FindLite.exe"
if ($NsisCompiler) {
  $installerScript = Join-Path $PSScriptRoot 'installer.nsi'
  $installerText = [System.IO.File]::ReadAllText($installerScript)
  [System.IO.File]::WriteAllText($installerScript, $installerText, [System.Text.UTF8Encoding]::new($true))
  & $NsisCompiler /V2 $installerScript
  if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
}
