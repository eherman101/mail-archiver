"""Tests for importer.py. Run: python3 -m unittest -v test_importer (in this folder)."""

import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

import importer

MBOX = b"From a@b Mon Jan  1 00:00:00 2024\nSubject: hi\n\nbody\n"

# Stands in for `dotnet MailArchiver.dll`: records its arguments and prints the
# same result block the real CLI prints. FAKE_STATUS picks the outcome.
FAKE_CLI = r'''
import json, os, sys
with open(os.environ["FAKE_LOG"], "a") as fh:
    fh.write(json.dumps(sys.argv[1:]) + "\n")
status = os.environ.get("FAKE_STATUS", "Completed")
if status == "crash":
    print("Unhandled exception"); sys.exit(134)
print("info: MailArchiver[0] per-message noise")
print("=== Import Results ===")
print(f"Status: {status}")
print("Total Emails: 10")
print("Imported Successfully: 3")
print(f"Failed: {os.environ.get('FAKE_FAILED', '1')}")
print("Skipped (malformed): 0")
print("Skipped (duplicates): 6")
sys.exit(0 if status == "Completed" else 1)
'''


class ImporterTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        for d in ("downloads", "work", "state"):
            (self.tmp / d).mkdir()
        cli = self.tmp / "fake_cli.py"
        cli.write_text(FAKE_CLI)
        self.calls = self.tmp / "calls.log"
        os.environ.update({
            "DOWNLOADS_DIR": str(self.tmp / "downloads"),
            "WORK_DIR": str(self.tmp / "work"),
            "STATE_DIR": str(self.tmp / "state"),
            "ACCOUNT_ID": "1",
            "SETTLE_MINUTES": "0",
            "IMPORT_CMD": f"{sys.executable} {cli}",
            "IMPORT_CWD": str(self.tmp),
            "FAKE_LOG": str(self.calls),
            "FAKE_STATUS": "CompletedWithErrors",
            "FAKE_FAILED": "1",
            "DELETE_AFTER_IMPORT": "false",
        })
        self.cfg = importer.Config()
        self.state = importer.State(self.cfg.state_dir / "state.json")

    def make_zip(self, name="takeout-20260101T000000Z-1-001.zip", members=None):
        members = members or {"Takeout/Mail/All mail Including Spam and Trash.mbox": MBOX,
                              "Takeout/Mail/User Settings/Filters.json": b"{}"}
        path = self.tmp / "downloads" / name
        with zipfile.ZipFile(path, "w") as zf:
            for member, data in members.items():
                zf.writestr(member, data)
        return path

    def poll(self):
        # Two polls: the first only records the size, the second sees it settled.
        importer.run_once(self.cfg, self.state)
        importer.run_once(self.cfg, self.state)

    def calls_made(self):
        return [json.loads(l) for l in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def test_zip_imported_once_with_account_and_folder(self):
        self.make_zip()
        self.poll()
        calls = self.calls_made()
        self.assertEqual(len(calls), 1)
        args = calls[0]
        self.assertEqual(args[args.index("--account-id") + 1], "1")
        self.assertEqual(args[args.index("--folder") + 1], "INBOX")
        self.assertTrue(args[args.index("--file") + 1].endswith("All mail Including Spam and Trash.mbox"))
        entry = next(iter(self.state.archives.values()))
        self.assertEqual(entry["result"], "done")
        self.assertEqual(entry["totals"]["imported"], 3)
        self.assertEqual(entry["totals"]["duplicates"], 6)
        # Extracted mbox is cleaned up; later polls don't import it again.
        self.assertEqual(list((self.tmp / "work").iterdir()), [])
        self.poll()
        self.assertEqual(len(self.calls_made()), 1)
        status = json.loads((self.tmp / "state" / "status.json").read_text())
        self.assertEqual(status["last_import_totals"]["imported"], 3)

    def test_tgz_is_supported(self):
        path = self.tmp / "downloads" / "takeout-20251102T143457Z-1-001.tgz"
        with tarfile.open(path, "w:gz") as tf:
            info = tarfile.TarInfo("Takeout/Mail/All mail Including Spam and Trash.mbox")
            info.size = len(MBOX)
            tf.addfile(info, io.BytesIO(MBOX))
        self.poll()
        self.assertEqual(len(self.calls_made()), 1)

    def test_partial_download_is_left_alone(self):
        path = self.make_zip()
        path.with_name(path.name + ".part").write_bytes(b"")
        self.poll()
        self.assertEqual(self.calls_made(), [])

    def test_growing_file_waits_to_settle(self):
        os.environ["SETTLE_MINUTES"] = "10"
        self.cfg = importer.Config()
        self.make_zip()
        self.poll()
        self.assertEqual(self.calls_made(), [])

    def test_archive_without_mail_is_skipped(self):
        self.make_zip(members={"Takeout/Drive/file.txt": b"x"})
        self.poll()
        self.assertEqual(self.calls_made(), [])
        self.assertEqual(next(iter(self.state.archives.values()))["result"], "done")

    def test_crash_is_retried_up_to_max_attempts(self):
        os.environ["FAKE_STATUS"] = "crash"
        self.make_zip()
        for _ in range(5):
            importer.run_once(self.cfg, self.state)
        entry = next(iter(self.state.archives.values()))
        self.assertEqual(entry["result"], "failed")
        self.assertEqual(entry["attempts"], 3)
        self.assertEqual(len(self.calls_made()), 3)

    def test_non_takeout_files_ignored(self):
        (self.tmp / "downloads" / "holiday.zip").write_bytes(b"x")
        self.poll()
        self.assertEqual(self.state.archives, {})

    def enable_delete(self):
        os.environ["DELETE_AFTER_IMPORT"] = "true"
        self.cfg = importer.Config()

    def test_clean_import_deletes_archive(self):
        self.enable_delete()
        os.environ["FAKE_FAILED"] = "0"
        path = self.make_zip()
        self.poll()
        self.assertFalse(path.exists())
        entry = next(iter(self.state.archives.values()))
        self.assertIn("deleted", entry)
        self.assertEqual(len(self.calls_made()), 1)

    def test_archive_with_failed_messages_is_kept(self):
        self.enable_delete()
        path = self.make_zip()  # fake CLI reports Failed: 1
        self.poll()
        self.assertTrue(path.exists())

    def test_delete_off_by_default(self):
        os.environ["FAKE_FAILED"] = "0"
        path = self.make_zip()
        self.poll()
        self.assertTrue(path.exists())

    def test_crashed_or_unrecorded_archive_never_deleted(self):
        self.enable_delete()
        os.environ["FAKE_STATUS"] = "crash"
        crashed = self.make_zip()
        manual = self.make_zip(name="takeout-20250101T000000Z-1-001.zip")
        self.state.archives[importer.archive_key(manual)] = {"file": manual.name, "attempts": 0, "result": "done"}
        for _ in range(4):
            importer.run_once(self.cfg, self.state)
        self.assertTrue(crashed.exists())
        self.assertTrue(manual.exists())  # done, but no totals: never imported by us

    def test_parse_results(self):
        res = importer.parse_results("Status: Completed\nTotal Emails: 5\nFailed: 0\nDuration: 00:01:00\n")
        self.assertEqual(res, {"status": "Completed", "total": 5, "failed": 0})


if __name__ == "__main__":
    unittest.main()
