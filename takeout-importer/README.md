# takeout-importer (fork only)

Keeps the Mail Archive current from scheduled Google Takeout exports of Gmail.

1. Google Takeout (every 2 months, `.zip`, link by email) emails you when an export is ready.
   If `GMAIL_USER` / `GMAIL_APP_PASSWORD` are set, this container sees that email too, and
   notifies you then, and again after `REMIND_AFTER_DAYS` if it still hasn't been imported
   (Google's link expires after a week).
2. You download the zip in the NAS's Firefox container. It saves to `/volume1/shared/random/downloads`.
3. This container polls that folder. Once a `takeout-*.zip|tgz` has finished downloading (no `.part`
   file, and its size has held steady for `SETTLE_MINUTES`), it extracts `Takeout/Mail/*.mbox` to
   `/work` and runs Mail-Archiver's own CLI import (`--import-mbox`) into `ACCOUNT_ID`.
   Messages already in the archive are skipped, so a full-mailbox export only adds what's new.
4. The extracted mbox is deleted afterwards. With `DELETE_AFTER_IMPORT=true` the downloaded archive
   is deleted too, but only when the import ran to the end with 0 failed and 0 malformed messages
   (duplicates are fine: they are already archived). Anything else keeps the archive for a look.

Note: the CLI's "Total Emails" is upstream's estimate, extrapolated from the first 10 MB of the
mbox. The real count is imported + duplicates + failed + malformed.

State: `/state/state.json` (one entry per archive, keyed by name and size; a completed import is
never re-run, and a crashed one is retried up to `MAX_ATTEMPTS` times). `/state/status.json` has
a summary of the last run for dashboards. Progress is in `docker logs mail-archive-takeout`.

To re-import an archive, delete its entry from `state.json` and restart the container.

## Configuration (environment)

| Variable | Default | |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | (required) | Same database as the app |
| `LocalImport__AllowedPaths__0` | (required) | Must be `WORK_DIR` (`/work`) |
| `ACCOUNT_ID` | (required) | Mail-Archiver account to import into |
| `TARGET_FOLDER` | `INBOX` | Folder name given to imported mail |
| `DOWNLOADS_DIR` / `WORK_DIR` / `STATE_DIR` | `/downloads`, `/work`, `/state` | |
| `POLL_MINUTES` / `SETTLE_MINUTES` | 15 / 5 | |
| `DELETE_AFTER_IMPORT` | `false` | Delete cleanly imported archives (needs `DOWNLOADS_DIR` mounted read-write) |
| `GMAIL_USER`, `GMAIL_APP_PASSWORD` | unset (off) | Gmail IMAP with an app password, read-only |
| `GMAIL_CHECK_HOURS` / `REMIND_AFTER_DAYS` | 6 / 4 | |
| `NOTIFY_URL` | unset (log only) | An ntfy-style URL: the message is POSTed as the body, with a `Title` header |

## Build and test

```sh
python3 -m unittest -v test_importer          # no Docker needed
sudo docker build -t mail-archiver-takeout:latest .   # after building mail-archiver-local:latest
```
