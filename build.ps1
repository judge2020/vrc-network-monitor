param([string]$Output = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'VrcNetworkMonitor.csproj') -c Release -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$toolsDir = Join-Path $Output 'tools'
New-Item -ItemType Directory -Force $toolsDir | Out-Null
$presentMon = Join-Path $toolsDir 'PresentMon.exe'
$expected = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
if (!(Test-Path $presentMon) -or (Get-FileHash $presentMon -Algorithm SHA256).Hash -ne $expected) {
    Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $presentMon
}
if ((Get-FileHash $presentMon -Algorithm SHA256).Hash -ne $expected) { throw 'PresentMon download hash mismatch' }
Invoke-WebRequest 'https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.6.0/LICENSE.txt' -OutFile (Join-Path $toolsDir 'PresentMon-LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $Output
Copy-Item (Join-Path $PSScriptRoot 'README.md') $Output
Copy-Item (Join-Path $PSScriptRoot 'docs') $Output -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'TraceEvent-LICENSE.txt') $toolsDir
$testFile = Join-Path $Output 'self-test.json'
$testProcess = Start-Process (Join-Path $Output 'VrcNetworkMonitor.exe') -ArgumentList @('--self-test', '--out', ('"' + $testFile + '"')) -Wait -PassThru
if ($testProcess.ExitCode -ne 0) { throw "Self-test failed; see $testFile" }
Write-Host "Built and checked: $Output"
