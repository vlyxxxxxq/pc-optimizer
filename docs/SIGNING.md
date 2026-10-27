# Signing Rig releases

Rig's updater checks every download's size and, when GitHub publishes one, its SHA-256 digest. That catches a
damaged download, but it can't prove the file came from you: anyone who could replace a release asset could replace
its digest too. Signed releases close that gap. **This is not set up yet**: until it is, updates are unsigned.

How it works once set up:

- The release workflow signs `Rig.exe` with a private key kept as the `RIG_SIGNING_KEY` repository secret, and uploads
  `Rig.exe.sig` next to it. The signature is ECDSA P-256 over SHA-256 of the file,
  base64-encoded.
- Rig carries the matching public key in `Updater.SigningKey`. When that constant isn't empty, the updater refuses
  any update without a valid `.sig`.

## Setting it up (one time, by the maintainer)

1. On a trusted machine, create a key pair (PowerShell 7):

   ```powershell
   $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName("nistP256"))
   $key.ExportPkcs8PrivateKeyPem() | Set-Content rig-signing-private.pem
   [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
   ```

2. Add the contents of `rig-signing-private.pem` as the repository secret `RIG_SIGNING_KEY`
   (GitHub → Settings → Secrets and variables → Actions). Then delete or securely store the file; never commit it.
3. Paste the printed public key into `SigningKey` in `Rig/Services/Updater.cs`.
4. Release as usual. The first release that contains the key should itself be signed, so check the
   `Sign the assets` step ran.

Notes:

- Versions without the key (2.0.x, 1.x) still update the old way; from the first version with the key onward,
  unsigned releases are refused.
- Losing the private key means shipping a version with a new public key, which older versions will refuse; keep a
  secure backup.
- Authenticode code signing (removing the SmartScreen warning) is separate and needs a certificate from a CA.
