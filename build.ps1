$ErrorActionPreference = 'Stop'

$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $base 'build\tools\roslyn\tasks\net472\csc.exe'
$wv = Join-Path $base 'build\tools\webview2\lib\net462'
$appOut = Join-Path $base 'build\out\app'
$payloadDir = Join-Path $base 'build\payload'
$payloadZip = Join-Path $base 'build\payload.zip'
$dist = Join-Path $base 'dist'
$icoDir = Join-Path $base 'ico'
$buildIconDir = Join-Path $base 'build\icon'
$iconTools = Join-Path $base 'build\IconTools.exe'
$bootstrap = Join-Path $base 'bootstrap-tools.ps1'

if (!(Test-Path $csc) -or
    !(Test-Path (Join-Path $wv 'Microsoft.Web.WebView2.Core.dll')) -or
    !(Test-Path (Join-Path $base 'build\tools\MicrosoftEdgeWebview2Setup.exe'))) {

    if (Test-Path $bootstrap) {
        Write-Host 'Не найдены инструменты сборки — запускаю bootstrap-tools.ps1...'
        & $bootstrap
    }
    else {
        throw 'Не найдены инструменты сборки. Запустите bootstrap-tools.ps1.'
    }
}

function Invoke-Csc {
    param([string[]]$Arguments)
    & $csc @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed (exit code $LASTEXITCODE)"
    }
}

Write-Host 'Preparing icons...'
New-Item -ItemType Directory -Force $buildIconDir | Out-Null
$icon = $null
$logo = $null

if (Test-Path (Join-Path $base 'src\tools\IconTools.cs')) {
    & $csc /nologo /target:exe "/out:$iconTools" /r:System.Drawing.dll `
        (Join-Path $base 'src\tools\IconTools.cs')
    if ($LASTEXITCODE -ne 0) { throw 'IconTools compilation failed' }
}

if (Test-Path $icoDir) {
    $icoFile = Get-ChildItem $icoDir -Filter *.ico -File -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object -First 1

    if ($icoFile) {
        $icon = $icoFile.FullName
    }
    else {
        $image = Get-ChildItem $icoDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|bmp)$' } |
            Sort-Object Name | Select-Object -First 1

        if ($image) {
            $generatedIco = Join-Path $buildIconDir 'max.ico'
            $generatedLogo = Join-Path $buildIconDir 'max-logo.png'
            & $iconTools from-image $image.FullName $generatedIco $generatedLogo
            if ($LASTEXITCODE -ne 0) { throw 'Icon conversion failed' }
            $icon = $generatedIco
            $logo = $generatedLogo
        }
    }
}

if (!$icon) {
    $icon = Join-Path $base 'assets\max.ico'
    Write-Host '  Custom icon not found, using fallback assets\max.ico'
}

if (!$logo) {
    $logoFile = Get-ChildItem $icoDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.png' } |
        Sort-Object Name | Select-Object -First 1

    if ($logoFile) {
        $logo = $logoFile.FullName
    }
    elseif (Test-Path $iconTools) {
        $extractedLogo = Join-Path $buildIconDir 'max-logo.png'
        & $iconTools from-ico $icon $extractedLogo 2>$null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $extractedLogo)) {
            $logo = $extractedLogo
        }
    }
}

Write-Host "  Icon: $icon"
if ($logo) { Write-Host "  Header logo: $logo" }

Write-Host 'Cleaning output directories...'
Remove-Item -Recurse -Force $appOut, $payloadDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $appOut, $payloadDir, $dist | Out-Null

Write-Host 'Compiling MAX.exe...'
Invoke-Csc @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:latest',
    '/optimize+', '/deterministic',
    "/out:$appOut\Max.exe",
    "/win32icon:$icon",
    "/win32manifest:$base\src\app\app.manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    "/r:$wv\Microsoft.Web.WebView2.Core.dll",
    "/r:$wv\Microsoft.Web.WebView2.WinForms.dll",
    "$base\src\app\Program.cs",
    "$base\src\app\MainForm.cs",
    "$base\src\app\AssemblyInfo.cs"
)

Write-Host 'Copying WebView2 runtime files...'
Copy-Item "$wv\Microsoft.Web.WebView2.Core.dll" $appOut -Force
Copy-Item "$wv\Microsoft.Web.WebView2.WinForms.dll" $appOut -Force
foreach ($arch in @('x64', 'x86', 'arm64')) {
    $target = Join-Path $appOut "runtimes\$arch"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $base "build\tools\webview2\runtimes\win-$arch\native\WebView2Loader.dll") $target -Force
}

Write-Host 'Preparing payload...'
Copy-Item "$appOut\*" $payloadDir -Recurse -Force
Copy-Item (Join-Path $base 'assets\README.txt') $payloadDir -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadDir,
    $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

Write-Host 'Compiling MaxSetup.exe...'
$setupOut = Join-Path $dist 'MaxSetup.exe'
$installerArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:latest',
    '/optimize+', '/deterministic',
    "/out:$setupOut",
    "/win32icon:$icon",
    "/win32manifest:$base\src\installer\installer.manifest",
    "/resource:$payloadZip,payload.zip",
    "/resource:$base\build\tools\MicrosoftEdgeWebview2Setup.exe,webview2setup.exe",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll', '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.CSharp.dll',
    "$base\src\installer\Program.cs",
    "$base\src\installer\EntryPoint.cs",
    "$base\src\installer\InstallerForm.cs",
    "$base\src\installer\ShortcutHelper.cs",
    "$base\src\installer\Branding.cs",
    "$base\src\installer\AssemblyInfo.cs"
)
if ($logo) {
    $installerArgs += "/resource:$logo,logo.png"
}
Invoke-Csc -Arguments $installerArgs

Write-Host ''
Write-Host 'Build completed:'
Get-ChildItem $dist | Format-Table Name, Length, LastWriteTime
Get-ChildItem $appOut -Recurse -File | Measure-Object Length -Sum |
    ForEach-Object { Write-Host ("App payload size: {0:N2} MB" -f ($_.Sum / 1MB)) }
