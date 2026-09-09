"""Palette and reusable widgets for the Steam Stopper UI."""

from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import customtkinter as ctk

BG = "#0c1016"
SIDEBAR = "#10161e"
CARD = "#161e28"
CARD_HOVER = "#1c2633"
ELEVATED = "#1b2430"
LINE = "#2a3544"
ACCENT = "#5cb3ff"
ACCENT_DIM = "#1e4f7a"
GREEN = "#3dd68c"
RED = "#ef5b54"
AMBER = "#f0c14b"
TEXT = "#edf2f7"
MUTED = "#93a1b0"
FAINT = "#667484"

FONT = "Segoe UI"
_FONTS: dict[tuple[int, str], ctk.CTkFont] = {}


def font(size: int = 13, weight: str = "normal") -> ctk.CTkFont:
    key = (size, weight)
    cached = _FONTS.get(key)
    if cached is None:
        cached = ctk.CTkFont(family=FONT, size=size, weight=weight)
        _FONTS[key] = cached
    return cached


def tk_font(size: int = 11, weight: str = "normal") -> tuple:
    return (FONT, size, weight) if weight != "normal" else (FONT, size)


def apply_tree_style(root: tk.Misc) -> None:
    style = ttk.Style(root)
    try:
        style.theme_use("clam")
    except tk.TclError:
        pass
    style.configure(
        "Steam.Treeview",
        background=ELEVATED,
        foreground=TEXT,
        fieldbackground=ELEVATED,
        bordercolor=LINE,
        lightcolor=ELEVATED,
        darkcolor=ELEVATED,
        rowheight=34,
        font=tk_font(11),
    )
    style.configure(
        "Steam.Treeview.Heading",
        background=CARD,
        foreground=MUTED,
        bordercolor=LINE,
        relief="flat",
        font=tk_font(10, "bold"),
        padding=(8, 8),
    )
    style.map(
        "Steam.Treeview",
        background=[("selected", ACCENT_DIM)],
        foreground=[("selected", TEXT)],
    )
    style.configure(
        "Steam.Vertical.TScrollbar",
        background=CARD,
        troughcolor=BG,
        bordercolor=BG,
        arrowcolor=MUTED,
    )


class NativeScroll(tk.Frame):
    """Lightweight canvas scroller. CustomTkinter's scroll frame is too slow to show/hide."""

    def __init__(self, master, page_key: str = "", app=None, **kwargs):
        super().__init__(master, bg=BG, highlightthickness=0)
        self.page_key = page_key
        self.app = app
        self.canvas = tk.Canvas(self, bg=BG, highlightthickness=0, bd=0)
        self.bar = ttk.Scrollbar(self, orient="vertical", style="Steam.Vertical.TScrollbar", command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=self.bar.set)
        self.bar.pack(side="right", fill="y")
        self.canvas.pack(side="left", fill="both", expand=True)
        self.body = tk.Frame(self.canvas, bg=BG)
        self._win = self.canvas.create_window((0, 0), window=self.body, anchor="nw")
        self.body.bind("<Configure>", self._on_inner)
        self.canvas.bind("<Configure>", self._on_canvas)

    def _on_inner(self, _event=None) -> None:
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _on_canvas(self, event) -> None:
        self.canvas.itemconfigure(self._win, width=event.width)

    def _on_wheel(self, event) -> None:
        if self.app is not None and getattr(self.app, "current_page", None) != self.page_key:
            return
        self.canvas.yview_scroll(int(-event.delta / 120), "units")
        return "break"


class Card(tk.Frame):
    def __init__(self, parent, title: str | None = None, subtitle: str | None = None, **kwargs):
        super().__init__(
            parent,
            bg=kwargs.pop("fg_color", CARD),
            highlightthickness=1,
            highlightbackground=kwargs.pop("border_color", LINE),
            **kwargs,
        )
        pad = tk.Frame(self, bg=CARD)
        pad.pack(fill="both", expand=True, padx=16, pady=14)
        if title:
            tk.Label(pad, text=title, bg=CARD, fg=TEXT, font=tk_font(13, "bold"), anchor="w").pack(fill="x")
            if subtitle:
                tk.Label(pad, text=subtitle, bg=CARD, fg=MUTED, font=tk_font(9), anchor="w").pack(fill="x", pady=(2, 8))
            else:
                tk.Frame(pad, bg=CARD, height=8).pack(fill="x")
        self.body = pad


class Chip(tk.Label):
    def __init__(self, parent, text: str = "", color: str = MUTED, **kwargs):
        super().__init__(
            parent,
            text=text,
            bg=kwargs.pop("fg_color", ELEVATED),
            fg=color,
            font=tk_font(9, "bold"),
            padx=10,
            pady=4,
            **kwargs,
        )

    def set(self, text: str, color: str = MUTED) -> None:
        self.configure(text=text, fg=color)


class StatTile(tk.Frame):
    def __init__(self, parent, caption: str, value: str = "--", color: str = TEXT, **kwargs):
        super().__init__(parent, bg=ELEVATED, highlightthickness=1, highlightbackground=LINE)
        tk.Label(self, text=caption.upper(), bg=ELEVATED, fg=FAINT, font=tk_font(8, "bold"), anchor="w").pack(
            fill="x", padx=12, pady=(10, 0)
        )
        self._value = tk.Label(self, text=value, bg=ELEVATED, fg=color, font=tk_font(16, "bold"), anchor="w")
        self._value.pack(fill="x", padx=12, pady=(2, 10))

    def set(self, value: str, color: str | None = None) -> None:
        self._value.configure(text=value)
        if color:
            self._value.configure(fg=color)


class NavButton(tk.Frame):
    def __init__(self, parent, text: str, command, **kwargs):
        super().__init__(parent, bg=SIDEBAR, cursor="hand2", height=40)
        self.pack_propagate(False)
        self._command = command
        self._active = False
        self._bar = tk.Frame(self, width=3, bg=SIDEBAR)
        self._bar.pack(side="left", fill="y", padx=(0, 10), pady=8)
        self._label = tk.Label(self, text=text, bg=SIDEBAR, fg=MUTED, font=tk_font(11), anchor="w")
        self._label.pack(side="left", fill="x", expand=True)
        for widget in (self, self._label, self._bar):
            widget.bind("<Button-1>", lambda _e: self._command())
            widget.bind("<Enter>", self._on_enter)
            widget.bind("<Leave>", self._on_leave)

    def _on_enter(self, _event=None) -> None:
        if not self._active:
            self.configure(bg=CARD_HOVER)
            self._label.configure(bg=CARD_HOVER)

    def _on_leave(self, _event=None) -> None:
        if not self._active:
            self.configure(bg=SIDEBAR)
            self._label.configure(bg=SIDEBAR)

    def set_active(self, active: bool) -> None:
        self._active = active
        bg = ELEVATED if active else SIDEBAR
        self.configure(bg=bg)
        self._label.configure(bg=bg, fg=TEXT if active else MUTED, font=tk_font(11, "bold" if active else "normal"))
        self._bar.configure(bg=ACCENT if active else bg)


class Sparkline(tk.Canvas):
    def __init__(self, parent, height: int = 52, capacity: int = 80, color: str = ACCENT, **kwargs):
        super().__init__(parent, height=height, bg=ELEVATED, highlightthickness=1, highlightbackground=LINE, bd=0, **kwargs)
        self._values: list[float] = []
        self._capacity = capacity
        self._color = color
        self._pending = False
        self.bind("<Configure>", lambda _e: self._schedule())

    def push(self, value: float) -> None:
        self._values.append(max(0.0, value))
        if len(self._values) > self._capacity:
            del self._values[0 : len(self._values) - self._capacity]
        self._schedule()

    def clear(self) -> None:
        self._values.clear()
        self._schedule()

    def _schedule(self) -> None:
        if self._pending:
            return
        self._pending = True
        self.after_idle(self._redraw)

    def _redraw(self) -> None:
        self._pending = False
        self.delete("all")
        width = self.winfo_width()
        height = self.winfo_height()
        if width < 8 or height < 8:
            return
        for i in range(1, 4):
            self.create_line(0, height * i / 4, width, height * i / 4, fill=LINE)
        peak = max(self._values) if self._values else 0.0
        if len(self._values) < 2 or peak <= 0:
            self.create_text(width / 2, height / 2, text="speed graph waits for live throughput", fill=FAINT, font=tk_font(9))
            return
        step = width / (len(self._values) - 1)
        points = []
        for index, value in enumerate(self._values):
            points.extend((index * step, height - 8 - (value / peak) * (height - 18)))
        self.create_line(*points, fill=self._color, width=2)
        self.create_text(width - 8, 10, text=f"{self._values[-1]:.1f} MB/s", fill=MUTED, font=tk_font(8), anchor="e")


def make_tree(parent, columns: tuple[str, ...], headings: tuple[str, ...], widths: tuple[int, ...], stretch: str) -> ttk.Treeview:
    tree = ttk.Treeview(
        parent,
        columns=columns,
        show="headings",
        style="Steam.Treeview",
        selectmode="browse",
    )
    for column, heading, width in zip(columns, headings, widths):
        tree.heading(column, text=heading, anchor="w")
        tree.column(column, width=width, minwidth=70, stretch=(column == stretch), anchor="w")
    scroll = ttk.Scrollbar(parent, orient="vertical", style="Steam.Vertical.TScrollbar", command=tree.yview)
    tree.configure(yscrollcommand=scroll.set)
    tree.pack(side="left", fill="both", expand=True)
    scroll.pack(side="right", fill="y")
    return tree


def _parent_bg(parent) -> str:
    for attr in ("bg", "fg_color"):
        try:
            value = parent.cget(attr)
        except (tk.TclError, ValueError):
            continue
        if not value or value == "transparent":
            continue
        if isinstance(value, (list, tuple)):
            value = value[1] if len(value) > 1 else value[0]
        return str(value)
    return CARD


def primary_button(parent, text: str, command, color: str = ACCENT, **kwargs) -> ctk.CTkButton:
    kwargs.setdefault("height", 36)
    kwargs.setdefault("corner_radius", 8)
    kwargs.setdefault("bg_color", _parent_bg(parent))
    return ctk.CTkButton(
        parent,
        text=text,
        command=command,
        font=font(13, "bold"),
        fg_color=color,
        hover_color=_darken(color),
        text_color=BG if color in (ACCENT, GREEN, AMBER) else TEXT,
        **kwargs,
    )


def ghost_button(parent, text: str, command, **kwargs) -> ctk.CTkButton:
    kwargs.setdefault("height", 34)
    kwargs.setdefault("corner_radius", 8)
    kwargs.setdefault("bg_color", _parent_bg(parent))
    return ctk.CTkButton(
        parent,
        text=text,
        command=command,
        font=font(12),
        fg_color=ELEVATED,
        hover_color=CARD_HOVER,
        text_color=TEXT,
        border_width=1,
        border_color=LINE,
        **kwargs,
    )


def _darken(hex_color: str, factor: float = 0.82) -> str:
    hex_color = hex_color.lstrip("#")
    rgb = tuple(int(hex_color[i : i + 2], 16) for i in (0, 2, 4))
    return "#" + "".join(f"{int(channel * factor):02x}" for channel in rgb)
