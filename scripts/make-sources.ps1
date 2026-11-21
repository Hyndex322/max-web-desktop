param(
    [string]$OutputPath = '',
    [string]$Version = '1.0.1'
)

$ErrorActionPreference = 'Stop'

$base = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $base "dist\MAX-$Version-sources.zip"
}

$staging = Join-Path $env:TEMP ("MAX-sources-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $staging | Out-Null

try {
    $include = @(
        'README.md',
        'build.ps1',
        'bootstrap-tools.ps1',
        '.gitignore',
        'src',
        'scripts',
        'assets',
        'ico',
        'docs',
        'dist\README-INSTALL.txt',
        'dist\SHA256.txt',
        'dist\MAX-Publisher.cer'
    )

    foreach ($item in $include) {
        $source = Join-Path $base $item
        if (!(Test-Path $source)) { continue }

        $destination = Join-Path $staging $item
        $parent = Split-Path -Parent $destination
        if ($parent) { New-Item -ItemType Directory -Force $parent | Out-Null }

        if ((Get-Item $source).PSIsContainer) {
            Copy-Item $source $destination -Recurse -Force
        }
        else {
            Copy-Item $source $destination -Force
        }
    }

    $buildReadme = @"
Сборка MAX для Windows $Version из исходников
============================================

Требования:
  - Windows 10/11
  - .NET Framework 4.8
  - PowerShell 5.1
  - Интернет для загрузки закреплённых версий инструментов

Шаги:
  1. Распакуйте архив в папку без пробелов (желательно).
  2. powershell -ExecutionPolicy Bypass -File .\bootstrap-tools.ps1
  3. powershell -ExecutionPolicy Bypass -File .\build.ps1
  4. (необязательно, для подписи) powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfSign
     либо укажите -CertificatePath, -CertificatePassword или -Thumbprint.

Результат:
  dist\MaxSetup.exe
  dist\SHA256.txt
  dist\README-INSTALL.txt

Подписанный релиз содержит Max.exe и MaxSetup.exe с Authenticode-подписью.
Самоподписанный сертификат (если использовался -SelfSign) не является
доверенным для Windows SmartScreen; для публичного релиза нужен сертификат
доверенного центра сертификации.
"@
    Set-Content -Path (Join-Path $staging 'SOURCE-BUILD.txt') `
        -Value $buildReadme -Encoding UTF8

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging,
        $OutputPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "Архив исходников: $OutputPath"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
