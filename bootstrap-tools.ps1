$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Скачивает инструменты, нужные для сборки:
#  - Microsoft.Web.WebView2 SDK (NuGet)
#  - компилятор C# (Roslyn)
#  - загрузчик WebView2 Runtime (для встраивания в установщик)

$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDir = Join-Path $base 'build\tools'
$nugetDir = Join-Path $base 'build\nuget'

# Закреплённые версии — так сборка воспроизводима.
$webView2Version = '1.0.4314.35'
$roslynVersion = '5.12.0'

New-Item -ItemType Directory -Force $toolsDir, $nugetDir | Out-Null

function Get-NuGetPackage {
    param([string]$PackageId, [string]$Version, [string]$Destination)

    $url = "https://api.nuget.org/v3-flatcontainer/$PackageId/$Version/$PackageId.$Version.nupkg"
    $file = Join-Path $nugetDir "$PackageId.$Version.nupkg"

    if (!(Test-Path $file)) {
        Write-Host "Скачивание $PackageId $Version..."
        Invoke-WebRequest $url -OutFile $file -UseBasicParsing
    }

    Write-Host "Распаковка $PackageId..."
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }

    try {
        # .nupkg — это обычный ZIP, но Expand-Archive принимает только *.zip.
        [System.IO.Compression.ZipFile]::ExtractToDirectory($file, $Destination)
    }
    catch {
        $zipFile = [System.IO.Path]::ChangeExtension($file, '.zip')
        Copy-Item $file $zipFile -Force
        Expand-Archive $zipFile -DestinationPath $Destination -Force
    }
}

if (!(Test-Path (Join-Path $toolsDir 'webview2\lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
    Get-NuGetPackage -PackageId 'microsoft.web.webview2' -Version $webView2Version `
        -Destination (Join-Path $toolsDir 'webview2')
}

if (!(Test-Path (Join-Path $toolsDir 'roslyn\tasks\net472\csc.exe'))) {
    Get-NuGetPackage -PackageId 'microsoft.net.compilers.toolset' -Version $roslynVersion `
        -Destination (Join-Path $toolsDir 'roslyn')
}

$bootstrap = Join-Path $toolsDir 'MicrosoftEdgeWebview2Setup.exe'
if (!(Test-Path $bootstrap)) {
    Write-Host 'Скачивание загрузчика WebView2...'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' `
        -OutFile $bootstrap -UseBasicParsing
}

Write-Host ''
Write-Host 'Инструменты готовы:'
Get-ChildItem $toolsDir | Select-Object Name, Length
Write-Host ''
Write-Host 'Теперь запустите build.ps1.'
