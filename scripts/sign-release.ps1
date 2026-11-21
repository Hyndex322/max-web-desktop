param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$Thumbprint,
    [switch]$SelfSigned,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$CerOutput
)

$ErrorActionPreference = 'Stop'

function New-PublisherCertificate {
    $subject = 'CN=MAX Messenger (unofficial build), O=MAX Messenger, C=RU'
    Write-Host "Создание самоподписанного сертификата издателя: $subject"
    return New-SelfSignedCertificate `
        -Subject $subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3) `
        -FriendlyName 'MAX Messenger Publisher 1.0.1'
}

$cert = $null
$imported = $false

if (![string]::IsNullOrWhiteSpace($CertificatePath)) {
    if (!(Test-Path $CertificatePath)) {
        throw "Сертификат не найден: $CertificatePath"
    }

    $secure = ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
    $cert = Import-PfxCertificate `
        -FilePath $CertificatePath `
        -Password $secure `
        -CertStoreLocation 'Cert:\CurrentUser\My'
    $imported = $true
}
elseif (![string]::IsNullOrWhiteSpace($Thumbprint)) {
    $cert = Get-ChildItem 'Cert:\CurrentUser\My' |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1

    if (!$cert) {
        throw "Сертификат с отпечатком $Thumbprint не найден."
    }
}
elseif ($SelfSigned) {
    $cert = New-PublisherCertificate
}
else {
    throw 'Не задан сертификат подписи (CertificatePath, Thumbprint или -SelfSigned).'
}

if (!$cert.HasPrivateKey) {
    throw 'У выбранного сертификата нет закрытого ключа.'
}

if (![string]::IsNullOrWhiteSpace($CerOutput)) {
    $cerDir = Split-Path -Parent $CerOutput
    if ($cerDir) { New-Item -ItemType Directory -Force $cerDir | Out-Null }
    Export-Certificate -Cert $cert -FilePath $CerOutput -Type CERT | Out-Null
    Write-Host "Публичный сертификат: $CerOutput"
}

try {
    foreach ($file in $Files) {
        if (!(Test-Path $file)) {
            throw "Файл для подписи не найден: $file"
        }

        Write-Host "Подпись: $file"
        $signature = Set-AuthenticodeSignature `
            -FilePath $file `
            -Certificate $cert `
            -HashAlgorithm SHA256 `
            -TimestampServer $TimestampUrl

        if (!$signature.SignerCertificate) {
            throw "Не удалось подписать файл: $file"
        }

        $check = Get-AuthenticodeSignature -FilePath $file
        if (!$check.SignerCertificate) {
            throw "Подпись отсутствует после подписания: $file"
        }

        Write-Host ("  статус проверки: {0}; издатель: {1}" -f $check.Status, $check.SignerCertificate.Subject)
    }
}
finally {
    if ($imported -and $cert) {
        Remove-Item ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue
    }
}
