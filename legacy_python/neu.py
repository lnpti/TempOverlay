"""Mini-toolkit Neumorphism (somente claro) para Tkinter, seguindo o ui-ux-pro-max:
base #E0E5EC, sombra dupla (clara em cima/esquerda + escura embaixo/direita), relevo "extruded"
em repouso, "inset" ao pressionar/campos, cantos 12-16px, grade de 8px, foco visivel.
"""
import tkinter as tk

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageTk

BASE = "#E0E5EC"
DARK = "#A3B1C6"
LIGHT = "#FFFFFF"
TEXT = "#1E293B"        # 10:1 sobre a base
TEXT2 = "#475569"       # 5.8:1
MUTED = "#64748B"
ACCENT = "#F97316"      # CTA (texto escuro por cima: 6.4:1)
ACCENT_TEXT = "#C2410C"  # laranja legivel sobre a base (4.5:1)
FOCUS = "#2563EB"
OK = "#166534"
FONT = "Segoe UI"

SC = 1.0


def set_scale(v):
    global SC
    SC = max(1.0, v)


def px(n):
    return max(1, int(round(n * SC)))


_cache = {}


def render(W, H, kind, radius, fill, margin, off, blur, ring=None, glyph=None):
    key = (W, H, kind, radius, fill, margin, off, blur, ring, glyph)
    if key in _cache:
        return _cache[key]
    S = 3 if max(W, H) < 80 else 2
    w, h = W * S, H * S
    img = Image.new("RGB", (w, h), BASE)
    mask = Image.new("L", (w, h), 0)
    box = (margin * S, margin * S, (W - margin) * S - 1, (H - margin) * S - 1)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius * S, fill=255)
    br = max(1, blur * S / 2)
    layers = ((DARK, off, 0.9), (LIGHT, -off, 1.0))
    if kind == "raised":
        for color, d, op in layers:
            sh = Image.new("L", (w, h), 0)
            sh.paste(mask, (d * S, d * S))
            sh = sh.filter(ImageFilter.GaussianBlur(br)).point(lambda v, o=op: int(v * o))
            img.paste(Image.new("RGB", (w, h), color), (0, 0), sh)
        img.paste(Image.new("RGB", (w, h), fill), (0, 0), mask)
    else:
        img.paste(Image.new("RGB", (w, h), fill), (0, 0), mask)
        inv = ImageChops.invert(mask)
        for color, d, op in layers:
            sh = Image.new("L", (w, h), 255)
            sh.paste(inv, (d * S, d * S))
            sh = ImageChops.multiply(sh.filter(ImageFilter.GaussianBlur(br)), mask)
            sh = sh.point(lambda v, o=op: int(v * o))
            img.paste(Image.new("RGB", (w, h), color), (0, 0), sh)
    d = ImageDraw.Draw(img)
    if ring:
        d.rounded_rectangle(box, radius=radius * S, outline=ring, width=2 * S)
    if glyph == "check":
        x0, y0, x1, y1 = box
        sw, sh_ = x1 - x0, y1 - y0
        pts = [(x0 + sw * .26, y0 + sh_ * .52), (x0 + sw * .43, y0 + sh_ * .70), (x0 + sw * .76, y0 + sh_ * .32)]
        d.line(pts, fill=ACCENT, width=int(2.6 * S * SC), joint="curve")
    elif glyph == "dot":
        cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
        r = (box[2] - box[0]) * 0.22
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=ACCENT)
    im = ImageTk.PhotoImage(img.resize((W, H), Image.LANCZOS))
    _cache[key] = im
    return im


class NeuBox(tk.Frame):
    """Superficie com relevo. Coloque o conteudo em `.body`."""

    def __init__(self, parent, kind="raised", radius=14, margin=8, pad=12, fill=BASE, off=4, blur=6, **kw):
        super().__init__(parent, bg=BASE, **kw)
        self.kind, self.fill, self.ring, self.glyph = kind, fill, None, None
        self.r, self.m, self.off, self.blur = px(radius), px(margin), px(off), px(blur)
        self.bgl = tk.Label(self, bd=0, bg=BASE)
        self.bgl.place(x=0, y=0, relwidth=1, relheight=1)
        self.body = tk.Frame(self, bg=fill)
        self.body.grid(row=0, column=0, padx=self.m + px(pad), pady=self.m + px(pad))
        self._size = None
        tk.Frame.bind(self, "<Configure>", self._on_cfg)

    def _on_cfg(self, e):
        if (e.width, e.height) != self._size:
            self._size = (e.width, e.height)
            self.redraw()

    def redraw(self):
        if not self._size:
            return
        W, H = self._size
        im = render(W, H, self.kind, self.r, self.fill, self.m, self.off, self.blur, self.ring, self.glyph)
        self.bgl.configure(image=im)
        self.bgl.image = im

    def set_state(self, state):
        pass

    def configure(self, cnf=None, **kw):
        if "state" in kw:
            self.set_state(kw.pop("state"))
        if cnf or kw:
            return super().configure(cnf, **kw)


class NeuButton(NeuBox):
    def __init__(self, parent, text, command=None, primary=False, margin=8, pad=6, bold=False, width=0,
                 off=4, blur=6, radius=14):
        fill = ACCENT if primary else BASE
        super().__init__(parent, "raised", radius=radius, margin=margin, pad=pad, fill=fill, off=off, blur=blur,
                         takefocus=True)
        self.cmd, self.primary, self.disabled, self.selected, self._down_on = command, primary, False, False, False
        self._bold = bold or primary
        self.lbl = tk.Label(self.body, text=text, bg=fill, fg=TEXT, width=width, cursor="hand2",
                            font=(FONT, 10, "bold" if self._bold else "normal"))
        self.lbl.grid(padx=px(8), pady=px(3))
        for wdg in (self, self.bgl, self.body, self.lbl):
            wdg.bind("<ButtonPress-1>", self._down)
            wdg.bind("<ButtonRelease-1>", self._up)
        tk.Frame.bind(self, "<FocusIn>", lambda e: self._ring(FOCUS))
        tk.Frame.bind(self, "<FocusOut>", lambda e: self._ring(None))
        tk.Frame.bind(self, "<Return>", lambda e: self.invoke())
        tk.Frame.bind(self, "<space>", lambda e: self.invoke())

    def _ring(self, c):
        self.ring = c
        self.redraw()

    def _down(self, e):
        if self.disabled:
            return
        self._down_on = True
        self.kind = "inset"
        self.redraw()

    def _up(self, e):
        if not self._down_on:
            return
        self._down_on = False
        self.kind = "inset" if self.selected else "raised"
        self.redraw()
        w = self.winfo_containing(e.x_root, e.y_root)
        if w in (self, self.bgl, self.body, self.lbl):
            self.invoke()

    def invoke(self):
        if not self.disabled and self.cmd:
            self.cmd()

    def set_selected(self, flag):
        self.selected = flag
        self.kind = "inset" if flag else "raised"
        self.lbl.configure(fg=ACCENT_TEXT if flag else TEXT, font=(FONT, 10, "bold" if flag or self._bold else "normal"))
        self.redraw()

    def set_state(self, state):
        self.disabled = state == "disabled"
        self.lbl.configure(fg=MUTED if self.disabled else TEXT, cursor="" if self.disabled else "hand2")
        super().configure(takefocus=not self.disabled)


class NeuEntry(NeuBox):
    def __init__(self, parent, textvariable=None, width=24, margin=6):
        super().__init__(parent, "inset", radius=12, margin=margin, pad=6, off=3, blur=5)
        self.entry = tk.Entry(self.body, textvariable=textvariable, width=width, bd=0, relief="flat", bg=BASE,
                              fg=TEXT, insertbackground=TEXT, disabledbackground=BASE, disabledforeground=MUTED,
                              highlightthickness=0, selectbackground=ACCENT, selectforeground=TEXT, font=(FONT, 10))
        self.entry.grid(padx=px(6), pady=px(4))
        self.entry.bind("<FocusIn>", lambda e: self._ring(FOCUS))
        self.entry.bind("<FocusOut>", lambda e: self._ring(None))

    def _ring(self, c):
        self.ring = c
        self.redraw()

    def set_state(self, state):
        self.entry.configure(state=state)

    def focus_set(self):
        self.entry.focus_set()

    def bind(self, seq, fn, add=None):
        return self.entry.bind(seq, fn, add)


class NeuSpin(tk.Frame):
    def __init__(self, parent, var, lo, hi, width=5):
        super().__init__(parent, bg=BASE)
        self.var, self.lo, self.hi = var, lo, hi
        self.e = NeuEntry(self, textvariable=var, width=width)
        self.e.grid(row=0, column=0)
        self.bm = NeuButton(self, "−", lambda: self.step(-1), margin=8, pad=1, off=3, blur=4, radius=8)
        self.bp = NeuButton(self, "+", lambda: self.step(1), margin=8, pad=1, off=3, blur=4, radius=8)
        self.bm.grid(row=0, column=1)
        self.bp.grid(row=0, column=2)

    def step(self, d):
        try:
            v = int(float(self.var.get()))
        except (tk.TclError, ValueError):
            v = self.lo
        self.var.set(max(self.lo, min(self.hi, v + d)))

    def configure(self, cnf=None, **kw):
        if "state" in kw:
            st = kw.pop("state")
            for wdg in (self.e, self.bm, self.bp):
                wdg.set_state(st)
        if cnf or kw:
            return super().configure(cnf, **kw)


class NeuCheck(tk.Frame):
    """Caixa (extruded -> inset com marca laranja) ou botao de opcao (value != None)."""

    def __init__(self, parent, text, variable, value=None, command=None, wrap=0):
        super().__init__(parent, bg=BASE, takefocus=True, highlightthickness=2,
                         highlightbackground=BASE, highlightcolor=FOCUS)
        self.var, self.value, self.cmd, self.radio, self.disabled = variable, value, command, value is not None, False
        self.ind = tk.Label(self, bd=0, bg=BASE, cursor="hand2")
        self.ind.grid(row=0, column=0, padx=(0, px(6)))
        self.txt = tk.Label(self, text=text, bg=BASE, fg=TEXT, font=(FONT, 10), justify="left", anchor="w",
                            cursor="hand2", wraplength=wrap)
        self.txt.grid(row=0, column=1, sticky="w")
        for wdg in (self.ind, self.txt):
            wdg.bind("<ButtonRelease-1>", self.toggle)
        self.bind("<space>", self.toggle)
        self.bind("<Return>", self.toggle)
        self._tid = variable.trace_add("write", lambda *a: self._sync())
        self._sync()

    def selected(self):
        try:
            return self.var.get() == self.value if self.radio else bool(self.var.get())
        except tk.TclError:
            return False

    def toggle(self, e=None):
        if self.disabled:
            return
        self.var.set(self.value if self.radio else not bool(self.var.get()))
        if self.cmd:
            self.cmd()

    def _sync(self):
        try:
            on = self.selected()
            size = px(32)
            m = px(7)
            r = (size - 2 * m) // 2 if self.radio else px(6)
            im = render(size, size, "inset" if on else "raised", r, BASE, m, px(2), px(4),
                        None, ("dot" if self.radio else "check") if on else None)
            self.ind.configure(image=im)
            self.ind.image = im
        except tk.TclError:
            pass


class NeuListbox(NeuBox):
    def __init__(self, parent, height=4, width=44):
        super().__init__(parent, "inset", radius=12, margin=6, pad=4, off=3, blur=5)
        self.lb = tk.Listbox(self.body, height=height, width=width, bd=0, relief="flat", bg=BASE, fg=TEXT,
                             selectbackground=ACCENT, selectforeground=TEXT, highlightthickness=0,
                             exportselection=False, activestyle="none", font=(FONT, 10))
        self.lb.grid(padx=px(6), pady=px(4))


class NeuSwatch(NeuBox):
    def __init__(self, parent, color, command):
        super().__init__(parent, "raised", radius=12, margin=6, pad=4, off=3, blur=5, takefocus=True)
        self.cmd = command
        self.chip = tk.Frame(self.body, width=px(40), height=px(20), bg=color, cursor="hand2",
                             highlightthickness=1, highlightbackground=TEXT2)
        self.chip.grid()
        for wdg in (self, self.bgl, self.body, self.chip):
            wdg.bind("<ButtonRelease-1>", lambda e: self.cmd())
        tk.Frame.bind(self, "<Return>", lambda e: self.cmd())
        tk.Frame.bind(self, "<space>", lambda e: self.cmd())
        tk.Frame.bind(self, "<FocusIn>", lambda e: self._ring(FOCUS))
        tk.Frame.bind(self, "<FocusOut>", lambda e: self._ring(None))

    def _ring(self, c):
        self.ring = c
        self.redraw()

    def set_color(self, c):
        self.chip.configure(bg=c)


def label(parent, text, kind="text", **kw):
    fg, font = {"text": (TEXT, (FONT, 10)), "hint": (TEXT2, (FONT, 9)), "title": (TEXT, (FONT, 18, "bold")),
                "sub": (TEXT2, (FONT, 10)), "ok": (OK, (FONT, 10, "bold"))}[kind]
    return tk.Label(parent, text=text, bg=BASE, fg=fg, font=font, anchor="w", justify="left", **kw)


def groove(parent):
    f = tk.Frame(parent, bg=BASE)
    tk.Frame(f, bg=DARK, height=1).pack(fill="x")
    tk.Frame(f, bg=LIGHT, height=1).pack(fill="x")
    return f
