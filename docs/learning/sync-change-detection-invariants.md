# Sync Change Detection: Invariants

## Context

The app decides what to upload by comparing each file against `sync_hashes.json`:
timestamp + size first (fast path), SHA-256 when those differ. Every bug in this area
has the same shape: the index claims a file is synced when Drive has something else,
and the UI shows *Up to date* over it. These rules keep that from happening.

## Invariants

1. **Capture metadata, then hash, then upload.** The index must describe a version no
   newer than the one uploaded. If the file changes mid-way, its timestamp moves past
   the stored one and the next run re-hashes. Hashing *after* the upload records a
   version Drive never received, and the fast path then trusts it forever.
   `FileInfo` loads its properties lazily on first access, so call `Refresh()` to pin
   the snapshot at a known point.

2. **"Could not read" is an error, not "unchanged".** A locked or permission-denied
   file, or a folder that cannot be listed, must reach `sync_errors.json` and fail the
   run. Otherwise it is silently skipped while the status reads *Up to date*.

3. **Know each key format before purging.** Work-file keys are `<prefix>|<localPath>`
   and can be purged when the local file is gone. Claude context keys are
   Drive-relative paths with no local counterpart; running `File.Exists` on them
   resolves against the working directory, always fails, and wipes them on every sync.

4. **Write the index atomically.** A truncated index loads as empty and triggers a full
   re-upload with no warning. Write to a temp file and replace.

5. **Tests must not share the real data directory.** `DriveSyncService.DataDirectory`
   is settable; `TempSettingsFileScope` redirects it, and the test assembly runs
   sequentially because both paths are process-wide statics.

## Known limits

- The index records what this app uploaded, not what is in Drive. Deletions in Drive
  are not detected; clearing the index from Settings forces a full re-upload.
- The fast path trusts an unchanged timestamp and size, so an edit that preserves both
  goes unnoticed.
