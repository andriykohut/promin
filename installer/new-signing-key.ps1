#Requires -Version 7.4
# Makes the project's signing key, once. The private key goes to the PROMIN_SIGNING_KEY repository secret, which the
# release workflow signs with, and to -BackupPath, which only the owner keeps. The public key is printed for
# UpdateSignature.ProjectPublicKey. Without the private key, a release cannot be signed: every agent would then need
# its next setup run by hand, with a new key built in.
# Needs the GitHub CLI, signed in with rights to set the repository's secrets.

param([Parameter(Mandatory)][string]$BackupPath)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backup = [System.IO.Path]::GetFullPath($BackupPath)
if ($backup.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Keep the backup outside the repository ($repo)."
}
if (Test-Path -LiteralPath $backup) { throw "$backup already exists; choose a new path." }

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $private = [Convert]::ToBase64String($key.ExportPkcs8PrivateKey())
    Set-Content -LiteralPath $backup -Value $private -NoNewline -Encoding ascii
    $private | gh secret set PROMIN_SIGNING_KEY --repo andriykohut/promin
    Write-Host "The private key is saved in $backup and set as PROMIN_SIGNING_KEY."
    Write-Host 'Put this in UpdateSignature.ProjectPublicKey:'
    Write-Host ([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
}
finally {
    $key.Dispose()
}
