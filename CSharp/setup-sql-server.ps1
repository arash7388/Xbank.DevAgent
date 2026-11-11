$ErrorActionPreference = 'Stop'
$toolDirectory = Join-Path $PSScriptRoot '.tools/mssql'
dotnet tool install Alyio.McpMssql --tool-path $toolDirectory --version 1.8.0 --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'Alyio MCP MSSQL installation failed.' }
Write-Host 'Alyio MCP MSSQL is ready. Configure SQL Server in the application Settings.'
