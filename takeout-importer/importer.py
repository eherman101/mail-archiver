"""Import Google Takeout Gmail exports into Mail-Archiver, unattended.

Watches a downloads folder for Takeout archives (takeout-*.zip / .tgz / .tar.gz),
pulls the Gmail .mbox files out of each finished download, and hands every mbox
to Mail-Archiver's own CLI importer (dotnet MailArchiver.dll --import-mbox). That
importer skips messages already in the archive, so each Takeout, which always
contains the whole mailbox, only adds what is new since the last one.

Optionally it also watches Gmail (IMAP, app password) for Google's "your data is
ready" email, and sends a notification when one arrives and again if the export
has not been imported a few days later, before Google's one-week link expires.

Standard library only. State lives in STATE_DIR/state.json; a summary of the
last run is in STATE_DIR/status.json for dashboards.
"""

import email
import email.header
import email.utils
import imaplib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.request
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

ARCHIVE_RE = re.compile(r"^takeout-.*\.(zip|tgz|tar\.gz)$", re.IGNORECASE)
# Mail-Archiver's CLI prints these lines once an import has run to the end.
RESULT_FIELDS = {
    "Status": "status",
    "Total Emails": "total",
    "Imported Successfully": "imported",
    "Failed": "failed",
    "Skipped (malformed)": "malformed",
    "Skipped (duplicates)": "duplicates",
}
COMPLETED_STATUSES = {"Completed", "CompletedWithErrors"}


def env(name, default=None):
    value = os.environ.get(name, default)
    if value is None:
        sys.exit(f"missing required environment variable {name}")
    return value


class Config:
    def __init__(self):
        self.downloads = Path(env("DOWNLOADS_DIR", "/downloads"))
        self.work = Path(env("WORK_DIR", "/work"))
        self.state_dir = Path(env("STATE_DIR", "/state"))
        self.account_id = int(env("ACCOUNT_ID"))
        self.folder = env("TARGET_FOLDER", "INBOX")
        self.poll_seconds = int(env("POLL_MINUTES", "15")) * 60
        # A download counts as finished once its size has not changed for this long.
        self.settle_seconds = int(env("SETTLE_MINUTES", "5")) * 60
        self.max_attempts = int(env("MAX_ATTEMPTS", "3"))
        self.import_cmd = env("IMPORT_CMD", "dotnet /app/MailArchiver.dll").split()
        self.import_cwd = env("IMPORT_CWD", "/app")
        self.gmail_user = os.environ.get("GMAIL_USER", "")
        self.gmail_password = os.environ.get("GMAIL_APP_PASSWORD", "")
        self.gmail_check_seconds = int(env("GMAIL_CHECK_HOURS", "6")) * 3600
        self.remind_after_days = int(env("REMIND_AFTER_DAYS", "4"))
        self.notify_url = os.environ.get("NOTIFY_URL", "")


def log(msg):
    print(f"{datetime.now().strftime('%Y-%m-%d %H:%M:%S')} {msg}", flush=True)


def now_iso():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class State:
    def __init__(self, path):
        self.path = path
        self.data = {"archives": {}, "sizes": {}, "takeout_emails": {}}
        if path.exists():
            self.data.update(json.loads(path.read_text()))

    def save(self):
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.data, indent=2, sort_keys=True))
        tmp.replace(self.path)

    @property
    def archives(self):
        return self.data["archives"]


# ---------------------------------------------------------------- downloads


def archive_key(path):
    """Identify a download by name and size, so a re-downloaded file of the same
    name but different content is treated as new."""
    return f"{path.name}:{path.stat().st_size}"


def finished_downloads(cfg, state):
    """Archives in the downloads folder that are complete and not yet handled.

    Firefox writes to <name>.part and renames on completion, but other browsers
    and copies don't, so a file also has to hold the same size across polls for
    SETTLE_MINUTES before it is touched.
    """
    ready = []
    sizes = state.data["sizes"]
    seen = set()
    for path in sorted(cfg.downloads.iterdir()):
        if not path.is_file() or not ARCHIVE_RE.match(path.name):
            continue
        if path.with_name(path.name + ".part").exists():
            continue
        seen.add(path.name)
        size = path.stat().st_size
        prev = sizes.get(path.name)
        if prev is None or prev["size"] != size:
            sizes[path.name] = {"size": size, "since": time.time()}
            continue
        if time.time() - prev["since"] < cfg.settle_seconds:
            continue
        entry = state.archives.get(archive_key(path))
        if entry and (entry["result"] == "done" or entry["attempts"] >= cfg.max_attempts):
            continue
        ready.append(path)
    for name in list(sizes):
        if name not in seen:
            del sizes[name]
    return ready


def is_mail_mbox(member_name):
    parts = member_name.replace("\\", "/").split("/")
    return parts[-1].lower().endswith(".mbox") and "Mail" in parts


def extract_mboxes(archive, dest):
    """Stream every Gmail .mbox out of a Takeout archive into dest. Returns paths."""
    dest.mkdir(parents=True, exist_ok=True)
    out = []

    def write(stream, member_name):
        target = dest / f"{len(out):02d}-{Path(member_name).name}"
        with open(target, "wb") as fh:
            shutil.copyfileobj(stream, fh, 16 * 1024 * 1024)
        out.append(target)

    if archive.name.lower().endswith(".zip"):
        with zipfile.ZipFile(archive) as zf:
            for info in zf.infolist():
                if not info.is_dir() and is_mail_mbox(info.filename):
                    with zf.open(info) as src:
                        write(src, info.filename)
    else:
        # Streaming mode: a Takeout .tgz is read once, front to back.
        with tarfile.open(archive, "r|gz") as tf:
            for member in tf:
                if member.isfile() and is_mail_mbox(member.name):
                    write(tf.extractfile(member), member.name)
    return out


def parse_results(output):
    results = {}
    for line in output.splitlines():
        label, sep, value = line.partition(":")
        field = RESULT_FIELDS.get(label.strip())
        if sep and field:
            value = value.strip()
            results[field] = value if field == "status" else int(value or 0)
    return results


def run_import(cfg, mbox):
    cmd = cfg.import_cmd + [
        "--import-mbox", "--file", str(mbox),
        "--account-id", str(cfg.account_id), "--folder", cfg.folder,
    ]
    log(f"importing {mbox.name} ({mbox.stat().st_size / 2**30:.2f} GiB)")
    proc = subprocess.Popen(cmd, cwd=cfg.import_cwd, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, errors="replace")
    lines = []
    for line in proc.stdout:
        lines.append(line)
        # Per-message progress goes to the app's logger; pass through the summary
        # and anything that looks like trouble so it shows in `docker logs`.
        if not line.startswith(("info:", "      ")) or "rror" in line:
            print(f"    {line.rstrip()}", flush=True)
    proc.wait()
    results = parse_results("".join(lines))
    results["exit_code"] = proc.returncode
    return results


def process_archive(cfg, state, archive):
    key = archive_key(archive)
    entry = state.archives.setdefault(key, {"file": archive.name, "attempts": 0})
    entry["attempts"] += 1
    entry["started"] = now_iso()
    state.save()

    work = cfg.work / archive.name
    shutil.rmtree(work, ignore_errors=True)
    try:
        log(f"extracting mail from {archive.name}")
        mboxes = extract_mboxes(archive, work)
        if not mboxes:
            # Not a Gmail export (another Takeout product): record it and move on.
            entry.update(result="done", finished=now_iso(), mboxes=[], note="no Mail/*.mbox inside")
            log(f"{archive.name}: no Gmail mbox inside, skipping")
            return entry
        runs = []
        for mbox in mboxes:
            res = run_import(cfg, mbox)
            res["mbox"] = mbox.name
            runs.append(res)
            if res.get("status") not in COMPLETED_STATUSES:
                raise RuntimeError(f"import of {mbox.name} did not complete: {res}")
        # A completed run is final even with a few failed or malformed messages:
        # running it again would only skip everything that did go in.
        entry.update(result="done", finished=now_iso(), mboxes=runs, error=None)
        totals = {f: sum(r.get(f, 0) for r in runs) for f in ("total", "imported", "duplicates", "failed", "malformed")}
        entry["totals"] = totals
        log(f"{archive.name}: done {totals}")
        return entry
    except Exception as exc:  # noqa: BLE001 - recorded, retried on a later poll
        entry.update(result="failed", finished=now_iso(), error=str(exc))
        log(f"{archive.name}: FAILED (attempt {entry['attempts']}/{cfg.max_attempts}): {exc}")
        return entry
    finally:
        shutil.rmtree(work, ignore_errors=True)
        state.save()


# ---------------------------------------------------------------- gmail + notify


def notify(cfg, title, message):
    log(f"notify: {title}: {message}")
    if not cfg.notify_url:
        return
    try:
        req = urllib.request.Request(cfg.notify_url, data=message.encode(), method="POST",
                                     headers={"Title": title, "Tags": "email"})
        urllib.request.urlopen(req, timeout=30).close()
    except Exception as exc:  # noqa: BLE001
        log(f"notify failed: {exc}")


def decode_header(value):
    return str(email.header.make_header(email.header.decode_header(value or "")))


def find_takeout_emails(cfg):
    """Google's 'your data is ready' emails from the last two weeks, via IMAP."""
    imap = imaplib.IMAP4_SSL("imap.gmail.com")
    try:
        imap.login(cfg.gmail_user, cfg.gmail_password)
        imap.select('"[Gmail]/All Mail"', readonly=True)
        # X-GM-RAW takes Gmail's own search syntax, as one quoted IMAP string.
        typ, data = imap.search(None, "X-GM-RAW", '"from:google.com subject:(data is ready) newer_than:14d"')
        found = []
        for num in (data[0].split() if typ == "OK" and data[0] else []):
            typ, msg_data = imap.fetch(num, "(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID SUBJECT DATE)])")
            if typ != "OK":
                continue
            msg = email.message_from_bytes(msg_data[0][1])
            try:
                sent = email.utils.parsedate_to_datetime(msg.get("Date")).astimezone(timezone.utc)
            except (TypeError, ValueError):
                sent = datetime.now(timezone.utc)
            found.append({
                "id": msg.get("Message-ID", "").strip() or f"num-{num.decode()}",
                "subject": decode_header(msg.get("Subject")),
                "date": sent.isoformat(),
            })
        return found
    finally:
        try:
            imap.logout()
        except Exception:  # noqa: BLE001
            pass


def check_gmail(cfg, state):
    seen = state.data["takeout_emails"]
    for mail in find_takeout_emails(cfg):
        if mail["id"] in seen:
            continue
        seen[mail["id"]] = {**mail, "notified": now_iso(), "reminded": None}
        notify(cfg, "Google Takeout ready",
               f"{mail['subject']}. Download the zip into the Firefox downloads folder "
               f"within 7 days; it will be imported into Mail Archive automatically.")
    # Remind about an export still not imported, before its link expires.
    last_import = max((a.get("finished", "") for a in state.archives.values() if a.get("result") == "done"), default="")
    for mail in seen.values():
        age = datetime.now(timezone.utc) - datetime.fromisoformat(mail["date"])
        if mail["reminded"] or age < timedelta(days=cfg.remind_after_days) or age > timedelta(days=7):
            continue
        if last_import >= mail["date"]:
            continue
        mail["reminded"] = now_iso()
        notify(cfg, "Google Takeout not imported yet",
               f"The export from {mail['date'][:10]} has not been downloaded and imported. "
               f"Its download link expires about {(datetime.fromisoformat(mail['date']) + timedelta(days=7)).date()}.")
    state.data["gmail_checked"] = now_iso()
    state.save()


# ---------------------------------------------------------------- main loop


def write_status(cfg, state):
    done = [a for a in state.archives.values() if a.get("result") == "done" and a.get("totals")]
    last = max(done, key=lambda a: a["finished"], default=None)
    failed = [a for a in state.archives.values() if a.get("result") == "failed"]
    status = {
        "updated": now_iso(),
        "last_import": last["finished"] if last else None,
        "last_import_file": last["file"] if last else None,
        "last_import_totals": last["totals"] if last else None,
        "failed_archives": [{"file": a["file"], "attempts": a["attempts"], "error": a.get("error")} for a in failed],
        "gmail_checked": state.data.get("gmail_checked"),
    }
    (cfg.state_dir / "status.json").write_text(json.dumps(status, indent=2))


def run_once(cfg, state):
    for archive in finished_downloads(cfg, state):
        entry = process_archive(cfg, state, archive)
        if entry["result"] == "done" and entry.get("totals"):
            t = entry["totals"]
            notify(cfg, "Mail Archive import finished",
                   f"{archive.name}: {t['imported']} new, {t['duplicates']} already archived, "
                   f"{t['failed'] + t['malformed']} not imported.")
        elif entry["result"] == "failed" and entry["attempts"] >= cfg.max_attempts:
            notify(cfg, "Mail Archive import FAILED",
                   f"{archive.name} failed {entry['attempts']} times: {entry.get('error')}")
    state.save()
    write_status(cfg, state)


def main():
    cfg = Config()
    cfg.work.mkdir(parents=True, exist_ok=True)
    cfg.state_dir.mkdir(parents=True, exist_ok=True)
    state = State(cfg.state_dir / "state.json")
    once = "--once" in sys.argv
    gmail = bool(cfg.gmail_user and cfg.gmail_password)
    log(f"watching {cfg.downloads} every {cfg.poll_seconds // 60} min for account {cfg.account_id}; "
        f"gmail check {'on' if gmail else 'off'}")
    next_gmail = 0.0
    while True:
        if gmail and time.time() >= next_gmail:
            try:
                check_gmail(cfg, state)
            except Exception as exc:  # noqa: BLE001
                log(f"gmail check failed: {exc}")
            next_gmail = time.time() + cfg.gmail_check_seconds
        try:
            run_once(cfg, state)
        except Exception as exc:  # noqa: BLE001
            log(f"poll failed: {exc}")
        if once:
            return
        time.sleep(cfg.poll_seconds)


if __name__ == "__main__":
    main()
