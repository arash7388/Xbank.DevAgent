param(
    [Parameter(Mandatory = $true)]
    [string] $SdkRoot
)

$ErrorActionPreference = 'Stop'
$sdkPath = (Resolve-Path -LiteralPath $SdkRoot).Path
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('maf-talk-sdk-' + [guid]::NewGuid().ToString('N'))
$packageOutput = Join-Path $PSScriptRoot 'packages'
$nugetConfig = Join-Path $PSScriptRoot '../../NuGet.Config'

# Copy only library source, excluding SDK build output, demos and local configuration.
foreach ($part in @('POD_Async', 'POD_Chat')) {
    $sourceRoot = Join-Path $sdkPath $part
    Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($sdkPath.Length + 1)
            $target = Join-Path $buildRoot $relative
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
}

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Version>0.9.1.12-maf.1</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Apache.NMS.ActiveMQ" Version="2.1.1" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
    <PackageReference Include="NLog" Version="4.7.15" />
    <PackageReference Include="RestSharp" Version="106.15.0" />
    <PackageReference Include="System.ComponentModel.Annotations" Version="5.0.0" />
    <PackageReference Include="System.Drawing.Common" Version="8.0.21" />
    <PackageReference Include="WebSocket4Net" Version="0.15.2" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $buildRoot 'POD_Async/POD_Async.csproj')

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Version>0.10.6.22-maf.1</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../POD_Async/POD_Async.csproj" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $buildRoot 'POD_Chat/POD_Chat.csproj')

foreach ($part in @('POD_Async', 'POD_Chat')) {
    dotnet pack (Join-Path $buildRoot "$part/$part.csproj") --configuration Release `
        --output $packageOutput --configfile $nugetConfig --nologo
    if ($LASTEXITCODE -ne 0) { throw "Failed to package $part. Build source retained at $buildRoot" }
}
Write-Output "Packages written to $packageOutput. Build source retained at $buildRoot"
