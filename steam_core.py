"""Steam path detection, firewall blocking, processes, downloads, and power actions."""

from __future__ import annotations

import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import winreg
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

FIREWALL_PREFIX = "SteamStopper"
STEAM_PROCESS_NAMES = (
    "steam.exe",
    "steamwebhelper.exe",
    "steamerrorreporter.exe",
    "gameoverlayui.exe",
    "streaming_client.exe",
)
CLIENT_EXES = (
    "steam.exe",
    "steamwebhelper.exe",
    "steamerrorreporter.exe",
    "GameOverlayUI.exe",
    "streaming_client.exe",
)


def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin() -> None:
    params = " ".join(f'"{arg}"' for arg in sys.argv)
    ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, None, 1)


def _reg_steam_path() -> Path | None:
    keys = [
        (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
    ]
    for hive, subkey, name in keys:
        try:
            with winreg.OpenKey(hive, subkey) as key:
                value, _ = winreg.QueryValueEx(key, name)
            path = Path(str(value).replace("/", "\\"))
            if path.exists():
                return path
        except OSError:
            continue
    return None


def find_steam_root() -> Path | None:
    found = _reg_steam_path()
    if found:
        return found
    for candidate in (
        Path(r"C:\Program Files (x86)\Steam"),
        Path(r"C:\Program Files\Steam"),
        Path(os.path.expandvars(r"%ProgramFiles(x86)%\Steam")),
    ):
        if (candidate / "steam.exe").exists():
            return candidate
    return None


def parse_vdf_simple(text: str) -> dict:
    """Minimal VDF parser for libraryfolders and appmanifest files."""
    tokens = re.findall(r'"((?:\\.|[^"\\])*)"|(\{)|(\})', text)

    def parse_object(index: int) -> tuple[dict, int]:
        obj: dict = {}
        last_key: str | None = None
        while index < len(tokens):
            quoted, open_b, close_b = tokens[index]
            if close_b:
                return obj, index + 1
            if open_b:
                if last_key is None:
                    nested, index = parse_object(index + 1)
                    obj.setdefault("_unnamed", []).append(nested)
                else:
                    nested, index = parse_object(index + 1)
                    obj[last_key] = nested
                    last_key = None
                continue
            value = quoted.encode("utf-8").decode("unicode_escape")
            if last_key is None:
                last_key = value
            else:
                obj[last_key] = value
                last_key = None
            index += 1
        return obj, index

    root, _ = parse_object(0)
    return root


def _extract_library_paths(steam_root: Path) -> list[Path]:
    paths = [steam_root]
    vdf = steam_root / "steamapps" / "libraryfolders.vdf"
    if not vdf.exists():
        return paths
    try:
        data = parse_vdf_simple(vdf.read_text(encoding="utf-8", errors="ignore"))
    except Exception:
        return paths

    folders = data.get("libraryfolders", data)
    if not isinstance(folders, dict):
        return paths

    for key, value in folders.items():
        if key.startswith("_"):
            continue
        if isinstance(value, dict):
            raw = value.get("path")
        else:
            raw = value if key.isdigit() or key == "path" else None
        if not raw:
            continue
        lib = Path(str(raw).replace("\\\\", "\\"))
        if lib.exists() and lib not in paths:
            paths.append(lib)
    return paths


@dataclass
class GameEntry:
    appid: str
    name: str
    size_bytes: int
    install_dir: str
    library: str
    acf_path: str


def list_installed_games(steam_root: Path) -> list[GameEntry]:
    games: list[GameEntry] = []
    for library in _extract_library_paths(steam_root):
        apps = library / "steamapps"
        if not apps.exists():
            continue
        for acf in apps.glob("appmanifest_*.acf"):
            try:
                text = acf.read_text(encoding="utf-8", errors="ignore")
                data = parse_vdf_simple(text)
                app = data.get("AppState", data)
                if not isinstance(app, dict):
                    continue
                name = str(app.get("name", acf.stem))
                appid = str(app.get("appid", acf.stem.replace("appmanifest_", "")))
                size = int(str(app.get("SizeOnDisk", "0") or "0"))
                install_dir = str(app.get("installdir", ""))
                games.append(
                    GameEntry(
                        appid=appid,
                        name=name,
                        size_bytes=size,
                        install_dir=install_dir,
                        library=str(library),
                        acf_path=str(acf),
                    )
                )
            except Exception:
                continue
    games.sort(key=lambda g: g.name.lower())
    return games


def folder_size(path: Path) -> int:
    total = 0
    if not path.exists():
        return 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            try:
                total += (Path(root) / name).stat().st_size
            except OSError:
                pass
    return total


def format_bytes(num: int) -> str:
    value = float(num)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if value < 1024 or unit == "TB":
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{num} B"


def steam_exe_paths(steam_root: Path) -> list[Path]:
    found = []
    for name in CLIENT_EXES:
        path = steam_root / name
        if path.exists():
            found.append(path)
    return found


def game_exe_paths(steam_root: Path, limit: int = 400) -> list[Path]:
    exes: list[Path] = []
    for library in _extract_library_paths(steam_root):
        common = library / "steamapps" / "common"
        if not common.exists():
            continue
        for exe in common.rglob("*.exe"):
            exes.append(exe)
            if len(exes) >= limit:
                return exes
    return exes


def _run(cmd: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=subprocess.CREATE_NO_WINDOW,
    )


def _run_netsh(args: list[str]) -> subprocess.CompletedProcess:
    return _run(["netsh", *args])


def firewall_rule_names() -> list[str]:
    result = _run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            (
                "Get-NetFirewallRule -ErrorAction SilentlyContinue | "
                f"Where-Object {{ $_.DisplayName -like '{FIREWALL_PREFIX}*' }} | "
                "Select-Object -ExpandProperty DisplayName"
            ),
        ]
    )
    names = [line.strip() for line in (result.stdout or "").splitlines() if line.strip()]
    if names:
        return list(dict.fromkeys(names))
    result = _run_netsh(["advfirewall", "firewall", "show", "rule", "name=all"])
    fallback = []
    for line in (result.stdout or "").splitlines():
        if line.strip().startswith("Rule Name:"):
            name = line.split(":", 1)[1].strip()
            if name.startswith(FIREWALL_PREFIX):
                fallback.append(name)
    return fallback


def is_blocked() -> bool:
    return bool(firewall_rule_names())


def _add_block_rule(program: Path, direction: str) -> None:
    safe = re.sub(r"[^A-Za-z0-9._-]+", "_", program.name)
    digest = hashlib.sha1(str(program).lower().encode("utf-8")).hexdigest()[:10]
    name = f"{FIREWALL_PREFIX}-{direction}-{safe}-{digest}"
    _run_netsh(
        [
            "advfirewall",
            "firewall",
            "add",
            "rule",
            f"name={name}",
            f"dir={direction}",
            "action=block",
            f"program={program}",
            "enable=yes",
            "profile=any",
        ]
    )


def block_steam(steam_root: Path, include_games: bool = False) -> int:
    unblock_steam()
    programs = steam_exe_paths(steam_root)
    if include_games:
        programs.extend(game_exe_paths(steam_root))
    unique = []
    seen = set()
    for program in programs:
        key = str(program).lower()
        if key not in seen:
            seen.add(key)
            unique.append(program)
    for program in unique:
        _add_block_rule(program, "out")
        _add_block_rule(program, "in")
    return len(unique)


def unblock_steam() -> int:
    names = firewall_rule_names()
    for name in names:
        _run_netsh(["advfirewall", "firewall", "delete", "rule", f"name={name}"])
    return len(names)


def running_steam_processes() -> list[dict]:
    proc_names = [n[:-4] if n.lower().endswith(".exe") else n for n in STEAM_PROCESS_NAMES]
    listed = ",".join(f"'{n}'" for n in proc_names)
    result = _run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            (
                f"$names = @({listed}); "
                "Get-Process -ErrorAction SilentlyContinue | "
                "Where-Object { $names -contains $_.Name } | "
                "ForEach-Object { '{0}|{1}|{2}' -f $_.Name, $_.Id, $_.WorkingSet64 }"
            ),
        ]
    )
    rows = []
    for line in (result.stdout or "").splitlines():
        parts = line.strip().split("|")
        if len(parts) != 3:
            continue
        name, pid, working = parts
        try:
            size = format_bytes(int(working))
        except ValueError:
            size = working
        rows.append({"name": f"{name}.exe", "pid": pid, "memory": size})
    return rows


def kill_steam() -> int:
    killed = 0
    for name in STEAM_PROCESS_NAMES:
        result = _run(["taskkill", "/F", "/IM", name])
        if result.returncode == 0:
            killed += 1
    return killed


def launch_steam(steam_root: Path, offline: bool = False) -> None:
    exe = steam_root / "steam.exe"
    args = [str(exe)]
    if offline:
        args.append("-offline")
    subprocess.Popen(args, cwd=str(steam_root))


def set_wants_offline_mode(steam_root: Path, enabled: bool) -> bool:
    config = steam_root / "config" / "loginusers.vdf"
    if not config.exists():
        return False
    text = config.read_text(encoding="utf-8", errors="ignore")
    replacement = '"WantsOfflineMode"\t\t"{}"'.format("1" if enabled else "0")
    new_text, count = re.subn(
        r'"WantsOfflineMode"\s*"\d+"',
        replacement.replace("\t\t", "\t\t"),
        text,
    )
    if count == 0:
        new_text = re.sub(
            r'("MostRecent"\s*"\d+")',
            replacement + "\n\t\t\\1",
            text,
            count=1,
        )
    config.write_text(new_text, encoding="utf-8")
    return True


def cache_targets(steam_root: Path) -> dict[str, Path]:
    return {
        "Downloading": steam_root / "steamapps" / "downloading",
        "Temp": steam_root / "steamapps" / "temp",
        "HTML cache": steam_root / "config" / "htmlcache",
        "Shader cache (client)": steam_root / "steam" / "cached",
        "Logs": steam_root / "logs",
        "Dump": steam_root / "dumps",
    }


def clear_path(path: Path) -> int:
    removed = 0
    if not path.exists():
        return 0
    if path.is_file():
        path.unlink(missing_ok=True)
        return 1
    for child in path.iterdir():
        try:
            if child.is_dir():
                shutil.rmtree(child, ignore_errors=True)
            else:
                child.unlink(missing_ok=True)
            removed += 1
        except OSError:
            pass
    return removed


def backup_config(steam_root: Path, dest_dir: Path | None = None) -> Path:
    dest_dir = dest_dir or Path.home() / "Documents" / "SteamStopperBackups"
    dest_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    archive = dest_dir / f"steam-config-{stamp}"
    config = steam_root / "config"
    userdata = steam_root / "userdata"
    staging = dest_dir / f"_staging-{stamp}"
    staging.mkdir(parents=True, exist_ok=True)
    if config.exists():
        shutil.copytree(config, staging / "config", dirs_exist_ok=True)
    if userdata.exists():
        shutil.copytree(userdata, staging / "userdata", dirs_exist_ok=True)
    zip_path = shutil.make_archive(str(archive), "zip", staging)
    shutil.rmtree(staging, ignore_errors=True)
    return Path(zip_path)


def open_folder(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    os.startfile(path)  # type: ignore[attr-defined]


def userdata_accounts(steam_root: Path) -> list[Path]:
    root = steam_root / "userdata"
    if not root.exists():
        return []
    return [p for p in root.iterdir() if p.is_dir() and p.name.isdigit()]


# --------------------------------------------------------------------- downloads

# Steam EAppState bits, as written to appmanifest_*.acf StateFlags.
STATE_UPDATE_REQUIRED = 2
STATE_FILES_MISSING = 32
STATE_UPDATE_RUNNING = 256
STATE_UPDATE_PAUSED = 512
STATE_UPDATE_STARTED = 1024
STATE_VALIDATING = 65536
STATE_ADDING_FILES = 131072
STATE_PREALLOCATING = 262144
STATE_DOWNLOADING = 524288
STATE_STAGING = 1048576
STATE_COMMITTING = 2097152

ACTIVE_STATE_MASK = (
    STATE_UPDATE_RUNNING
    | STATE_UPDATE_STARTED
    | STATE_VALIDATING
    | STATE_ADDING_FILES
    | STATE_PREALLOCATING
    | STATE_DOWNLOADING
    | STATE_STAGING
    | STATE_COMMITTING
)


def _to_int(value, default: int = 0) -> int:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return default


@dataclass
class DownloadJob:
    appid: str
    name: str
    downloaded: int
    total: int
    staged: int
    to_stage: int
    state_flags: int
    library: str

    @property
    def pending_bytes(self) -> int:
        return max(0, self.total - self.downloaded)

    @property
    def percent(self) -> float:
        if self.total <= 0:
            return 0.0
        return min(1.0, self.downloaded / self.total)

    @property
    def is_paused(self) -> bool:
        return bool(self.state_flags & STATE_UPDATE_PAUSED)

    @property
    def is_active(self) -> bool:
        return bool(self.state_flags & ACTIVE_STATE_MASK)

    @property
    def status(self) -> str:
        flags = self.state_flags
        if flags & STATE_COMMITTING:
            return "Committing"
        if flags & STATE_STAGING:
            return "Staging"
        if flags & STATE_VALIDATING:
            return "Validating"
        if flags & STATE_PREALLOCATING:
            return "Preallocating"
        if flags & (STATE_DOWNLOADING | STATE_ADDING_FILES):
            return "Downloading"
        if self.is_paused:
            return "Paused"
        if flags & (STATE_UPDATE_RUNNING | STATE_UPDATE_STARTED):
            return "Updating"
        if flags & STATE_FILES_MISSING:
            return "Repairing"
        if flags & STATE_UPDATE_REQUIRED:
            return "Queued"
        return "Pending"


def list_download_jobs(steam_root: Path, include_queued: bool = True) -> list[DownloadJob]:
    """Jobs Steam still has work left on, read from appmanifest files."""
    jobs: list[DownloadJob] = []
    for library in _extract_library_paths(steam_root):
        apps = library / "steamapps"
        if not apps.exists():
            continue
        for acf in apps.glob("appmanifest_*.acf"):
            try:
                data = parse_vdf_simple(acf.read_text(encoding="utf-8", errors="ignore"))
            except OSError:
                continue
            app = data.get("AppState", data)
            if not isinstance(app, dict):
                continue

            flags = _to_int(app.get("StateFlags"))
            downloaded = _to_int(app.get("BytesDownloaded"))
            total = _to_int(app.get("BytesToDownload"))
            staged = _to_int(app.get("BytesStaged"))
            to_stage = _to_int(app.get("BytesToStage"))

            active = bool(flags & ACTIVE_STATE_MASK)
            has_pending_bytes = total > 0 and downloaded < total
            queued = bool(flags & (STATE_UPDATE_REQUIRED | STATE_UPDATE_PAUSED | STATE_FILES_MISSING))
            if not (active or has_pending_bytes or (include_queued and queued)):
                continue

            jobs.append(
                DownloadJob(
                    appid=str(app.get("appid", acf.stem.replace("appmanifest_", ""))),
                    name=str(app.get("name", acf.stem)),
                    downloaded=downloaded,
                    total=total,
                    staged=staged,
                    to_stage=to_stage,
                    state_flags=flags,
                    library=str(library),
                )
            )
    jobs.sort(key=lambda j: (not j.is_active, j.name.lower()))
    return jobs


@dataclass
class DownloadSnapshot:
    jobs: list[DownloadJob] = field(default_factory=list)
    downloaded: int = 0
    total: int = 0
    remaining: int = 0
    speed_bps: float = 0.0
    eta_seconds: float | None = None
    active: bool = False
    seen_activity: bool = False
    idle_seconds: float = 0.0
    finished: bool = False

    @property
    def percent(self) -> float:
        if self.total <= 0:
            return 0.0
        return min(1.0, self.downloaded / self.total)


class DownloadWatcher:
    """Samples appmanifest byte counters to derive speed, ETA, and completion."""

    def __init__(
        self,
        steam_root: Path,
        include_queued: bool = True,
        grace_seconds: float = 45.0,
        require_activity_first: bool = True,
        speed_window: float = 25.0,
    ) -> None:
        self.steam_root = steam_root
        self.include_queued = include_queued
        self.grace_seconds = grace_seconds
        self.require_activity_first = require_activity_first
        self.speed_window = speed_window
        self._last_bytes: dict[str, int] = {}
        self._samples: deque[tuple[float, int]] = deque()
        self.seen_activity = False
        self._idle_since: float | None = None

    def reset(self) -> None:
        self._last_bytes.clear()
        self._samples.clear()
        self.seen_activity = False
        self._idle_since = None

    def poll(self) -> DownloadSnapshot:
        now = time.monotonic()
        jobs = list_download_jobs(self.steam_root, include_queued=self.include_queued)

        current = {job.appid: job.downloaded + job.staged for job in jobs}
        delta = sum(max(0, value - self._last_bytes.get(appid, value)) for appid, value in current.items())
        self._last_bytes = current

        self._samples.append((now, delta))
        while self._samples and now - self._samples[0][0] > self.speed_window:
            self._samples.popleft()

        speed = 0.0
        if len(self._samples) >= 2:
            span = now - self._samples[0][0]
            if span > 0:
                speed = sum(sample for _t, sample in list(self._samples)[1:]) / span

        moving = delta > 0
        state_active = any(job.is_active for job in jobs)
        pending = bool(jobs)
        active = moving or state_active

        if active:
            self.seen_activity = True
        if pending:
            self._idle_since = None
        elif self._idle_since is None:
            self._idle_since = now

        idle = 0.0 if self._idle_since is None else now - self._idle_since
        finished = (
            not pending
            and idle >= self.grace_seconds
            and (self.seen_activity or not self.require_activity_first)
        )

        downloaded = sum(job.downloaded for job in jobs)
        total = sum(job.total for job in jobs)
        remaining = sum(job.pending_bytes for job in jobs)
        eta = remaining / speed if speed > 0 and remaining > 0 else None

        return DownloadSnapshot(
            jobs=jobs,
            downloaded=downloaded,
            total=total,
            remaining=remaining,
            speed_bps=speed,
            eta_seconds=eta,
            active=active,
            seen_activity=self.seen_activity,
            idle_seconds=idle,
            finished=finished,
        )


def format_speed(bps: float) -> str:
    if bps <= 0:
        return "0 MB/s"
    return f"{bps / (1024 * 1024):.1f} MB/s"


def format_duration(seconds: float | None) -> str:
    if seconds is None:
        return "--"
    seconds = int(max(0, seconds))
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    if hours:
        return f"{hours}h {minutes:02d}m"
    if minutes:
        return f"{minutes}m {secs:02d}s"
    return f"{secs}s"


# ------------------------------------------------------------------ power actions

POWER_ACTIONS: dict[str, str] = {
    "notify": "Just notify me",
    "exit_steam": "Exit Steam",
    "block_internet": "Block Steam internet",
    "lock": "Lock Windows",
    "sleep": "Sleep",
    "hibernate": "Hibernate",
    "signout": "Sign out",
    "restart": "Restart PC",
    "shutdown": "Shut down PC",
}


def run_power_action(action: str, steam_root: Path | None = None) -> str:
    if action == "notify":
        return "Downloads finished."
    if action == "exit_steam":
        killed = kill_steam()
        return f"Downloads finished, Steam closed ({killed} process name(s))."
    if action == "block_internet":
        if steam_root is None:
            return "Downloads finished, but Steam path is unknown."
        count = block_steam(steam_root)
        kill_steam()
        return f"Downloads finished, blocked {count} Steam binary path(s)."
    if action == "lock":
        ctypes.windll.user32.LockWorkStation()
        return "Downloads finished, workstation locked."
    if action in ("sleep", "hibernate"):
        hibernate = action == "hibernate"
        ctypes.windll.powrprof.SetSuspendState(int(hibernate), 1, 0)
        return f"Downloads finished, entering {action}."
    if action == "signout":
        _run(["shutdown", "/l"])
        return "Downloads finished, signing out."
    if action == "restart":
        _run(["shutdown", "/r", "/f", "/t", "0"])
        return "Downloads finished, restarting."
    if action == "shutdown":
        _run(["shutdown", "/s", "/f", "/t", "0"])
        return "Downloads finished, shutting down."
    return f"Unknown action: {action}"


def cancel_pending_shutdown() -> bool:
    return _run(["shutdown", "/a"]).returncode == 0


# ---------------------------------------------------------------------- settings

SETTINGS_PATH = Path(os.environ.get("APPDATA", str(Path.home()))) / "SteamStopper" / "settings.json"

DEFAULT_SETTINGS = {
    "auto_action": "shutdown",
    "countdown_seconds": 60,
    "grace_seconds": 45,
    "include_queued": True,
    "require_activity_first": True,
    "block_game_exes": False,
    "kill_after_block": True,
}


def load_settings() -> dict:
    settings = dict(DEFAULT_SETTINGS)
    try:
        stored = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        if isinstance(stored, dict):
            settings.update({k: v for k, v in stored.items() if k in DEFAULT_SETTINGS})
    except (OSError, ValueError):
        pass
    return settings


def save_settings(settings: dict) -> None:
    try:
        SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
        SETTINGS_PATH.write_text(json.dumps(settings, indent=2), encoding="utf-8")
    except OSError:
        pass
