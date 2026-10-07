param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\Gameorder.csproj'
$outputPath = Join-Path $PSScriptRoot 'dist'
if ($SelfContained) {
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ResetTestBuild=false -o $outputPath
} else {
    dotnet publish $projectPath -c Release --self-contained false -p:ResetTestBuild=false -o $outputPath
}
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Output ('已生成: ' + (Join-Path $outputPath 'Gameorder.exe'))
