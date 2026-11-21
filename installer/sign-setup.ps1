#Requires -Version 7.4
# Signs each setup with the project's key, writing <setup>.sig beside it, for the agent and the console to check
# before they run it. The release workflow runs this with the key from the PROMIN_SIGNING_KEY secret.
# What is signed and the .sig format are UpdateSignature and SetupSignature in src/Promin.Protocol/Updates.

param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string[]]$Setups
)

$ErrorActionPreference = 'Stop'

if (-not $env:PROMIN_SIGNING_KEY) { throw 'PROMIN_SIGNING_KEY is not set' }

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $read = 0
    $key.ImportPkcs8PrivateKey([Convert]::FromBase64String($env:PROMIN_SIGNING_KEY), [ref]$read)

    foreach ($setup in $Setups) {
        $sha256 = [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($setup))
        $name = Split-Path -Leaf $setup
        $signed = [System.Text.Encoding]::UTF8.GetBytes("promin-setup`n$name`n$Version`n$([Convert]::ToHexStringLower($sha256))")
        $signature = $key.SignData($signed, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        [ordered]@{ version = $Version; signature = [Convert]::ToBase64String($signature) } |
            ConvertTo-Json -Compress |
            Set-Content -LiteralPath "$setup.sig" -NoNewline -Encoding utf8NoBOM
    }
}
finally {
    $key.Dispose()
}
