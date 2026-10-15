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
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import tkinter as tk
import urllib.parse
import urllib.request
from tkinter import colorchooser, filedialog, messagebox, ttk

import pystray
from PIL import Image, ImageDraw

APP = "TempOverlay"
VERSION = "1.1.0"
GITHUB_REPO = "lnpti/TempOverlay"
R2_MANIFEST = "https://pub-8060abbe70084968817647c74ce4ffbc.r2.dev/tempoverlay/latest.json"
UPDATE_HOUR = 3  # checagem de madrugada (alem de a cada 24h)
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
    "auto_update": True,
    "last_update_check": 0,
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


def _ver(s):
    return tuple(int(x) for x in re.findall(r"\d+", s)[:3])


def latest_release():
    """(versao, url do instalador): GitHub primeiro, espelho R2 se o GitHub falhar."""
    try:
        d = http_json(f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest", 15)
        a = next(x for x in d["assets"] if x["name"].lower().endswith(".exe") and "setup" in x["name"].lower())
        return d["tag_name"].lstrip("v"), a["browser_download_url"]
    except Exception:
        d = http_json(R2_MANIFEST, 15)
        return d["version"], d["installer_url"]


def download_installer(url):
    dest = os.path.join(tempfile.mkdtemp(prefix="tempoverlay_upd_"), "TempOverlay_Setup.exe")
    req = urllib.request.Request(url, headers={"User-Agent": f"{APP}/{VERSION}"})
    with urllib.request.urlopen(req, timeout=60) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)
    with open(dest, "rb") as f:
        ok = f.read(2) == b"MZ"
    if not ok or os.path.getsize(dest) < 1_000_000:
        raise ValueError("download invalido")
    return dest


def run_installer_silent(path):
    # PYINSTALLER_RESET_ENVIRONMENT: o app reaberto pelo instalador nao pode herdar o _MEIPASS deste processo
    env = dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT="1")
    subprocess.Popen([path, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS"],
                     env=env, close_fds=True, creationflags=0x00000008 | 0x00000200)  # DETACHED | NEW_GROUP


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
        self.t0 = time.time()
        self.updating = False
        self.upd_retry_at = 0.0

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
            pystray.MenuItem("Verificar atualização do app", lambda: self.q.put(lambda: self.check_update(manual=True))),
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

    def update_due(self):
        if not self.cfg["auto_update"] or self.updating or time.time() < self.upd_retry_at:
            return False
        if time.time() - self.t0 < 30:
            return False
        now, last = time.time(), self.cfg["last_update_check"]
        if now - last >= 24 * 3600:
            return True
        day = lambda t: time.strftime("%Y%m%d", time.localtime(t))
        return time.localtime(now).tm_hour == UPDATE_HOUR and day(now) != day(last)

    def check_update(self, manual=False):
        if self.updating:
            return
        if not getattr(sys, "frozen", False):
            if manual:
                messagebox.showinfo(APP, "Atualização automática só funciona na versão instalada.")
            return
        self.updating = True

        def finish(msg=None, retry=False, installer=None):
            self.updating = False
            if retry:
                self.upd_retry_at = time.time() + 1800
            else:
                self.cfg["last_update_check"] = time.time()
                save_cfg(self.cfg)
            if installer:
                run_installer_silent(installer)
                self.root.after(600, self.quit)
            elif msg and manual:
                messagebox.showinfo(APP, msg)

        def work():
            try:
                ver, url = latest_release()
                if _ver(ver) <= _ver(VERSION):
                    self.q.put(lambda: finish(f"Você já está na versão mais recente ({VERSION})."))
                    return
                path = download_installer(url)
                self.q.put(lambda: finish(installer=path))
            except Exception:
                self.q.put(lambda: finish("Não foi possível verificar agora (sem internet?).", retry=True))
        threading.Thread(target=work, daemon=True).start()

    def tick(self):
        self.refresh()
        if self.update_due():
            self.check_update()
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

    # ----- configuracoes: Neumorphism claro (ui-ux-pro-max), ver neu.py -----
    def open_settings(self):
        if self.settings and self.settings.winfo_exists():
            self.settings.deiconify()
            self.settings.lift()
            return
        import neu
        from neu import NeuBox, NeuButton, NeuCheck, NeuEntry, NeuListbox, NeuSpin, NeuSwatch, groove, label, px
        neu.set_scale(self.root.winfo_fpixels("1i") / 96)
        c = self.cfg
        self._snap = dict(c)
        self._prev_job = None
        w = self.settings = tk.Toplevel(self.root)
        w.title(f"{APP} - Configurações")
        w.configure(bg=neu.BASE)
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
        self.var_autoupd = tk.BooleanVar(value=c["auto_update"])
        self.text_color = c["text_color"]
        self.card_color = c["card_color"]

        outer = tk.Frame(w, bg=neu.BASE)
        outer.pack(padx=px(24), pady=px(20))
        label(outer, "TempOverlay", "title").grid(row=0, column=0, sticky="w", padx=px(8))
        label(outer, "Temperatura sobreposta a qualquer programa da tela.", "sub").grid(
            row=1, column=0, sticky="w", padx=px(8), pady=(0, px(8)))

        tabs = tk.Frame(outer, bg=neu.BASE)
        tabs.grid(row=2, column=0, sticky="w")
        card = NeuBox(outer, "raised", radius=20, margin=12, pad=16, off=6, blur=10)
        card.grid(row=3, column=0, sticky="ew")
        names = ("Fonte", "Aparência", "Geral")
        pages = [tk.Frame(card.body, bg=neu.BASE) for _ in names]
        btns = []
        for i, n in enumerate(names):
            pages[i].grid(row=0, column=0, sticky="nsew")

            def show(i=i):
                pages[i].tkraise()
                for j, b in enumerate(btns):
                    b.set_selected(j == i)
            b = NeuButton(tabs, n, show, margin=8, pad=4, width=10)
            b.grid(row=0, column=i)
            btns.append(b)
        src, vis, misc = pages
        pw = px(6)

        # --- Fonte ---
        NeuCheck(src, "Cidade (internet)", self.var_source, "city", self.toggle_source).grid(
            row=0, column=0, sticky="w", pady=(0, pw))
        label(src, "Cidade").grid(row=1, column=0, sticky="w", padx=px(6))
        rowc = tk.Frame(src, bg=neu.BASE)
        rowc.grid(row=2, column=0, sticky="w")
        self.e_city = NeuEntry(rowc, textvariable=self.var_city_q, width=32)
        self.e_city.grid(row=0, column=0)
        self.e_city.bind("<Return>", lambda ev: self.do_search())
        self.b_search = NeuButton(rowc, "Buscar", self.do_search)
        self.b_search.grid(row=0, column=1)
        lbox = NeuListbox(src, height=4, width=44)
        lbox.grid(row=3, column=0, sticky="w")
        self.lst = lbox.lb
        self.lst.bind("<<ListboxSelect>>", self.on_pick)
        self.lbl_sel = label(src, self.sel_text(), "ok" if self.sel.get("lat") else "hint")
        self.lbl_sel.grid(row=4, column=0, sticky="w", padx=px(6), pady=(pw, 0))
        rowr = tk.Frame(src, bg=neu.BASE)
        rowr.grid(row=5, column=0, sticky="w", pady=(pw, 0))
        label(rowr, "Atualizar a cada (min)").grid(row=0, column=0, padx=(px(6), px(8)))
        self.sp_ref = NeuSpin(rowr, self.var_refresh, 1, 120, width=4)
        self.sp_ref.grid(row=0, column=1)
        groove(src).grid(row=6, column=0, sticky="ew", pady=px(14), padx=px(6))
        NeuCheck(src, "Arquivo TXT (só um número, ex.: 27,5)", self.var_source, "txt", self.toggle_source).grid(
            row=7, column=0, sticky="w", pady=(0, pw))
        label(src, "Arquivo").grid(row=8, column=0, sticky="w", padx=px(6))
        rowt = tk.Frame(src, bg=neu.BASE)
        rowt.grid(row=9, column=0, sticky="w")
        self.e_txt = NeuEntry(rowt, textvariable=self.var_txt, width=32)
        self.e_txt.grid(row=0, column=0)
        self.b_txt = NeuButton(rowt, "Procurar", self.pick_txt)
        self.b_txt.grid(row=0, column=1)
        label(src, "O arquivo é relido a cada poucos segundos.", "hint").grid(
            row=10, column=0, sticky="w", padx=px(6))

        # --- Aparencia ---
        label(vis, "Prévia ao vivo no painel da tela.", "hint").grid(row=0, column=0, columnspan=4, sticky="w", padx=px(6))
        label(vis, "Sufixo").grid(row=1, column=0, sticky="w", padx=px(6))
        NeuEntry(vis, textvariable=self.var_suffix, width=6).grid(row=1, column=1, sticky="w")
        label(vis, "Casas decimais").grid(row=1, column=2, sticky="w", padx=(px(16), px(6)))
        NeuSpin(vis, self.var_dec, 0, 1, width=3).grid(row=1, column=3, sticky="w")
        label(vis, "Tamanho da fonte").grid(row=2, column=0, sticky="w", padx=px(6))
        NeuSpin(vis, self.var_size, 12, 300, width=5).grid(row=2, column=1, sticky="w")
        label(vis, "Opacidade").grid(row=2, column=2, sticky="w", padx=(px(16), px(6)))
        sc = NeuBox(vis, "inset", radius=10, margin=6, pad=3, off=3, blur=4)
        sc.grid(row=2, column=3, sticky="w")
        tk.Scale(sc.body, from_=30, to=100, orient="horizontal", variable=self.var_opacity, showvalue=0,
                 length=px(130), width=px(12), sliderlength=px(22), bd=0, relief="flat", sliderrelief="flat",
                 bg=neu.ACCENT, activebackground=neu.ACCENT, troughcolor=neu.BASE, highlightthickness=0).grid()
        label(vis, "Cor do texto").grid(row=3, column=0, sticky="w", padx=px(6))
        self.sw_text = NeuSwatch(vis, self.text_color, lambda: self.pick_color("text_color", self.sw_text))
        self.sw_text.grid(row=3, column=1, sticky="w")
        label(vis, "Cor do cartão").grid(row=3, column=2, sticky="w", padx=(px(16), px(6)))
        self.sw_card = NeuSwatch(vis, self.card_color, lambda: self.pick_color("card_color", self.sw_card))
        self.sw_card.grid(row=3, column=3, sticky="w")
        label(vis, "Estilo").grid(row=4, column=0, sticky="w", padx=px(6))
        NeuCheck(vis, "Transparente", self.var_style, "transparent").grid(row=4, column=1, sticky="w", pady=pw)
        NeuCheck(vis, "Cartão", self.var_style, "card").grid(row=4, column=2, columnspan=2, sticky="w", padx=(px(16), 0))
        NeuCheck(vis, "Mostrar rótulo", self.var_showlabel).grid(row=5, column=0, sticky="w", pady=(pw, 0))
        NeuEntry(vis, textvariable=self.var_label, width=24).grid(row=5, column=1, columnspan=3, sticky="w", pady=(pw, 0))
        label(vis, "Rótulo vazio = nome da cidade.", "hint").grid(row=6, column=1, columnspan=3, sticky="w", padx=px(6))

        # --- Geral ---
        NeuCheck(misc, "Travar posição (os cliques passam por cima do painel)", self.var_locked).grid(
            row=0, column=0, sticky="w", pady=pw)
        NeuCheck(misc, "Iniciar com o Windows", self.var_autostart).grid(row=1, column=0, sticky="w", pady=pw)
        NeuCheck(misc, "Atualizar sozinho (a cada 24 h e de madrugada) e reabrir como estava",
                 self.var_autoupd).grid(row=2, column=0, sticky="w", pady=pw)
        NeuButton(misc, f"Verificar atualização agora (v{VERSION})",
                  lambda: self.check_update(manual=True)).grid(row=3, column=0, sticky="w", pady=(px(8), 0))
        label(misc, "Destravado: arraste o painel (contorno laranja). Para reabrir esta janela, use o ícone "
                    "perto do relógio.", "hint", wraplength=px(420)).grid(
            row=4, column=0, sticky="w", padx=px(6), pady=(px(8), 0))

        # --- rodape: uma unica acao primaria ---
        foot = tk.Frame(outer, bg=neu.BASE)
        foot.grid(row=4, column=0, sticky="e", pady=(px(8), 0))
        NeuButton(foot, "Cancelar", self.cancel_settings).grid(row=0, column=0)
        NeuButton(foot, "Aplicar", lambda: self.save_settings(close=False)).grid(row=0, column=1)
        NeuButton(foot, "Salvar", lambda: self.save_settings(close=True), primary=True).grid(row=0, column=2)

        for v in (self.var_suffix, self.var_dec, self.var_showlabel, self.var_label, self.var_size,
                  self.var_style, self.var_opacity, self.var_locked):
            v.trace_add("write", lambda *a: self.preview())
        btns[0].set_selected(True)
        self.toggle_source()
        w.update_idletasks()
        sw, sh = w.winfo_screenwidth(), w.winfo_screenheight()
        w.geometry(f"+{(sw - w.winfo_width()) // 2}+{max(10, (sh - w.winfo_height()) // 3)}")
        self.e_city.focus_set()

    def pick_color(self, attr, swatch):
        c = colorchooser.askcolor(color=getattr(self, attr), parent=self.settings)[1]
        if c:
            setattr(self, attr, c)
            swatch.set_color(c)
            self.preview()

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
                    "suffix": self.var_suffix.get(), "decimals": self._i(self.var_dec, 0),
                    "show_label": bool(self.var_showlabel.get()), "label": self.var_label.get(),
                    "font_size": max(12, self._i(self.var_size, 48)), "style": self.var_style.get(),
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

    @staticmethod
    def _i(var, default):
        try:
            return int(float(var.get()))
        except (tk.TclError, ValueError):
            return default

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
            self.lbl_sel.configure(text=self.sel_text(), fg="#166534", font=("Segoe UI", 10, "bold"))

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
            "refresh_min": max(1, self._i(self.var_refresh, 10)),
            "suffix": self.var_suffix.get(),
            "decimals": self._i(self.var_dec, 0),
            "show_label": bool(self.var_showlabel.get()),
            "label": self.var_label.get(),
            "font_size": max(12, self._i(self.var_size, 48)),
            "text_color": self.text_color,
            "card_color": self.card_color,
            "style": self.var_style.get(),
            "opacity": self._i(self.var_opacity, 100),
            "locked": bool(self.var_locked.get()),
            "autostart": bool(self.var_autostart.get()),
            "auto_update": bool(self.var_autoupd.get()),
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
