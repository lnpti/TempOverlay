"""TempOverlay - mostra a temperatura (de uma cidade ou de um TXT) sobreposta a tudo na tela.

Fonte do dado:
  * Cidade   -> Open-Meteo (gratuito, sem chave); cidade escolhida nas configuracoes.
  * Arquivo  -> um .txt simples contendo apenas um numero (ex.: 27 ou 27,5). Relido a cada poucos segundos.
"""
import ctypes
import json
import os
import queue
import re
import sys
import threading
import time
import tkinter as tk
import urllib.parse
import urllib.request
from tkinter import colorchooser, filedialog, messagebox, ttk

import pystray
from PIL import Image, ImageDraw

APP = "TempOverlay"
VERSION = "1.0.0"
KEY_COLOR = "#010101"  # cor tornada transparente (estilo "Transparente")
CFG_DIR = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), APP)
CFG_FILE = os.path.join(CFG_DIR, "config.json")
TICK_MS = 5000

DEFAULTS = {
    "source": "city",          # "city" | "txt"
    "city_name": "",
    "city_desc": "",
    "lat": None,
    "lon": None,
    "txt_path": "",
    "refresh_min": 10,
    "suffix": "°C",
    "decimals": 0,
    "show_label": True,
    "label": "",
    "font_size": 48,
    "font_family": "Segoe UI",
    "text_color": "#FFFFFF",
    "style": "transparent",    # "transparent" | "card"
    "card_color": "#101820",
    "opacity": 100,
    "locked": False,
    "x": 40,
    "y": 40,
    "autostart": False,
}


def load_cfg():
    cfg = dict(DEFAULTS)
    try:
        with open(CFG_FILE, "r", encoding="utf-8") as f:
            cfg.update(json.load(f))
    except Exception:
        pass
    return cfg


def save_cfg(cfg):
    try:
        os.makedirs(CFG_DIR, exist_ok=True)
        with open(CFG_FILE, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def http_json(url, timeout=10):
    req = urllib.request.Request(url, headers={"User-Agent": f"{APP}/{VERSION}"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def search_city(name):
    q = urllib.parse.urlencode({"name": name, "count": 8, "language": "pt", "format": "json"})
    data = http_json("https://geocoding-api.open-meteo.com/v1/search?" + q)
    out = []
    for r in data.get("results", []) or []:
        parts = [r.get("name", ""), r.get("admin1", ""), r.get("country", "")]
        out.append({
            "name": r.get("name", ""),
            "desc": ", ".join(p for p in parts if p),
            "lat": r["latitude"],
            "lon": r["longitude"],
        })
    return out


def fetch_city_temp(lat, lon):
    q = urllib.parse.urlencode({"latitude": lat, "longitude": lon, "current": "temperature_2m"})
    data = http_json("https://api.open-meteo.com/v1/forecast?" + q)
    return float(data["current"]["temperature_2m"])


_NUM = re.compile(r"-?\d+(?:[.,]\d+)?")


def read_txt_number(path):
    with open(path, "rb") as f:
        raw = f.read(4096)
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = raw.decode("latin-1")
    m = _NUM.search(text)
    if not m:
        raise ValueError("sem numero")
    return float(m.group(0).replace(",", "."))


# ---------- Windows helpers ----------
user32 = ctypes.windll.user32
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20
WS_EX_LAYERED = 0x80000
WS_EX_TOOLWINDOW = 0x80
WS_EX_NOACTIVATE = 0x08000000


def set_click_through(widget, enabled):
    try:
        hwnd = user32.GetAncestor(widget.winfo_id(), 2)  # GA_ROOT
        style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        if enabled:
            style |= WS_EX_TRANSPARENT
        else:
            style &= ~WS_EX_TRANSPARENT
        user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style)
    except Exception:
        pass


def set_autostart(enabled):
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                             r"Software\Microsoft\Windows\CurrentVersion\Run", 0, winreg.KEY_SET_VALUE)
        if enabled:
            if getattr(sys, "frozen", False):
                cmd = f'"{sys.executable}"'
            else:
                pyw = sys.executable.replace("python.exe", "pythonw.exe")
                cmd = f'"{pyw}" "{os.path.abspath(__file__)}"'
            winreg.SetValueEx(key, APP, 0, winreg.REG_SZ, cmd)
        else:
            try:
                winreg.DeleteValue(key, APP)
            except FileNotFoundError:
                pass
        winreg.CloseKey(key)
    except Exception:
        pass


def tray_image():
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((26, 6, 38, 42), radius=6, fill=(240, 240, 240, 255))
    d.ellipse((18, 34, 46, 62), fill=(230, 60, 50, 255))
    d.rounded_rectangle((29, 20, 35, 44), radius=3, fill=(230, 60, 50, 255))
    return img


class App:
    def __init__(self):
        self.cfg = load_cfg()
        self.first_run = not os.path.exists(CFG_FILE)
        self.q = queue.Queue()
        self.value = None
        self.last_fetch = 0.0
        self.fetching = False
        self.settings = None
        self.drag = (0, 0)

        self.root = tk.Tk()
        self.root.title(APP)
        self.root.overrideredirect(True)
        self.frame = tk.Frame(self.root, bd=0, highlightthickness=0)
        self.frame.pack()
        self.lbl_city = tk.Label(self.frame, bd=0)
        self.lbl_temp = tk.Label(self.frame, bd=0)
        for w in (self.frame, self.lbl_city, self.lbl_temp):
            w.bind("<ButtonPress-1>", self.on_press)
            w.bind("<B1-Motion>", self.on_motion)
            w.bind("<ButtonRelease-1>", self.on_release)

        self.tray = pystray.Icon(APP, tray_image(), APP, menu=pystray.Menu(
            pystray.MenuItem("Configurações", lambda: self.q.put(self.open_settings), default=True),
            pystray.MenuItem("Travar posição (cliques passam)", lambda: self.q.put(self.toggle_lock),
                             checked=lambda i: bool(self.cfg["locked"])),
            pystray.MenuItem("Atualizar agora", lambda: self.q.put(lambda: self.refresh(force=True))),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Sair", lambda: self.q.put(self.quit)),
        ))
        self.tray.run_detached()

        self.apply_style()
        self.root.geometry(f"+{int(self.cfg['x'])}+{int(self.cfg['y'])}")
        self.root.update()
        set_click_through(self.root, self.cfg["locked"])
        self.render()
        self.poll_queue()
        self.tick()
        if self.first_run or (self.cfg["source"] == "city" and not self.cfg["lat"]):
            self.root.after(400, self.open_settings)

    # ----- overlay -----
    def apply_style(self):
        c = self.cfg
        card = c["style"] == "card"
        bg = c["card_color"] if card else KEY_COLOR
        self.root.configure(bg=bg)
        self.frame.configure(bg=bg, padx=14 if card else 4, pady=8 if card else 2)
        size = int(c["font_size"])
        self.lbl_city.configure(bg=bg, fg=c["text_color"], font=(c["font_family"], max(8, int(size * 0.34))))
        self.lbl_temp.configure(bg=bg, fg=c["text_color"], font=(c["font_family"], size, "bold"))
        alpha = max(0.3, min(1.0, int(c["opacity"]) / 100))
        self.root.attributes("-topmost", True)
        self.root.attributes("-alpha", alpha)
        if card:
            self.root.attributes("-transparentcolor", "")
        else:
            self.root.attributes("-transparentcolor", KEY_COLOR)
        # contorno visivel enquanto a posicao esta destravada
        self.frame.configure(highlightthickness=0 if c["locked"] else 1,
                             highlightbackground="#FFB000", highlightcolor="#FFB000")
        self.root.update_idletasks()
        set_click_through(self.root, c["locked"])

    def label_text(self):
        if not self.cfg["show_label"]:
            return ""
        if self.cfg["label"].strip():
            return self.cfg["label"].strip()
        return self.cfg["city_name"] if self.cfg["source"] == "city" else ""

    def format_value(self):
        suffix = self.cfg["suffix"]
        if self.value is None:
            return "--" + suffix
        s = f"{self.value:.{int(self.cfg['decimals'])}f}"
        if s.lstrip("-").strip("0.") == "":
            s = s.lstrip("-")
        return s.replace(".", ",") + suffix

    def render(self):
        txt = self.label_text()
        self.lbl_city.pack_forget()
        self.lbl_temp.pack_forget()
        if txt:
            self.lbl_city.configure(text=txt)
            self.lbl_city.pack()
        self.lbl_temp.configure(text=self.format_value())
        self.lbl_temp.pack()

    def on_press(self, e):
        self.drag = (e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())

    def on_motion(self, e):
        if self.cfg["locked"]:
            return
        self.root.geometry(f"+{e.x_root - self.drag[0]}+{e.y_root - self.drag[1]}")

    def on_release(self, e):
        if self.cfg["locked"]:
            return
        self.cfg["x"], self.cfg["y"] = self.root.winfo_x(), self.root.winfo_y()
        save_cfg(self.cfg)

    def toggle_lock(self):
        self.cfg["locked"] = not self.cfg["locked"]
        save_cfg(self.cfg)
        self.apply_style()
        if self.settings and self.settings.winfo_exists():
            self.var_locked.set(self.cfg["locked"])

    # ----- dados -----
    def refresh(self, force=False):
        c = self.cfg
        if c["source"] == "txt":
            try:
                self.value = read_txt_number(c["txt_path"])
            except Exception:
                self.value = None
            self.render()
            return
        if not c["lat"] or self.fetching:
            return
        if not force and time.time() - self.last_fetch < max(1, int(c["refresh_min"])) * 60:
            return
        self.fetching = True
        self.last_fetch = time.time()
        lat, lon = c["lat"], c["lon"]

        def work():
            try:
                v = fetch_city_temp(lat, lon)
            except Exception:
                v = None

            def done():
                self.fetching = False
                if v is not None:
                    self.value = v
                elif force:
                    self.last_fetch = 0  # tenta de novo no proximo tick
                self.render()
            self.q.put(done)
        threading.Thread(target=work, daemon=True).start()

    def tick(self):
        self.refresh()
        self.root.attributes("-topmost", True)
        self.root.lift()
        self.root.after(TICK_MS if self.cfg["source"] == "city" else 3000, self.tick)

    def poll_queue(self):
        try:
            while True:
                self.q.get_nowait()()
        except queue.Empty:
            pass
        self.root.after(150, self.poll_queue)

    # ----- configuracoes (tokens do ui-ux-pro-max: dark audio + acento quente, grid 8px) -----
    T = {
        "bg": "#0F0F23", "surface": "#1B1B33", "muted": "#27273B", "border": "#3A3A5C",
        "fg": "#F8FAFC", "fg2": "#B4B7D3", "accent": "#F97316", "on_accent": "#0F0F23",
        "focus": "#818CF8", "ok": "#4ADE80", "font": "Segoe UI",
    }

    def init_styles(self):
        T = self.T
        s = ttk.Style(self.root)
        s.theme_use("clam")
        f = (T["font"], 10)
        s.configure(".", background=T["surface"], foreground=T["fg"], font=f, bordercolor=T["border"],
                    lightcolor=T["surface"], darkcolor=T["surface"], focuscolor=T["focus"])
        s.configure("TFrame", background=T["surface"])
        s.configure("Root.TFrame", background=T["bg"])
        s.configure("TLabel", background=T["surface"], foreground=T["fg"])
        s.configure("Hint.TLabel", foreground=T["fg2"], font=(T["font"], 9))
        s.configure("Ok.TLabel", foreground=T["ok"])
        s.configure("Title.TLabel", background=T["bg"], font=(T["font"], 16, "bold"))
        s.configure("Sub.TLabel", background=T["bg"], foreground=T["fg2"])
        s.configure("Sec.TLabel", foreground=T["fg2"], font=(T["font"], 9, "bold"))
        s.configure("TSeparator", background=T["border"])
        for w in ("TEntry", "TSpinbox"):
            s.configure(w, fieldbackground=T["muted"], foreground=T["fg"], insertcolor=T["fg"],
                        bordercolor=T["border"], padding=6, arrowcolor=T["fg"])
            s.map(w, bordercolor=[("focus", T["focus"])], fieldbackground=[("disabled", T["surface"])],
                  foreground=[("disabled", T["fg2"])])
        s.configure("TButton", background=T["muted"], foreground=T["fg"], padding=(14, 7), borderwidth=1,
                    bordercolor=T["border"])
        s.map("TButton", background=[("active", "#34345A"), ("disabled", T["surface"])],
              foreground=[("disabled", T["fg2"])], bordercolor=[("focus", T["focus"])])
        s.configure("Accent.TButton", background=T["accent"], foreground=T["on_accent"],
                    font=(T["font"], 10, "bold"), bordercolor=T["accent"])
        s.map("Accent.TButton", background=[("active", "#FB923C")], bordercolor=[("focus", T["focus"])])
        for w in ("TRadiobutton", "TCheckbutton"):
            s.configure(w, background=T["surface"], foreground=T["fg"], indicatorcolor=T["muted"],
                        indicatorbackground=T["muted"])
            s.map(w, background=[("active", T["surface"])], indicatorcolor=[("selected", T["accent"])],
                  foreground=[("disabled", T["fg2"])])
        s.configure("Horizontal.TScale", background=T["surface"], troughcolor=T["muted"])

    def section(self, parent, title):
        T = self.T
        box = tk.Frame(parent, bg=T["surface"], highlightthickness=1, highlightbackground=T["border"])
        box.pack(fill="x", pady=(0, 16))
        inner = ttk.Frame(box, padding=16)
        inner.pack(fill="x")
        ttk.Label(inner, text=title.upper(), style="Sec.TLabel").grid(row=0, column=0, columnspan=4,
                                                                     sticky="w", pady=(0, 8))
        return inner

    def swatch(self, parent, attr, row, col, label):
        T = self.T
        ttk.Label(parent, text=label).grid(row=row, column=col, sticky="w", padx=(0, 8), pady=4)
        b = tk.Button(parent, width=4, relief="flat", bd=0, cursor="hand2", bg=getattr(self, attr),
                      activebackground=getattr(self, attr), highlightthickness=1,
                      highlightbackground=T["border"], highlightcolor=T["focus"])
        b.grid(row=row, column=col + 1, sticky="w", pady=4, padx=(0, 16))

        def pick():
            c = colorchooser.askcolor(color=getattr(self, attr), parent=self.settings)[1]
            if c:
                setattr(self, attr, c)
                b.configure(bg=c, activebackground=c)
                self.preview()
        b.configure(command=pick)
        return b

    def open_settings(self):
        if self.settings and self.settings.winfo_exists():
            self.settings.deiconify()
            self.settings.lift()
            return
        T = self.T
        c = self.cfg
        self.init_styles()
        self._snap = dict(c)
        self._prev_job = None
        w = self.settings = tk.Toplevel(self.root)
        w.title(f"{APP} - Configurações")
        w.configure(bg=T["bg"])
        w.resizable(False, False)
        w.attributes("-topmost", True)
        w.bind("<Escape>", lambda e: self.cancel_settings())
        w.protocol("WM_DELETE_WINDOW", self.cancel_settings)

        self.sel = {"name": c["city_name"], "desc": c["city_desc"], "lat": c["lat"], "lon": c["lon"]}
        self.results = []
        self.var_source = tk.StringVar(value=c["source"])
        self.var_city_q = tk.StringVar(value=c["city_name"])
        self.var_txt = tk.StringVar(value=c["txt_path"])
        self.var_refresh = tk.IntVar(value=c["refresh_min"])
        self.var_suffix = tk.StringVar(value=c["suffix"])
        self.var_dec = tk.IntVar(value=c["decimals"])
        self.var_showlabel = tk.BooleanVar(value=c["show_label"])
        self.var_label = tk.StringVar(value=c["label"])
        self.var_size = tk.IntVar(value=c["font_size"])
        self.var_style = tk.StringVar(value=c["style"])
        self.var_opacity = tk.IntVar(value=c["opacity"])
        self.var_locked = tk.BooleanVar(value=c["locked"])
        self.var_autostart = tk.BooleanVar(value=c["autostart"])
        self.text_color = c["text_color"]
        self.card_color = c["card_color"]

        outer = ttk.Frame(w, style="Root.TFrame", padding=24)
        outer.pack(fill="both", expand=True)
        ttk.Label(outer, text="TempOverlay", style="Title.TLabel").pack(anchor="w")
        ttk.Label(outer, text="Temperatura sobreposta a qualquer programa da tela.",
                  style="Sub.TLabel").pack(anchor="w", pady=(0, 16))

        # --- fonte ---
        src = self.section(outer, "Fonte da temperatura")
        ttk.Radiobutton(src, text="Cidade (internet)", variable=self.var_source, value="city",
                        command=self.toggle_source).grid(row=1, column=0, columnspan=4, sticky="w")
        ttk.Label(src, text="Cidade").grid(row=2, column=0, sticky="w", pady=(8, 0))
        self.e_city = ttk.Entry(src, textvariable=self.var_city_q, width=34)
        self.e_city.grid(row=3, column=0, columnspan=3, sticky="ew", padx=(0, 8))
        self.e_city.bind("<Return>", lambda ev: self.do_search())
        self.b_search = ttk.Button(src, text="Buscar", command=self.do_search)
        self.b_search.grid(row=3, column=3)
        self.lst = tk.Listbox(src, height=4, width=52, exportselection=False, bg=T["muted"], fg=T["fg"],
                              selectbackground=T["accent"], selectforeground=T["on_accent"], relief="flat",
                              highlightthickness=1, highlightbackground=T["border"],
                              highlightcolor=T["focus"], font=(T["font"], 10), activestyle="none")
        self.lst.grid(row=4, column=0, columnspan=4, sticky="ew", pady=(8, 0))
        self.lst.bind("<<ListboxSelect>>", self.on_pick)
        self.lbl_sel = ttk.Label(src, text=self.sel_text(),
                                 style="Ok.TLabel" if self.sel.get("lat") else "Hint.TLabel")
        self.lbl_sel.grid(row=5, column=0, columnspan=4, sticky="w", pady=(8, 0))
        ttk.Label(src, text="Atualizar a cada (min)").grid(row=6, column=0, sticky="w", pady=(8, 0))
        self.sp_ref = ttk.Spinbox(src, from_=1, to=120, textvariable=self.var_refresh, width=6)
        self.sp_ref.grid(row=6, column=1, sticky="w", pady=(8, 0))

        ttk.Separator(src).grid(row=7, column=0, columnspan=4, sticky="ew", pady=16)
        ttk.Radiobutton(src, text="Arquivo TXT (só um número, ex.: 27,5)", variable=self.var_source,
                        value="txt", command=self.toggle_source).grid(row=8, column=0, columnspan=4, sticky="w")
        ttk.Label(src, text="Arquivo").grid(row=9, column=0, sticky="w", pady=(8, 0))
        self.e_txt = ttk.Entry(src, textvariable=self.var_txt, width=34)
        self.e_txt.grid(row=10, column=0, columnspan=3, sticky="ew", padx=(0, 8))
        self.b_txt = ttk.Button(src, text="Procurar", command=self.pick_txt)
        self.b_txt.grid(row=10, column=3)
        ttk.Label(src, text="O arquivo é relido a cada poucos segundos.", style="Hint.TLabel").grid(
            row=11, column=0, columnspan=4, sticky="w", pady=(4, 0))
        src.columnconfigure(2, weight=1)

        # --- aparencia ---
        vis = self.section(outer, "Aparência (prévia ao vivo)")
        ttk.Label(vis, text="Sufixo").grid(row=1, column=0, sticky="w", pady=4)
        ttk.Entry(vis, textvariable=self.var_suffix, width=7).grid(row=1, column=1, sticky="w", padx=(0, 16))
        ttk.Label(vis, text="Casas decimais").grid(row=1, column=2, sticky="w", padx=(0, 8))
        ttk.Spinbox(vis, from_=0, to=1, textvariable=self.var_dec, width=4).grid(row=1, column=3, sticky="w")
        ttk.Label(vis, text="Tamanho da fonte").grid(row=2, column=0, sticky="w", pady=4)
        ttk.Spinbox(vis, from_=12, to=300, textvariable=self.var_size, width=7).grid(
            row=2, column=1, sticky="w", padx=(0, 16))
        ttk.Label(vis, text="Opacidade").grid(row=2, column=2, sticky="w", padx=(0, 8))
        ttk.Scale(vis, from_=30, to=100, variable=self.var_opacity, length=120).grid(
            row=2, column=3, sticky="w")
        self.swatch(vis, "text_color", 3, 0, "Cor do texto")
        self.swatch(vis, "card_color", 3, 2, "Cor do cartão")
        ttk.Label(vis, text="Estilo").grid(row=4, column=0, sticky="w", pady=4)
        ttk.Radiobutton(vis, text="Transparente", variable=self.var_style, value="transparent").grid(
            row=4, column=1, sticky="w")
        ttk.Radiobutton(vis, text="Cartão", variable=self.var_style, value="card").grid(
            row=4, column=2, sticky="w")
        ttk.Checkbutton(vis, text="Mostrar rótulo", variable=self.var_showlabel).grid(
            row=5, column=0, sticky="w", pady=(8, 0))
        ttk.Entry(vis, textvariable=self.var_label, width=30).grid(
            row=5, column=1, columnspan=3, sticky="ew", pady=(8, 0))
        ttk.Label(vis, text="Rótulo vazio = nome da cidade.", style="Hint.TLabel").grid(
            row=6, column=1, columnspan=3, sticky="w", pady=(4, 0))

        # --- comportamento ---
        misc = self.section(outer, "Comportamento")
        ttk.Checkbutton(misc, text="Travar posição (os cliques passam por cima do painel)",
                        variable=self.var_locked).grid(row=1, column=0, sticky="w")
        ttk.Checkbutton(misc, text="Iniciar com o Windows", variable=self.var_autostart).grid(
            row=2, column=0, sticky="w", pady=(4, 0))
        ttk.Label(misc, text="Destravado: arraste o painel (contorno laranja). Para reabrir esta janela, "
                             "use o ícone perto do relógio.", style="Hint.TLabel", wraplength=470).grid(
            row=3, column=0, sticky="w", pady=(8, 0))

        # --- rodape: 1 acao primaria ---
        foot = ttk.Frame(outer, style="Root.TFrame")
        foot.pack(fill="x")
        ttk.Button(foot, text="Salvar", style="Accent.TButton",
                   command=lambda: self.save_settings(close=True)).pack(side="right")
        ttk.Button(foot, text="Aplicar", command=lambda: self.save_settings(close=False)).pack(
            side="right", padx=8)
        ttk.Button(foot, text="Cancelar", command=self.cancel_settings).pack(side="right")

        for v in (self.var_suffix, self.var_dec, self.var_showlabel, self.var_label, self.var_size,
                  self.var_style, self.var_opacity, self.var_locked):
            v.trace_add("write", lambda *a: self.preview())
        self.toggle_source()
        w.update_idletasks()
        sw, sh = w.winfo_screenwidth(), w.winfo_screenheight()
        w.geometry(f"+{(sw - w.winfo_width()) // 2}+{max(10, (sh - w.winfo_height()) // 3)}")
        self.e_city.focus_set()

    def toggle_source(self):
        city = self.var_source.get() == "city"
        for wd in (self.e_city, self.b_search, self.sp_ref):
            wd.configure(state="normal" if city else "disabled")
        self.lst.configure(state="normal" if city else "disabled")
        for wd in (self.e_txt, self.b_txt):
            wd.configure(state="disabled" if city else "normal")

    def preview(self):
        """Previa ao vivo: aplica no overlay sem gravar em disco (Cancelar desfaz)."""
        if not (self.settings and self.settings.winfo_exists()):
            return
        if self._prev_job:
            self.root.after_cancel(self._prev_job)

        def go():
            self._prev_job = None
            try:
                self.cfg.update({
                    "suffix": self.var_suffix.get(), "decimals": int(self.var_dec.get() or 0),
                    "show_label": bool(self.var_showlabel.get()), "label": self.var_label.get(),
                    "font_size": max(12, int(self.var_size.get() or 48)), "style": self.var_style.get(),
                    "opacity": int(float(self.var_opacity.get())), "text_color": self.text_color,
                    "card_color": self.card_color, "locked": bool(self.var_locked.get()),
                })
            except (tk.TclError, ValueError):
                return  # campo numerico em branco durante a digitacao
            self.apply_style()
            self.render()
        self._prev_job = self.root.after(200, go)

    def cancel_settings(self):
        if self._prev_job:
            self.root.after_cancel(self._prev_job)
            self._prev_job = None
        self.cfg.clear()
        self.cfg.update(self._snap)
        self.apply_style()
        self.render()
        if self.settings and self.settings.winfo_exists():
            self.settings.destroy()

    def sel_text(self):
        return ("Selecionada: " + self.sel["desc"]) if self.sel.get("lat") else "Nenhuma cidade selecionada"

    def do_search(self):
        name = self.var_city_q.get().strip()
        if not name:
            return
        self.lst.delete(0, "end")
        self.lst.insert("end", "Buscando...")

        def work():
            try:
                res = search_city(name)
                err = None
            except Exception as ex:
                res, err = [], str(ex)

            def done():
                if not (self.settings and self.settings.winfo_exists()):
                    return
                self.results = res
                self.lst.delete(0, "end")
                if err:
                    self.lst.insert("end", "Erro na busca (sem internet?)")
                elif not res:
                    self.lst.insert("end", "Nada encontrado")
                for r in res:
                    self.lst.insert("end", r["desc"])
            self.q.put(done)
        threading.Thread(target=work, daemon=True).start()

    def on_pick(self, ev):
        s = self.lst.curselection()
        if s and s[0] < len(self.results):
            self.sel = self.results[s[0]]
            self.lbl_sel.configure(text=self.sel_text(), style="Ok.TLabel")

    def pick_txt(self):
        p = filedialog.askopenfilename(parent=self.settings, title="Arquivo com a temperatura",
                                       filetypes=[("Texto", "*.txt"), ("Todos", "*.*")])
        if p:
            self.var_txt.set(p)
            self.var_source.set("txt")

    def save_settings(self, close):
        c = self.cfg
        if self.var_source.get() == "city" and not self.sel.get("lat"):
            messagebox.showwarning(APP, "Busque e selecione uma cidade (ou escolha a fonte TXT).", parent=self.settings)
            return
        if self.var_source.get() == "txt" and not self.var_txt.get().strip():
            messagebox.showwarning(APP, "Escolha o arquivo TXT.", parent=self.settings)
            return
        c.update({
            "source": self.var_source.get(),
            "city_name": self.sel.get("name", ""),
            "city_desc": self.sel.get("desc", ""),
            "lat": self.sel.get("lat"),
            "lon": self.sel.get("lon"),
            "txt_path": self.var_txt.get().strip(),
            "refresh_min": max(1, int(self.var_refresh.get() or 10)),
            "suffix": self.var_suffix.get(),
            "decimals": int(self.var_dec.get() or 0),
            "show_label": bool(self.var_showlabel.get()),
            "label": self.var_label.get(),
            "font_size": max(12, int(self.var_size.get() or 48)),
            "text_color": self.text_color,
            "card_color": self.card_color,
            "style": self.var_style.get(),
            "opacity": int(float(self.var_opacity.get())),
            "locked": bool(self.var_locked.get()),
            "autostart": bool(self.var_autostart.get()),
        })
        save_cfg(c)
        set_autostart(c["autostart"])
        self.value = None
        self.apply_style()
        self.render()
        self.refresh(force=True)
        if close:
            self.settings.destroy()

    def quit(self):
        try:
            self.tray.stop()
        except Exception:
            pass
        self.root.destroy()

    def run(self):
        self.root.mainloop()


def main():
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass
    # instancia unica
    mutex = ctypes.windll.kernel32.CreateMutexW(None, False, f"Local\\{APP}_single")
    if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        return
    App().run()
    ctypes.windll.kernel32.CloseHandle(mutex)


if __name__ == "__main__":
    main()
