<#
.SYNOPSIS
    Builds a self-contained Windows package (no .NET installation needed on the server).
.EXAMPLE
    .\scripts\publish.ps1            # -> .\publish\
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish')
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'

dotnet test (Join-Path $root 'Mailserver.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed, nothing published.' }

foreach ($project in 'Mailserver.Service', 'Mailserver.Admin') {
    dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 --self-contained -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Write-Host "Package ready in $Output — copy it to the server and run scripts\install.ps1 there."
