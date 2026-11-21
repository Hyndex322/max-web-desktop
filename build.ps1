param(
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$Thumbprint,
    [switch]$SelfSign,
    [switch]$NoSign,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

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
$signScript = Join-Path $base 'scripts\sign-release.ps1'
$sourceScript = Join-Path $base 'scripts\make-sources.ps1'
$version = '1.0.1'

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

$selfSignedCert = $null
$signThumbprint = $Thumbprint
$cerOutput = Join-Path $dist 'MAX-Publisher.cer'

if ($SelfSign -and [string]::IsNullOrWhiteSpace($CertificatePath) -and
    [string]::IsNullOrWhiteSpace($Thumbprint)) {
    New-Item -ItemType Directory -Force $dist | Out-Null
    Write-Host 'Создание самоподписанного сертификата издателя...'
    $selfSignedCert = New-SelfSignedCertificate `
        -Subject 'CN=MAX Messenger (unofficial build), O=MAX Messenger, C=RU' `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3) `
        -FriendlyName "MAX Messenger Publisher $version"
    $signThumbprint = $selfSignedCert.Thumbprint
    Export-Certificate -Cert $selfSignedCert -FilePath $cerOutput -Type CERT | Out-Null
    Write-Host "  Сертификат: $cerOutput"
}

function Invoke-SignFiles {
    param([string[]]$Files)

    if ($NoSign) {
        Write-Host 'Подпись пропущена (-NoSign).'
        return
    }

    if ([string]::IsNullOrWhiteSpace($CertificatePath) -and
        [string]::IsNullOrWhiteSpace($signThumbprint)) {
        Write-Warning 'Кодовая подпись не настроена. Max.exe и MaxSetup.exe останутся неподписанными.'
        return
    }

    $arguments = @{
        Files = $Files
        TimestampUrl = $TimestampUrl
    }
    if (![string]::IsNullOrWhiteSpace($CertificatePath)) {
        $arguments.CertificatePath = $CertificatePath
        $arguments.CertificatePassword = $CertificatePassword
    }
    else {
        $arguments.Thumbprint = $signThumbprint
    }

    & $signScript @arguments
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
    $preferredIcon = Join-Path $icoDir 'max.ico'
    $icoFile = $null

    if (Test-Path $preferredIcon) {
        $icoFile = Get-Item $preferredIcon
    }
    else {
        $icoFile = Get-ChildItem $icoDir -Filter *.ico -File -ErrorAction SilentlyContinue |
            Sort-Object Name | Select-Object -First 1
    }

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
    $preferredLogo = Join-Path $icoDir 'max-logo.png'
    $logoFile = $null

    if (Test-Path $preferredLogo) {
        $logoFile = Get-Item $preferredLogo
    }
    else {
        $logoFile = Get-ChildItem $icoDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -eq '.png' } |
            Sort-Object Name | Select-Object -First 1
    }

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
    "$base\src\common\LogSanitizer.cs",
    "$base\src\app\Program.cs",
    "$base\src\app\NavigationPolicy.cs",
    "$base\src\app\PolicySelfTest.cs",
    "$base\src\app\MainForm.cs",
    "$base\src\app\AssemblyInfo.cs"
)

Write-Host 'Signing MAX.exe...'
Invoke-SignFiles -Files @("$appOut\Max.exe")

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
    "$base\src\common\LogSanitizer.cs",
    "$base\src\installer\Program.cs",
    "$base\src\installer\EntryPoint.cs",
    "$base\src\installer\InstallerEngine.cs",
    "$base\src\installer\SafeFileSystem.cs",
    "$base\src\installer\InstallManifest.cs",
    "$base\src\installer\SignatureVerifier.cs",
    "$base\src\installer\WebView2Installer.cs",
    "$base\src\installer\InstallerForm.cs",
    "$base\src\installer\ShortcutHelper.cs",
    "$base\src\installer\Branding.cs",
    "$base\src\installer\AssemblyInfo.cs"
)
if ($logo) {
    $installerArgs += "/resource:$logo,logo.png"
}
Invoke-Csc -Arguments $installerArgs

Write-Host 'Signing MaxSetup.exe...'
Invoke-SignFiles -Files @($setupOut)

Write-Host 'Writing SHA256.txt...'
$setupHash = (Get-FileHash $setupOut -Algorithm SHA256).Hash
Set-Content -Path (Join-Path $dist 'SHA256.txt') `
    -Value ("$setupHash *MaxSetup.exe") -Encoding ASCII

Write-Host 'Creating sources archive...'
& $sourceScript -OutputPath (Join-Path $dist "MAX-$version-sources.zip") -Version $version

if ($selfSignedCert) {
    Remove-Item ("Cert:\CurrentUser\My\" + $selfSignedCert.Thumbprint) -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Build completed:'
Get-ChildItem $dist | Format-Table Name, Length, LastWriteTime
Get-ChildItem $appOut -Recurse -File | Measure-Object Length -Sum |
    ForEach-Object { Write-Host ("App payload size: {0:N2} MB" -f ($_.Sum / 1MB)) }
Write-Host ("SHA256: {0}" -f $setupHash)
