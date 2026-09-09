"""Steam Stopper — internet kill switch, download watcher, and Steam tools."""

from __future__ import annotations

import sys
import threading
import time
import tkinter as tk
import winsound
from pathlib import Path
from tkinter import messagebox, ttk

import customtkinter as ctk

import steam_core as core
import ui_kit as ui

POLL_SECONDS = 2.0

PAGES = (
    ("dashboard", "Dashboard"),
    ("downloads", "Downloads"),
    ("stopper", "Internet Stopper"),
    ("process", "Processes"),
    ("library", "Library"),
    ("tools", "Maintenance"),
)

CACHE_TARGETS = {
    "Downloading": ("steamapps", "downloading"),
    "Temp": ("steamapps", "temp"),
    "HTML cache": ("config", "htmlcache"),
    "Logs": ("logs",),
    "Dumps": ("dumps",),
}

COUNTDOWN_CHOICES = ("Immediately", "15 seconds", "30 seconds", "60 seconds", "2 minutes", "5 minutes")
GRACE_CHOICES = ("15 seconds", "30 seconds", "45 seconds", "60 seconds", "2 minutes")


def _seconds_from_choice(label: str) -> int:
    mapping = {
        "Immediately": 0,
        "15 seconds": 15,
        "30 seconds": 30,
        "45 seconds": 45,
        "60 seconds": 60,
        "2 minutes": 120,
        "5 minutes": 300,
    }
    return mapping.get(label, 60)


def _choice_from_seconds(seconds: int, choices: tuple[str, ...]) -> str:
    for label in choices:
        if _seconds_from_choice(label) == seconds:
            return label
    return choices[len(choices) // 2]


class SteamStopperApp(ctk.CTk):
    def __init__(self) -> None:
        super().__init__()
        ctk.set_appearance_mode("dark")
        self.title("Steam Stopper")
        self.geometry("1240x820")
        self.minsize(1080, 720)
        self.configure(fg_color=ui.BG)

        self.settings = core.load_settings()
        self.steam_root = core.find_steam_root()
        self.status_var = tk.StringVar(value="Ready.")

        self.pages: dict[str, ui.NativeScroll] = {}
        self.nav_items: dict[str, ui.NavButton] = {}
        self.games: list[core.GameEntry] = []
        self.game_index: dict[str, core.GameEntry] = {}
        self.current_page = "dashboard"
        self._known_jobs: set[str] = set()

        self.armed = False
        self.countdown_left: int | None = None
        self.last_snapshot: core.DownloadSnapshot | None = None
        self._watch_thread_running = True
        self.watcher = (
            core.DownloadWatcher(
                self.steam_root,
                include_queued=bool(self.settings["include_queued"]),
                grace_seconds=float(self.settings["grace_seconds"]),
                require_activity_first=bool(self.settings["require_activity_first"]),
            )
            if self.steam_root
            else None
        )

        self._build_shell()
        self._build_dashboard()
        self._build_downloads()
        self._build_stopper()
        self._build_process()
        self._build_library()
        self._build_tools()

        self.show_page("dashboard")
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(120, self._boot)

    def _boot(self) -> None:
        """Background work, started once the event loop is actually running."""
        self.refresh_status_chips()
        self.refresh_processes()
        self._run_bg(self._scan_library)
        self._start_watch_thread()

    def _post(self, fn, *args) -> None:
        """Schedule a callback on the UI thread, tolerating a closing window."""
        try:
            self.after(0, fn, *args)
        except (RuntimeError, tk.TclError):
            pass

    # ------------------------------------------------------------------ shell

    def _build_shell(self) -> None:
        ui.apply_tree_style(self)
        sidebar = ctk.CTkFrame(self, width=232, fg_color=ui.SIDEBAR, corner_radius=0)
        sidebar.pack(side="left", fill="y")
        sidebar.pack_propagate(False)

        brand = ctk.CTkFrame(sidebar, fg_color="transparent")
        brand.pack(fill="x", padx=18, pady=(24, 26))
        mark = ctk.CTkLabel(
            brand,
            text="S",
            font=ui.font(20, "bold"),
            text_color=ui.BG,
            fg_color=ui.ACCENT,
            corner_radius=10,
            width=38,
            height=38,
        )
        mark.pack(side="left")
        text = ctk.CTkFrame(brand, fg_color="transparent")
        text.pack(side="left", padx=12)
        ctk.CTkLabel(text, text="Steam Stopper", font=ui.font(15, "bold"), text_color=ui.TEXT).pack(anchor="w")
        ctk.CTkLabel(text, text="local client control", font=ui.font(10), text_color=ui.FAINT).pack(anchor="w")

        for key, label in PAGES:
            item = ui.NavButton(sidebar, label, lambda k=key: self.show_page(k))
            item.pack(fill="x", padx=10, pady=2)
            self.nav_items[key] = item

        footer = ctk.CTkFrame(sidebar, fg_color="transparent")
        footer.pack(side="bottom", fill="x", padx=14, pady=16)
        self.admin_chip = ui.Chip(footer, text="checking rights", color=ui.MUTED)
        self.admin_chip.pack(anchor="w", pady=(0, 10))
        self.elevate_button = ui.ghost_button(footer, "Relaunch as Administrator", self._elevate)
        self.elevate_button.pack(fill="x")

        main = ctk.CTkFrame(self, fg_color=ui.BG)
        main.pack(side="right", fill="both", expand=True)

        header = ctk.CTkFrame(main, fg_color=ui.BG, height=76)
        header.pack(fill="x", padx=26, pady=(20, 0))
        header.pack_propagate(False)

        titles = ctk.CTkFrame(header, fg_color="transparent")
        titles.pack(side="left", anchor="w")
        self.page_title = ctk.CTkLabel(titles, text="Dashboard", font=ui.font(24, "bold"), text_color=ui.TEXT)
        self.page_title.pack(anchor="w")
        self.page_path = ctk.CTkLabel(titles, text="", font=ui.font(11), text_color=ui.FAINT)
        self.page_path.pack(anchor="w", pady=(2, 0))

        chips = ctk.CTkFrame(header, fg_color="transparent")
        chips.pack(side="right", anchor="e")
        self.firewall_chip = ui.Chip(chips, text="firewall", color=ui.MUTED)
        self.firewall_chip.pack(side="left", padx=5)
        self.download_chip = ui.Chip(chips, text="downloads", color=ui.MUTED)
        self.download_chip.pack(side="left", padx=5)
        self.auto_chip = ui.Chip(chips, text="auto: off", color=ui.MUTED)
        self.auto_chip.pack(side="left", padx=5)

        self.body = ctk.CTkFrame(main, fg_color=ui.BG)
        self.body.pack(fill="both", expand=True, padx=26, pady=(14, 8))
        self.body.grid_rowconfigure(0, weight=1)
        self.body.grid_columnconfigure(0, weight=1)
        self.bind_all("<MouseWheel>", self._on_mousewheel)

        status = ctk.CTkFrame(main, fg_color=ui.SIDEBAR, height=34, corner_radius=0)
        status.pack(side="bottom", fill="x")
        ctk.CTkLabel(status, textvariable=self.status_var, font=ui.font(11), text_color=ui.MUTED).pack(
            side="left", padx=20
        )

    def _page(self, key: str):
        holder = ui.NativeScroll(self.body, page_key=key, app=self)
        holder.grid(row=0, column=0, sticky="nsew")
        self.pages[key] = holder
        return holder.body

    def show_page(self, key: str) -> None:
        entering = key != self.current_page
        self.current_page = key
        for name, holder in self.pages.items():
            if name == key:
                holder.grid()
                holder.tkraise()
            else:
                holder.grid_remove()
        for name, item in self.nav_items.items():
            item.set_active(name == key)
        label = next(title for k, title in PAGES if k == key)
        self.page_title.configure(text=label)
        self.page_path.configure(text=str(self.steam_root) if self.steam_root else "Steam install not found")
        if entering and key == "downloads" and self.last_snapshot is not None:
            self._paint_downloads(self.last_snapshot)
        elif entering and key == "process":
            self.refresh_processes()

    def _on_mousewheel(self, event) -> None:
        widget = self.winfo_containing(event.x_root, event.y_root)
        current = widget
        while current is not None:
            if isinstance(current, ttk.Treeview):
                return
            current = getattr(current, "master", None)
        page = self.pages.get(self.current_page)
        if page is not None:
            page.canvas.yview_scroll(int(-event.delta / 120), "units")

    # -------------------------------------------------------------- dashboard

    def _build_dashboard(self) -> None:
        page = self._page("dashboard")

        tiles = tk.Frame(page, bg=ui.BG)
        tiles.pack(fill="x")
        tiles.columnconfigure((0, 1, 2, 3), weight=1, uniform="tiles")
        self.tile_firewall = ui.StatTile(tiles, "Steam network", "--")
        self.tile_client = ui.StatTile(tiles, "Steam client", "--")
        self.tile_downloads = ui.StatTile(tiles, "Active downloads", "--")
        self.tile_library = ui.StatTile(tiles, "Library on disk", "--")
        for column, tile in enumerate((self.tile_firewall, self.tile_client, self.tile_downloads, self.tile_library)):
            tile.grid(row=0, column=column, sticky="nsew", padx=(0 if column == 0 else 8, 0))

        quick = ui.Card(page, "Quick actions", "One-click controls for the Steam client")
        quick.pack(fill="x", pady=(14, 0))
        row = tk.Frame(quick.body, bg=ui.CARD)
        row.pack(fill="x")
        ui.primary_button(row, "Block internet", self._start_block, ui.RED, width=150).pack(
            side="left", padx=(0, 8)
        )
        ui.primary_button(row, "Restore internet", self._start_unblock, ui.GREEN, width=150).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(row, "Kill Steam", self._kill, height=40, width=110).pack(side="left", padx=(0, 8))
        ui.ghost_button(row, "Launch Steam", lambda: self._launch(False), height=40, width=120).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(row, "Launch offline", lambda: self._launch(True), height=40, width=120).pack(side="left")

        summary = ui.Card(page, "Auto-shutdown watcher", "Runs an action once Steam finishes all pending work")
        summary.pack(fill="x", pady=(14, 0))
        self.dash_auto = tk.Label(
            summary.body, text="Watcher is idle.", font=ui.tk_font(11), fg=ui.MUTED, bg=ui.CARD, anchor="w", justify="left"
        )
        self.dash_auto.pack(fill="x")
        ui.ghost_button(summary.body, "Open Downloads page", lambda: self.show_page("downloads"), width=180).pack(
            anchor="w", pady=(12, 0)
        )

        folders = ui.Card(page, "Quick folders", "Jump straight into the Steam directories")
        folders.pack(fill="x", pady=(14, 0))
        grid = tk.Frame(folders.body, bg=ui.CARD)
        grid.pack(fill="x")
        items = (
            ("Steam root", ()),
            ("steamapps", ("steamapps",)),
            ("Config", ("config",)),
            ("Userdata", ("userdata",)),
            ("Logs", ("logs",)),
            ("Backups", None),
        )
        for index, (label, parts) in enumerate(items):
            grid.columnconfigure(index % 3, weight=1, uniform="folders")
            ui.ghost_button(
                grid,
                label,
                lambda p=parts: self._open_folder(p),
                height=38,
            ).grid(row=index // 3, column=index % 3, sticky="ew", padx=(0 if index % 3 == 0 else 8, 0), pady=4)

    # -------------------------------------------------------------- downloads

    def _build_downloads(self) -> None:
        page = self._page("downloads")

        hero = ui.Card(page)
        hero.pack(fill="x")
        head = tk.Frame(hero.body, bg=ui.CARD)
        head.pack(fill="x")
        self.dl_headline = tk.Label(head, text="Watching for downloads…", font=ui.tk_font(16, "bold"), fg=ui.TEXT, bg=ui.CARD, anchor="w")
        self.dl_headline.pack(side="left")
        self.dl_state_chip = ui.Chip(head, text="idle", color=ui.MUTED)
        self.dl_state_chip.pack(side="right")

        self.dl_bar = ctk.CTkProgressBar(
            hero.body, height=10, corner_radius=5, fg_color=ui.ELEVATED, progress_color=ui.ACCENT, bg_color=ui.CARD
        )
        self.dl_bar.set(0)
        self.dl_bar.pack(fill="x", pady=(14, 12))

        stats = tk.Frame(hero.body, bg=ui.CARD)
        stats.pack(fill="x")
        stats.columnconfigure((0, 1, 2, 3), weight=1, uniform="dl")
        self.tile_speed = ui.StatTile(stats, "Speed", "0 MB/s", ui.ACCENT)
        self.tile_remaining = ui.StatTile(stats, "Remaining", "--")
        self.tile_eta = ui.StatTile(stats, "Time left", "--")
        self.tile_jobs = ui.StatTile(stats, "Jobs", "0")
        for column, tile in enumerate((self.tile_speed, self.tile_remaining, self.tile_eta, self.tile_jobs)):
            tile.grid(row=0, column=column, sticky="nsew", padx=(0 if column == 0 else 8, 0))

        self.sparkline = ui.Sparkline(hero.body)
        self.sparkline.pack(fill="x", pady=(12, 0))

        auto = ui.Card(page, "When downloads finish", "The action runs after a countdown you can cancel")
        auto.pack(fill="x", pady=(14, 0))

        controls = tk.Frame(auto.body, bg=ui.CARD)
        controls.pack(fill="x")
        controls.columnconfigure((0, 1, 2), weight=1, uniform="auto")

        self.action_var = ctk.StringVar(
            value=core.POWER_ACTIONS.get(self.settings["auto_action"], core.POWER_ACTIONS["shutdown"])
        )
        self.countdown_var = ctk.StringVar(
            value=_choice_from_seconds(int(self.settings["countdown_seconds"]), COUNTDOWN_CHOICES)
        )
        self.grace_var = ctk.StringVar(value=_choice_from_seconds(int(self.settings["grace_seconds"]), GRACE_CHOICES))

        self._labeled_menu(controls, 0, "Action", list(core.POWER_ACTIONS.values()), self.action_var)
        self._labeled_menu(controls, 1, "Countdown before action", list(COUNTDOWN_CHOICES), self.countdown_var)
        self._labeled_menu(controls, 2, "Idle time that counts as done", list(GRACE_CHOICES), self.grace_var)

        self.switch_queued = ctk.CTkSwitch(
            auto.body,
            text="Treat queued and paused updates as unfinished work",
            font=ui.font(12),
            text_color=ui.TEXT,
            progress_color=ui.ACCENT,
            bg_color=ui.CARD,
            command=self._apply_watch_settings,
        )
        self.switch_queued.pack(anchor="w", pady=(16, 6))
        self.switch_seen = ctk.CTkSwitch(
            auto.body,
            text="Only fire after a real download has been seen (avoids instant trigger)",
            font=ui.font(12),
            text_color=ui.TEXT,
            progress_color=ui.ACCENT,
            bg_color=ui.CARD,
            command=self._apply_watch_settings,
        )
        self.switch_seen.pack(anchor="w", pady=(0, 16))
        for switch, key in ((self.switch_queued, "include_queued"), (self.switch_seen, "require_activity_first")):
            switch.select() if self.settings[key] else switch.deselect()

        self.arm_button = ui.primary_button(auto.body, "Arm watcher", self._toggle_arm, ui.ACCENT, width=180)
        self.arm_button.pack(anchor="w")

        self.countdown_card = ui.Card(page, border_color=ui.AMBER)
        self.countdown_label = tk.Label(
            self.countdown_card.body, text="", font=ui.tk_font(16, "bold"), fg=ui.AMBER, bg=ui.CARD, anchor="w"
        )
        self.countdown_label.pack(side="left")
        ui.primary_button(self.countdown_card.body, "Cancel", self._cancel_countdown, ui.RED, width=120).pack(side="right")

        self.jobs_card = ui.Card(page, "Download queue", "Live from appmanifest files in every Steam library")
        self.jobs_card.pack(fill="both", expand=True, pady=(14, 0))
        jobs_wrap = tk.Frame(self.jobs_card.body, bg=ui.CARD, height=220)
        jobs_wrap.pack(fill="both", expand=True)
        jobs_wrap.pack_propagate(False)
        self.jobs_tree = ui.make_tree(
            jobs_wrap,
            ("name", "status", "progress", "left"),
            ("Game", "Status", "Progress", "Remaining"),
            (280, 110, 160, 140),
            "name",
        )

    def _labeled_menu(self, parent, column: int, caption: str, values: list[str], variable) -> None:
        holder = tk.Frame(parent, bg=ui.CARD)
        holder.grid(row=0, column=column, sticky="ew", padx=(0 if column == 0 else 10, 0))
        tk.Label(holder, text=caption.upper(), bg=ui.CARD, fg=ui.FAINT, font=ui.tk_font(8, "bold"), anchor="w").pack(fill="x")
        ctk.CTkOptionMenu(
            holder,
            values=values,
            variable=variable,
            font=ui.font(12),
            dropdown_font=ui.font(12),
            fg_color=ui.ELEVATED,
            button_color=ui.ELEVATED,
            button_hover_color=ui.CARD_HOVER,
            dropdown_fg_color=ui.ELEVATED,
            text_color=ui.TEXT,
            height=36,
            corner_radius=9,
            bg_color=ui.CARD,
            command=lambda _v: self._apply_watch_settings(),
        ).pack(fill="x", pady=(6, 0))

    # ---------------------------------------------------------------- stopper

    def _build_stopper(self) -> None:
        page = self._page("stopper")

        hero = ui.Card(page)
        hero.pack(fill="x")
        self.block_headline = tk.Label(
            hero.body, text="Checking firewall…", font=ui.tk_font(22, "bold"), fg=ui.ACCENT, bg=ui.CARD, anchor="w"
        )
        self.block_headline.pack(anchor="w")
        self.block_detail = tk.Label(
            hero.body,
            text="Blocks inbound and outbound traffic for the Steam client on this PC.",
            font=ui.tk_font(10),
            fg=ui.MUTED,
            bg=ui.CARD,
            anchor="w",
        )
        self.block_detail.pack(anchor="w", pady=(6, 0))
        self.rule_count = tk.Label(hero.body, text="", font=ui.tk_font(9), fg=ui.FAINT, bg=ui.CARD, anchor="w")
        self.rule_count.pack(anchor="w", pady=(4, 18))

        buttons = tk.Frame(hero.body, bg=ui.CARD)
        buttons.pack(fill="x")
        ui.primary_button(buttons, "Block Steam internet", self._start_block, ui.RED, width=210).pack(
            side="left", padx=(0, 10)
        )
        ui.primary_button(buttons, "Restore internet", self._start_unblock, ui.GREEN, width=190).pack(side="left")

        options = ui.Card(page, "Options")
        options.pack(fill="x", pady=(14, 0))
        self.switch_game_exes = ctk.CTkSwitch(
            options.body,
            text="Also block installed game executables (stricter, many more rules)",
            font=ui.font(12),
            text_color=ui.TEXT,
            progress_color=ui.ACCENT,
            bg_color=ui.CARD,
            command=self._persist_settings,
        )
        self.switch_game_exes.pack(anchor="w", pady=(0, 8))
        self.switch_kill_after = ctk.CTkSwitch(
            options.body,
            text="Kill Steam after blocking so open connections drop immediately",
            font=ui.font(12),
            text_color=ui.TEXT,
            progress_color=ui.ACCENT,
            bg_color=ui.CARD,
            command=self._persist_settings,
        )
        self.switch_kill_after.pack(anchor="w")
        for switch, key in ((self.switch_game_exes, "block_game_exes"), (self.switch_kill_after, "kill_after_block")):
            switch.select() if self.settings[key] else switch.deselect()

        offline = ui.Card(page, "Offline mode", "Writes WantsOfflineMode in loginusers.vdf; restart Steam to apply")
        offline.pack(fill="x", pady=(14, 0))
        offline_row = tk.Frame(offline.body, bg=ui.CARD)
        offline_row.pack(fill="x")
        ui.ghost_button(offline_row, "Prefer offline", lambda: self._offline_pref(True), width=150, height=38).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(offline_row, "Prefer online", lambda: self._offline_pref(False), width=150, height=38).pack(
            side="left"
        )

        note = ui.Card(page, "How it works")
        note.pack(fill="x", pady=(14, 0))
        tk.Label(
            note.body,
            text=(
                "Steam Stopper adds Windows Firewall rules named SteamStopper-* that block steam.exe, "
                "steamwebhelper.exe, and the other client binaries in both directions. Nothing leaves this "
                "machine and no Steam files are patched. Administrator rights are required, and Restore "
                "internet deletes every rule the app created."
            ),
            font=ui.tk_font(10),
            fg=ui.MUTED,
            bg=ui.CARD,
            wraplength=820,
            justify="left",
            anchor="w",
        ).pack(anchor="w")

    # --------------------------------------------------------------- processes

    def _build_process(self) -> None:
        page = self._page("process")

        actions = ui.Card(page, "Client control")
        actions.pack(fill="x")
        row = tk.Frame(actions.body, bg=ui.CARD)
        row.pack(fill="x")
        ui.primary_button(row, "Kill Steam", self._kill, ui.RED, width=140).pack(side="left", padx=(0, 8))
        ui.ghost_button(row, "Launch Steam", lambda: self._launch(False), width=140, height=40).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(row, "Launch offline", lambda: self._launch(True), width=140, height=40).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(row, "Refresh", self.refresh_processes, width=110, height=40).pack(side="left")

        procs = ui.Card(page, "Running Steam processes")
        procs.pack(fill="both", expand=True, pady=(14, 0))
        proc_wrap = tk.Frame(procs.body, bg=ui.CARD, height=280)
        proc_wrap.pack(fill="both", expand=True)
        proc_wrap.pack_propagate(False)
        self.proc_tree = ui.make_tree(
            proc_wrap,
            ("name", "pid", "memory"),
            ("Process", "PID", "Memory"),
            (280, 90, 140),
            "name",
        )

    # ----------------------------------------------------------------- library

    def _build_library(self) -> None:
        page = self._page("library")

        top = ui.Card(page)
        top.pack(fill="x")
        row = tk.Frame(top.body, bg=ui.CARD)
        row.pack(fill="x")
        self.lib_summary = tk.Label(row, text="Scanning…", font=ui.tk_font(15, "bold"), fg=ui.TEXT, bg=ui.CARD, anchor="w")
        self.lib_summary.pack(side="left")
        ui.ghost_button(row, "Rescan", lambda: self._run_bg(self._scan_library), width=110, height=36).pack(side="right")

        self.search_var = ctk.StringVar()
        self.search_var.trace_add("write", lambda *_: self._render_games())
        ctk.CTkEntry(
            top.body,
            textvariable=self.search_var,
            placeholder_text="Filter by title or app id",
            font=ui.font(12),
            fg_color=ui.ELEVATED,
            border_color=ui.LINE,
            bg_color=ui.CARD,
            height=38,
            corner_radius=9,
        ).pack(fill="x", pady=(14, 0))

        holder = ui.Card(page, "Installed games")
        holder.pack(fill="both", expand=True, pady=(14, 0))
        games_wrap = tk.Frame(holder.body, bg=ui.CARD, height=360)
        games_wrap.pack(fill="both", expand=True)
        games_wrap.pack_propagate(False)
        self.games_tree = ui.make_tree(
            games_wrap,
            ("name", "appid", "size"),
            ("Game", "App ID", "On disk"),
            (420, 90, 120),
            "name",
        )
        self.games_tree.bind("<Double-1>", lambda _e: self._open_selected_game())

    # ------------------------------------------------------------- maintenance

    def _build_tools(self) -> None:
        page = self._page("tools")

        caches = ui.Card(page, "Cache and log cleanup", "Measure first, then clear what you do not need")
        caches.pack(fill="x")
        grid = tk.Frame(caches.body, bg=ui.CARD)
        grid.pack(fill="x")
        grid.columnconfigure((0, 1), weight=1, uniform="cache")

        self.cache_labels: dict[str, tk.Label] = {}
        for index, name in enumerate(CACHE_TARGETS):
            tile = tk.Frame(grid, bg=ui.ELEVATED, highlightthickness=1, highlightbackground=ui.LINE)
            tile.grid(row=index // 2, column=index % 2, sticky="nsew", padx=(0 if index % 2 == 0 else 8, 0), pady=4)
            inner = tk.Frame(tile, bg=ui.ELEVATED)
            inner.pack(fill="x", padx=12, pady=10)
            labels = tk.Frame(inner, bg=ui.ELEVATED)
            labels.pack(side="left")
            tk.Label(labels, text=name, font=ui.tk_font(11, "bold"), fg=ui.TEXT, bg=ui.ELEVATED, anchor="w").pack(anchor="w")
            size_label = tk.Label(labels, text="not measured", font=ui.tk_font(9), fg=ui.MUTED, bg=ui.ELEVATED, anchor="w")
            size_label.pack(anchor="w")
            self.cache_labels[name] = size_label
            ui.ghost_button(inner, "Clear", lambda n=name: self._clear_cache(n), width=72, height=28).pack(side="right")

        buttons = tk.Frame(caches.body, bg=ui.CARD)
        buttons.pack(fill="x", pady=(14, 0))
        ui.ghost_button(buttons, "Measure sizes", lambda: self._run_bg(self._measure_caches), width=150, height=38).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(buttons, "Clear all", self._clear_all_caches, width=120, height=38).pack(side="left")

        backup = ui.Card(page, "Backup", "Zips config and userdata into Documents\\SteamStopperBackups")
        backup.pack(fill="x", pady=(14, 0))
        brow = tk.Frame(backup.body, bg=ui.CARD)
        brow.pack(fill="x")
        ui.primary_button(brow, "Backup now", lambda: self._run_bg(self._backup), ui.ACCENT, width=160).pack(
            side="left", padx=(0, 8)
        )
        ui.ghost_button(brow, "Open backups folder", lambda: self._open_folder(None), width=180, height=40).pack(
            side="left"
        )

        power = ui.Card(page, "Power", "Same actions the download watcher can trigger")
        power.pack(fill="x", pady=(14, 0))
        prow = tk.Frame(power.body, bg=ui.CARD)
        prow.pack(fill="x")
        for label, action in (("Sleep", "sleep"), ("Hibernate", "hibernate"), ("Lock", "lock"), ("Shut down", "shutdown")):
            ui.ghost_button(prow, label, lambda a=action: self._manual_power(a), width=120, height=38).pack(
                side="left", padx=(0, 8)
            )
        ui.ghost_button(prow, "Cancel pending shutdown", self._abort_shutdown, width=200, height=38).pack(side="left")

    # ------------------------------------------------------------- watch loop

    def _start_watch_thread(self) -> None:
        threading.Thread(target=self._watch_loop, daemon=True).start()

    def _watch_loop(self) -> None:
        while self._watch_thread_running:
            if self.watcher is not None:
                try:
                    snapshot = self.watcher.poll()
                except Exception:
                    snapshot = None
                if snapshot is not None:
                    self._post(self._apply_snapshot, snapshot)
            time.sleep(POLL_SECONDS)

    def _apply_snapshot(self, snapshot: core.DownloadSnapshot, force: bool = False) -> None:
        self.last_snapshot = snapshot
        active_jobs = [job for job in snapshot.jobs if job.is_active]
        self.tile_downloads.set(
            str(len(active_jobs)) if active_jobs else ("queued" if snapshot.jobs else "none"),
            ui.ACCENT if active_jobs else ui.MUTED,
        )
        self.download_chip.set(
            f"downloads: {len(active_jobs)} active" if active_jobs else "downloads: idle",
            ui.ACCENT if active_jobs else ui.MUTED,
        )
        self._update_auto_summary(snapshot)
        if self.current_page == "downloads" or force:
            self._paint_downloads(snapshot, active_jobs)
        if self.armed and snapshot.finished and self.countdown_left is None:
            self._begin_countdown()

    def _paint_downloads(self, snapshot: core.DownloadSnapshot, active_jobs: list[core.DownloadJob] | None = None) -> None:
        active_jobs = active_jobs if active_jobs is not None else [job for job in snapshot.jobs if job.is_active]
        if snapshot.jobs:
            if active_jobs:
                self.dl_headline.configure(text=f"Downloading {len(active_jobs)} item(s)", fg=ui.TEXT)
                self.dl_state_chip.set("active", ui.ACCENT)
            else:
                self.dl_headline.configure(text=f"{len(snapshot.jobs)} item(s) waiting", fg=ui.TEXT)
                self.dl_state_chip.set("queued", ui.AMBER)
        else:
            self.dl_headline.configure(text="No downloads pending", fg=ui.TEXT)
            self.dl_state_chip.set("idle", ui.GREEN)
        self.dl_bar.set(snapshot.percent)
        self.tile_speed.set(core.format_speed(snapshot.speed_bps))
        self.tile_remaining.set(core.format_bytes(snapshot.remaining) if snapshot.remaining else "0 B")
        self.tile_eta.set(core.format_duration(snapshot.eta_seconds))
        self.tile_jobs.set(str(len(snapshot.jobs)))
        self.sparkline.push(snapshot.speed_bps / (1024 * 1024))
        self._render_jobs(snapshot.jobs)

    def _render_jobs(self, jobs: list[core.DownloadJob]) -> None:
        seen = {job.appid for job in jobs}
        for job in jobs:
            progress = f"{job.percent * 100:.1f}%" if job.total else job.status
            remaining = core.format_bytes(job.pending_bytes) if job.total else "—"
            values = (job.name, job.status, progress, remaining)
            if job.appid in self._known_jobs:
                self.jobs_tree.item(job.appid, values=values)
            else:
                self.jobs_tree.insert("", "end", iid=job.appid, values=values)
                self._known_jobs.add(job.appid)
        for appid in list(self._known_jobs - seen):
            self.jobs_tree.delete(appid)
            self._known_jobs.discard(appid)

    def _update_auto_summary(self, snapshot: core.DownloadSnapshot) -> None:
        action_label = self.action_var.get()
        if not self.armed:
            self.dash_auto.configure(text=f"Watcher disarmed. Selected action: {action_label}.", fg=ui.MUTED)
            return
        if self.countdown_left is not None:
            self.dash_auto.configure(text=f"{action_label} in {self.countdown_left}s.", fg=ui.AMBER)
            return
        if snapshot.jobs:
            eta = core.format_duration(snapshot.eta_seconds)
            self.dash_auto.configure(
                text=f"Armed. {action_label} once the queue empties. Estimated time left: {eta}.",
                fg=ui.ACCENT,
            )
        elif self.watcher is not None and self.watcher.require_activity_first and not snapshot.seen_activity:
            self.dash_auto.configure(
                text=f"Armed, waiting for a download to start before {action_label.lower()}.", fg=ui.MUTED
            )
        else:
            remaining = max(0, int(self.watcher.grace_seconds - snapshot.idle_seconds)) if self.watcher else 0
            self.dash_auto.configure(
                text=f"Queue is empty. Confirming for {remaining}s more before {action_label.lower()}.",
                fg=ui.AMBER,
            )

    # ----------------------------------------------------------- auto shutdown

    def _toggle_arm(self) -> None:
        if self.armed:
            self.armed = False
            self.countdown_left = None
            self.countdown_card.pack_forget()
            self.arm_button.configure(text="Arm watcher", fg_color=ui.ACCENT, text_color=ui.BG)
            self.auto_chip.set("auto: off", ui.MUTED)
            self.status_var.set("Watcher disarmed.")
            return

        if self.watcher is None:
            messagebox.showerror("Steam Stopper", "Steam install not found, so downloads cannot be watched.")
            return

        self._apply_watch_settings()
        self.watcher.reset()
        self.armed = True
        self.arm_button.configure(text="Disarm watcher", fg_color=ui.RED, text_color=ui.TEXT)
        self.auto_chip.set(f"auto: {self._action_key()}", ui.ACCENT)
        self.status_var.set(f"Armed. Will {self.action_var.get().lower()} when downloads finish.")

    def _begin_countdown(self) -> None:
        self.countdown_left = _seconds_from_choice(self.countdown_var.get())
        self.countdown_card.pack(fill="x", pady=(14, 0), before=self.jobs_card)
        try:
            winsound.MessageBeep(winsound.MB_ICONASTERISK)
        except RuntimeError:
            pass
        self.deiconify()
        self.lift()
        self.attributes("-topmost", True)
        self.after(1200, lambda: self.attributes("-topmost", False))
        self._tick_countdown()

    def _tick_countdown(self) -> None:
        if self.countdown_left is None:
            return
        if self.countdown_left <= 0:
            self.countdown_label.configure(text=f"Running: {self.action_var.get()}")
            self.countdown_left = None
            self.armed = False
            self.arm_button.configure(text="Arm watcher", fg_color=ui.ACCENT, text_color=ui.BG)
            self.auto_chip.set("auto: firing", ui.RED)
            key = self._action_key()
            self._run_bg(lambda: self._fire_action(key))
            return
        self.countdown_label.configure(text=f"Downloads finished · {self.action_var.get()} in {self.countdown_left}s")
        self.countdown_left -= 1
        self.after(1000, self._tick_countdown)

    def _cancel_countdown(self) -> None:
        self.countdown_left = None
        self.countdown_card.pack_forget()
        core.cancel_pending_shutdown()
        self.status_var.set("Auto action cancelled.")
        self.auto_chip.set("auto: off", ui.MUTED)
        self.armed = False
        self.arm_button.configure(text="Arm watcher", fg_color=ui.ACCENT, text_color=ui.BG)

    def _action_key(self) -> str:
        label = self.action_var.get()
        for key, value in core.POWER_ACTIONS.items():
            if value == label:
                return key
        return "notify"

    def _fire_action(self, key: str) -> None:
        """Runs on a worker thread, so it must not touch Tk variables."""
        try:
            message = core.run_power_action(key, self.steam_root)
        except Exception as exc:
            message = f"Action failed: {exc}"
        self._post(self.status_var.set, message)
        self._post(self.refresh_status_chips)
        if key in ("notify", "exit_steam", "block_internet"):
            self._post(lambda: messagebox.showinfo("Steam Stopper", message))

    def _manual_power(self, action: str) -> None:
        label = core.POWER_ACTIONS.get(action, action)
        if not messagebox.askyesno("Steam Stopper", f"{label} now?"):
            return
        self._run_bg(lambda: core.run_power_action(action, self.steam_root))

    def _abort_shutdown(self) -> None:
        ok = core.cancel_pending_shutdown()
        self.status_var.set("Pending shutdown cancelled." if ok else "No pending shutdown to cancel.")

    def _apply_watch_settings(self) -> None:
        if self.watcher is not None:
            self.watcher.include_queued = bool(self.switch_queued.get())
            self.watcher.require_activity_first = bool(self.switch_seen.get())
            self.watcher.grace_seconds = float(_seconds_from_choice(self.grace_var.get()))
        if self.armed:
            self.auto_chip.set(f"auto: {self._action_key()}", ui.ACCENT)
        self._persist_settings()

    def _persist_settings(self) -> None:
        self.settings.update(
            {
                "auto_action": self._action_key(),
                "countdown_seconds": _seconds_from_choice(self.countdown_var.get()),
                "grace_seconds": _seconds_from_choice(self.grace_var.get()),
                "include_queued": bool(self.switch_queued.get()),
                "require_activity_first": bool(self.switch_seen.get()),
                "block_game_exes": bool(self.switch_game_exes.get()),
                "kill_after_block": bool(self.switch_kill_after.get()),
            }
        )
        core.save_settings(self.settings)

    # ------------------------------------------------------------------ status

    def refresh_status_chips(self) -> None:
        admin = core.is_admin()
        self.admin_chip.set("administrator" if admin else "standard user", ui.GREEN if admin else ui.AMBER)
        if admin:
            self.elevate_button.configure(state="disabled")
        self._run_bg(self._load_firewall_state)

    def _load_firewall_state(self) -> None:
        try:
            names = core.firewall_rule_names()
        except Exception:
            names = []
        self._post(self._apply_firewall_state, names)

    def _apply_firewall_state(self, names: list[str]) -> None:
        blocked = bool(names)
        self.firewall_chip.set("steam blocked" if blocked else "steam allowed", ui.RED if blocked else ui.GREEN)
        self.tile_firewall.set("blocked" if blocked else "allowed", ui.RED if blocked else ui.GREEN)
        self.block_headline.configure(
            text="STEAM IS BLOCKED" if blocked else "STEAM CAN CONNECT", fg=ui.RED if blocked else ui.GREEN
        )
        self.rule_count.configure(text=f"{len(names)} firewall rule(s) named {core.FIREWALL_PREFIX}-*")

    def refresh_processes(self) -> None:
        self._run_bg(lambda: self._post(self._render_processes, core.running_steam_processes()))

    def _render_processes(self, rows: list[dict]) -> None:
        self.proc_tree.delete(*self.proc_tree.get_children())
        self.tile_client.set(f"{len(rows)} proc" if rows else "stopped", ui.ACCENT if rows else ui.MUTED)
        for row in rows:
            self.proc_tree.insert("", "end", values=(row["name"], row["pid"], row["memory"]))

    # ----------------------------------------------------------------- actions

    def _need_steam(self) -> Path | None:
        if not self.steam_root:
            messagebox.showerror("Steam Stopper", "Could not find a Steam install.")
            return None
        return self.steam_root

    def _need_admin(self) -> bool:
        if core.is_admin():
            return True
        if messagebox.askyesno("Administrator required", "Firewall changes need elevation. Relaunch as admin now?"):
            core.relaunch_as_admin()
            self._on_close()
        return False

    def _elevate(self) -> None:
        core.relaunch_as_admin()
        self._on_close()

    def _run_bg(self, fn) -> None:
        threading.Thread(target=fn, daemon=True).start()

    def _start_block(self) -> None:
        """Read the switches on the UI thread, then block in the background."""
        if not self._need_admin():
            return
        if not self._need_steam():
            return
        include_games = bool(self.switch_game_exes.get())
        kill_after = bool(self.switch_kill_after.get())
        self.status_var.set("Adding firewall rules…")
        self._run_bg(lambda: self._do_block(include_games, kill_after))

    def _do_block(self, include_games: bool, kill_after: bool) -> None:
        root = self.steam_root
        if not root:
            return
        count = core.block_steam(root, include_games=include_games)
        if kill_after:
            core.kill_steam()
        self._post(self.refresh_status_chips)
        self._post(self.refresh_processes)
        self._post(self.status_var.set, f"Blocked {count} Steam binary path(s).")

    def _start_unblock(self) -> None:
        if not self._need_admin():
            return
        self.status_var.set("Removing firewall rules…")
        self._run_bg(self._do_unblock)

    def _do_unblock(self) -> None:
        removed = core.unblock_steam()
        self._post(self.refresh_status_chips)
        self._post(self.status_var.set, f"Removed {removed} firewall rule(s).")

    def _kill(self) -> None:
        count = core.kill_steam()
        self.refresh_processes()
        self.status_var.set(f"Kill signals sent for {count} process name(s).")

    def _launch(self, offline: bool) -> None:
        root = self._need_steam()
        if not root:
            return
        core.launch_steam(root, offline=offline)
        self.status_var.set("Launching Steam" + (" in offline mode." if offline else "."))
        self.after(2500, self.refresh_processes)

    def _offline_pref(self, enabled: bool) -> None:
        root = self._need_steam()
        if not root:
            return
        if core.set_wants_offline_mode(root, enabled):
            self.status_var.set("Updated WantsOfflineMode. Restart Steam to apply.")
        else:
            messagebox.showwarning("Steam Stopper", "Could not update loginusers.vdf. Has Steam ever signed in here?")

    def _scan_library(self) -> None:
        root = self.steam_root
        if not root:
            return
        games = core.list_installed_games(root)
        self._post(self._set_games, games)

    def _set_games(self, games: list[core.GameEntry]) -> None:
        self.games = games
        self.game_index = {game.appid: game for game in games}
        total = sum(game.size_bytes for game in games)
        self.lib_summary.configure(text=f"{len(games)} games  ·  {core.format_bytes(total)}")
        self.tile_library.set(core.format_bytes(total), ui.ACCENT)
        self._render_games()

    def _render_games(self) -> None:
        query = self.search_var.get().strip().lower()
        matches = [g for g in self.games if query in g.name.lower() or query in g.appid] if query else self.games
        self.games_tree.delete(*self.games_tree.get_children())
        for game in sorted(matches, key=lambda g: g.size_bytes, reverse=True):
            self.games_tree.insert(
                "",
                "end",
                iid=game.appid,
                values=(game.name, game.appid, core.format_bytes(game.size_bytes)),
            )

    def _open_selected_game(self) -> None:
        selection = self.games_tree.selection()
        if not selection:
            return
        game = self.game_index.get(selection[0])
        if game:
            core.open_folder(Path(game.library) / "steamapps" / "common" / game.install_dir)

    def _cache_path(self, name: str) -> Path | None:
        root = self.steam_root
        if not root:
            return None
        return root.joinpath(*CACHE_TARGETS[name])

    def _measure_caches(self) -> None:
        if not self.steam_root:
            return
        sizes = {name: core.folder_size(self._cache_path(name)) for name in CACHE_TARGETS}

        def apply() -> None:
            for name, size in sizes.items():
                self.cache_labels[name].configure(text=core.format_bytes(size))
            self.status_var.set(f"Measured {core.format_bytes(sum(sizes.values()))} of cache and logs.")

        self._post(apply)

    def _clear_cache(self, name: str) -> None:
        path = self._cache_path(name)
        if path is None:
            return
        if not messagebox.askyesno("Clear files", f"Delete the contents of:\n{path}?"):
            return
        removed = core.clear_path(path)
        self.status_var.set(f"Cleared {removed} item(s) from {name}.")
        self._run_bg(self._measure_caches)

    def _clear_all_caches(self) -> None:
        if not self.steam_root:
            return
        if not messagebox.askyesno("Clear all", "Delete the contents of every cache and log folder listed?"):
            return
        removed = sum(core.clear_path(self._cache_path(name)) for name in CACHE_TARGETS)
        self.status_var.set(f"Cleared {removed} item(s).")
        self._run_bg(self._measure_caches)

    def _backup(self) -> None:
        root = self.steam_root
        if not root:
            return
        self._post(self.status_var.set, "Backing up config and userdata…")
        try:
            archive = core.backup_config(root)
        except Exception as exc:
            self._post(lambda: messagebox.showerror("Backup failed", str(exc)))
            return
        self._post(self.status_var.set, f"Backup saved to {archive}")
        self._post(lambda: messagebox.showinfo("Backup complete", str(archive)))

    def _open_folder(self, parts: tuple[str, ...] | None) -> None:
        if parts is None:
            core.open_folder(Path.home() / "Documents" / "SteamStopperBackups")
            return
        root = self._need_steam()
        if not root:
            return
        core.open_folder(root.joinpath(*parts))

    def _on_close(self) -> None:
        self._watch_thread_running = False
        self._persist_settings()
        self.destroy()


def main() -> None:
    if sys.platform != "win32":
        print("Steam Stopper only runs on Windows.")
        sys.exit(1)
    SteamStopperApp().mainloop()


if __name__ == "__main__":
    main()
