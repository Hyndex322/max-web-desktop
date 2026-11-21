param(
    [string]$Tag = 'v1.0.2',
    [string]$ReleaseName = 'MAX для Windows 1.0.2',
    [string]$NotesFile = '',
    [switch]$ForceRelease
)

$ErrorActionPreference = 'Stop'

$base = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$owner = 'Hyndex322'
$repo = 'max-web-desktop'
$dist = Join-Path $base 'dist'
$installer = Join-Path $dist 'MaxSetup.exe'
$shaFile = Join-Path $dist 'SHA256.txt'
$readmeFile = Join-Path $dist 'README-INSTALL.txt'
$sourcesFile = Join-Path $dist 'MAX-1.0.2-sources.zip'
$cerFile = Join-Path $dist 'MAX-Publisher.cer'
$signatureStatusFile = Join-Path $dist 'SIGNATURE-STATUS.txt'
$resourceImage = Join-Path $base 'docs\resource-usage.png'

if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    $NotesFile = Join-Path $base 'docs\RELEASE-1.0.2.md'
}

foreach ($file in @($installer, $shaFile, $readmeFile, $sourcesFile, $cerFile, $signatureStatusFile, $resourceImage, $NotesFile)) {
    if (!(Test-Path $file)) {
        throw "Не найден файл релиза: $file"
    }
}

$signatureText = [System.IO.File]::ReadAllText($signatureStatusFile, [System.Text.Encoding]::UTF8)
$trusted = $signatureText -match '(?m)^Trusted=True\s*$'
$prerelease = (!$trusted) -and (!$ForceRelease)
if (!$trusted) {
    $ReleaseName += ' (тестовая подпись)'
}

$hash = (Get-Content $shaFile -Raw).Trim().Split(' ')[0]
$actualHash = (Get-FileHash $installer -Algorithm SHA256).Hash
if ($hash -ne $actualHash) {
    throw "SHA256.txt не совпадает с MaxSetup.exe: $hash != $actualHash"
}

$inputText = "protocol=https`nhost=github.com`n`n"
$credOutput = $inputText | git credential fill 2>$null
$token = ($credOutput | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''

if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'Не найден токен GitHub в хранилище Git Credential Manager.'
}

$headers = @{
    Authorization = "token $token"
    'User-Agent' = 'max-setup-publisher'
    Accept = 'application/vnd.github+json'
}

$bodyText = [System.IO.File]::ReadAllText($NotesFile, [System.Text.Encoding]::UTF8)
$bodyText += "`n`n### SHA-256`n`n``$actualHash *MaxSetup.exe``"

if (!$trusted) {
    $bodyText += "`n`n> **Тестовая сборка.** Authenticode-подпись присутствует и имеет метку времени, но не является доверенной для Windows. Для финального релиза нужен сертификат доверенного центра сертификации.`n`n"
    $bodyText += $signatureText.Trim() + "`n"
}

$releaseJson = @{
    tag_name = $Tag
    target_commitish = 'main'
    name = $ReleaseName
    body = $bodyText
    draft = $false
    prerelease = $prerelease
} | ConvertTo-Json -Depth 5

$releaseBytes = [System.Text.Encoding]::UTF8.GetBytes($releaseJson)

$release = $null
try {
    $release = Invoke-RestMethod "https://api.github.com/repos/$owner/$repo/releases/tags/$Tag" `
        -Headers $headers -TimeoutSec 30
    Write-Output "RELEASE_EXISTS $($release.html_url)"

    $patchJson = @{
        name = $ReleaseName
        body = $bodyText
        draft = $false
        prerelease = $prerelease
    } | ConvertTo-Json -Depth 5
    $patchBytes = [System.Text.Encoding]::UTF8.GetBytes($patchJson)
    $release = Invoke-RestMethod "https://api.github.com/repos/$owner/$repo/releases/$($release.id)" `
        -Method Patch -Headers $headers -Body $patchBytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    Write-Output "RELEASE_UPDATED $($release.html_url)"
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -ne 404) { throw }

    $release = Invoke-RestMethod "https://api.github.com/repos/$owner/$repo/releases" `
        -Method Post -Headers $headers -Body $releaseBytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    Write-Output "RELEASE_CREATED $($release.html_url)"
}

$existing = Invoke-RestMethod "https://api.github.com/repos/$owner/$repo/releases/$($release.id)/assets" `
    -Headers $headers -TimeoutSec 30

$wanted = @(
    'MaxSetup.exe',
    'SHA256.txt',
    'README-INSTALL.txt',
    'MAX-1.0.2-sources.zip',
    'MAX-Publisher.cer',
    'SIGNATURE-STATUS.txt',
    'resource-usage.png'
)

foreach ($asset in $existing) {
    if ($wanted -contains $asset.name) {
        Invoke-RestMethod "https://api.github.com/repos/$owner/$repo/releases/assets/$($asset.id)" `
            -Method Delete -Headers $headers -TimeoutSec 30 | Out-Null
        Write-Output "REMOVED_OLD_ASSET $($asset.name)"
    }
}

function Upload-Asset {
    param([string]$Path, [string]$Name, [string]$ContentType)

    $uri = "https://uploads.github.com/repos/$owner/$repo/releases/$($release.id)/assets?name=$Name"
    $asset = Invoke-RestMethod $uri -Method Post -Headers $headers `
        -InFile $Path -ContentType $ContentType -TimeoutSec 300
    Write-Output "ASSET $($asset.name) $($asset.size) bytes $($asset.browser_download_url)"
}

Upload-Asset -Path $installer -Name 'MaxSetup.exe' -ContentType 'application/octet-stream'
Upload-Asset -Path $shaFile -Name 'SHA256.txt' -ContentType 'text/plain'
Upload-Asset -Path $readmeFile -Name 'README-INSTALL.txt' -ContentType 'text/plain'
Upload-Asset -Path $sourcesFile -Name 'MAX-1.0.2-sources.zip' -ContentType 'application/zip'
Upload-Asset -Path $cerFile -Name 'MAX-Publisher.cer' -ContentType 'application/x-x509-ca-cert'
Upload-Asset -Path $signatureStatusFile -Name 'SIGNATURE-STATUS.txt' -ContentType 'text/plain'
Upload-Asset -Path $resourceImage -Name 'resource-usage.png' -ContentType 'image/png'

Write-Output "RELEASE_URL https://github.com/$owner/$repo/releases/tag/$Tag"
Write-Output "LATEST_URL https://github.com/$owner/$repo/releases/latest"
