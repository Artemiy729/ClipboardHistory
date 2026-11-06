# Encrypted clipboard history

## macOS

## Storage and key

The application stores text, timestamps, fingerprints, pins, favorites, and image
references inside the authenticated `history-v3.enc` archive. Image bytes are
stored separately under `encrypted-images/<UUID>.enc`. Both use AES-256-GCM
through Apple CryptoKit, with a fresh random nonce per write. Authentication
binds the archive to `history-v3` and each image to its filename, so image files
cannot be swapped between records without detection. Encryption does not hide
file sizes or the number of image files.

The random 256-bit key lives in a non-synchronizing generic-password item in the
login Keychain:

- Service: `local.artemiy.clipboardhistory.encryption`
- Account: `history-aes256-v1`
- Label: `Clipboard History encryption key`

The app explicitly creates an access list that trusts the creating application.
It never writes the key beside the history, to preferences, logs, or the repo.
Local builds use the file-based login Keychain and `SecAccessCreate`; that API
is deprecated but supports the project's ad-hoc signing workflow without paid
developer provisioning. It does **not** provide the device-lock guarantees of
the Data Protection Keychain. A production signed distribution should migrate
to the Data Protection Keychain with the appropriate entitlements.

Apple documents [AES-GCM authentication](https://developer.apple.com/documentation/cryptokit/aes/gcm),
[the creating-app trust rule for SecAccessCreate](https://developer.apple.com/documentation/security/secaccesscreate(_:_:_:)),
and [the macOS requirements for key accessibility attributes](https://developer.apple.com/documentation/security/ksecattraccessible).

Builds enable the hardened runtime. Directories use mode `0700`, ciphertext files
use `0600`. An exclusive file lock
prevents two updated copies from writing the same store concurrently. Old app
versions do not honor the lock: quit them before running the updated app, and do
not run an older version against a migrated store.

## Migration and failures

On first launch, the app loads the existing v2 archive (or the original v1 archive
if v2 is absent), encrypts valid images, atomically commits the encrypted index,
verifies that it decrypts, then removes `history-v2.json`, `history.json`, and the
old `images/` directory. No plaintext backup is created. If interrupted before
the encrypted index is committed, the originals remain available for retry. If
interrupted after commit, the next successful load finishes plaintext cleanup.
Migration removes these app-owned originals; external backups are unaffected.

Missing, invalid, or unauthenticated image files are skipped individually and the
remaining history is kept. The UI reports the number skipped. Permission and I/O
errors stop loading instead of treating inaccessible files as corrupt. If the
encrypted index is damaged or the key is missing/inaccessible, recording stops
and existing files are preserved. The app does not reset the store, generate a
replacement for a missing key when an encrypted index or image files exist, or fall back to
plaintext. Resolve the cause and choose **Retry Unlock**. Losing the original
key makes that encrypted history unrecoverable.

If the index is missing but encrypted images remain and there is no legacy
archive to migrate, the app stops and preserves those files instead of silently
creating an empty store.

Pins bypass time expiration, but not storage quotas or sensitive-content
filtering. Favorites alone do not prevent expiration. Deleting an entry commits
the new encrypted index before removing unreferenced encrypted image files.

## Limits of protection

Copied ciphertext files alone do not expose clipboard contents. However, a
running clipboard manager needs decrypted data and its key in memory to search,
preview, and record history. The system clipboard and destination app also hold
plaintext. This is not protection against malware in an unlocked session,
administrators controlling the machine, process-memory inspection, someone
allowed to retrieve the key from Keychain, or someone using the open app. There
is no separate app password or biometric unlock in this version.

Deleting old files cannot guarantee erasure of SSD blocks, APFS snapshots,
Time Machine history, or other backups. Earlier plaintext copies are not made
secure retroactively. Full-disk encryption and locking the session remain useful
additional controls. Avoid approving Keychain access for untrusted apps. An
ad-hoc rebuild can prompt again because the application's signing identity
changed.

## Windows

Windows protects `history-v1.enc` with current-user DPAPI through the native
`CryptProtectData` and `CryptUnprotectData` APIs. The encrypted archive contains
text, image bytes, fingerprints, timestamps, pins, and favorites. DPAPI handles
key management using the Windows user profile; no application key is stored
beside the history. The code never requests machine-wide protection. The fixed
application entropy identifies the format and is not a password or secret.
Microsoft describes the [user binding and integrity protection](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).

At startup the application authenticates the existing encrypted archive, or
loads legacy `history.json` when no encrypted archive exists. It skips invalid
images individually, writes and verifies the protected archive, then removes
the old plaintext archive and its old `.tmp` file. Encrypted writes use a
temporary file in the same directory followed by replacement. A file lock
prevents concurrent writers among updated app versions.

Decryption or save failures stop recording and show an error. A damaged
encrypted archive is preserved and is never replaced with an empty store or
an older plaintext history. There is no fallback to unencrypted storage.
DPAPI depends on the original user profile and Windows credentials; copying
the archive alone is not a portable backup or account-migration mechanism.

DPAPI restricts access by user, **not by application**. Another program running
as the same Windows user can potentially read and decrypt the archive without
an app-specific prompt. There is no separate master password or biometric
unlock. The memory, clipboard, compromised-session, and old-backup limitations
above also apply on Windows.

## Thumbnails

Both apps cache small image previews only in memory, bounded to 128 thumbnails.
macOS decodes and downsamples authenticated image data on a serial background
actor. Windows resizes image bytes from the decrypted history and disposes
evicted bitmaps. No thumbnail sidecar or plaintext preview file is created.
The build/CI demo screenshots contain only synthetic examples and the public
app icon; they do not read the user's history.
