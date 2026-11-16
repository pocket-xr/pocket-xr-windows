"""Pocket XR for Windows: the PC's screens as windows held still in space on XR glasses.

One borderless window covers the glasses' own display. Each PC screen is a panel around the head; the head's turn,
read from the glasses' sensor stream, is undone before every frame. Nothing is sent to the glasses unless --settings
is given.

Keys, from anywhere:
  Ctrl+Alt+R          recentre: the main screen comes in front of you
  Ctrl+Alt+Up/Down    bigger / smaller
  Ctrl+Alt+PgUp/PgDn  further / nearer (seen in 3D)
  Ctrl+Alt+1/2/3      how many screens are shown
  Ctrl+Alt+D          3D side by side on or off (only with --settings)
  Ctrl+Alt+Q          quit
"""
import argparse
import ctypes
import math
import os
import struct
import sys
import threading
import time
import zlib

from . import capture, displays, fusion, glasses, imu, win
from .win import gl

VERSION = "0.0.1"
FOV_V_DEG = 38.0  # the Aura's picture, vertical, per eye (the Deck's number)
EYE_M = 0.032  # half the distance between the eyes
GAP_DEG = 2.0
MAX_SCREENS = 3
K_RECENTRE, K_QUIT, K_BIGGER, K_SMALLER, K_FURTHER, K_NEARER, K_3D, K_ONE, K_TWO, K_THREE = range(1, 11)
# The pointer, in source pixels from its tip.
ARROW = ((0, 0), (0, 17), (4, 13.2), (7.2, 20), (9.8, 18.8), (6.7, 12.2), (12, 12))
ARROW_FILL = ((0, 1, 2), (0, 2, 6), (2, 5, 6), (2, 3, 4), (2, 4, 5))


class Log:
    def __init__(self, path=None):
        self.lock = threading.Lock()
        self.file = None
        if path:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            if os.path.exists(path) and os.path.getsize(path) > 2_000_000:
                os.replace(path, path + ".1")
            self.file = open(path, "a", encoding="utf-8")

    def __call__(self, msg):
        line = time.strftime("%H:%M:%S ") + str(msg)
        with self.lock:
            try:
                print(line, flush=True)
            except OSError:
                pass
            if self.file:
                self.file.write(line + "\n")
                self.file.flush()


class Source:
    """One PC screen shown as a panel."""

    def __init__(self, display, log):
        self.display = display
        self.capture = capture.ScreenCapture(display.gdi, log)
        self.capture.start()
        self.texture = ctypes.c_uint(0)
        self.tex_size = (0, 0)
        self.version = 0
        self.yaw = 0.0
        self.counted = 0

    def upload(self):
        def use(w, h, buf):
            if not self.texture.value:
                gl.glGenTextures(1, ctypes.byref(self.texture))
            gl.glBindTexture(win.GL_TEXTURE_2D, self.texture)
            if (w, h) != self.tex_size:
                for name, value in ((win.GL_TEXTURE_MIN_FILTER, win.GL_LINEAR), (win.GL_TEXTURE_MAG_FILTER, win.GL_LINEAR),
                                    (win.GL_TEXTURE_WRAP_S, win.GL_CLAMP_TO_EDGE), (win.GL_TEXTURE_WRAP_T, win.GL_CLAMP_TO_EDGE)):
                    gl.glTexParameteri(win.GL_TEXTURE_2D, name, value)
                gl.glTexImage2D(win.GL_TEXTURE_2D, 0, win.GL_RGBA, w, h, 0, win.GL_BGRA, win.GL_UNSIGNED_BYTE, buf)
                self.tex_size = (w, h)
            else:
                gl.glTexSubImage2D(win.GL_TEXTURE_2D, 0, 0, 0, w, h, win.GL_BGRA, win.GL_UNSIGNED_BYTE, buf)
        self.version = self.capture.take(self.version, use)

    def close(self):
        self.capture.stop = True
        if self.texture.value:
            gl.glDeleteTextures(1, ctypes.byref(self.texture))


class App:
    def __init__(self, args, log):
        self.args = args
        self.log = log
        self.size_deg = args.size
        self.distance = args.distance
        self.screens = max(1, min(MAX_SCREENS, args.screens))
        self.want_3d = True
        self.sources = []
        self.glasses_display = None
        self.shown = False
        self.settings = None
        self.frames = 0
        self.quit = False

        self.glasses = glasses.detect()
        log(f"Pocket XR for Windows {VERSION}")
        log(f"glasses on USB: {self.glasses.name if self.glasses else 'none found'}")
        self.head = None
        self.reader = None
        if not args.no_tracking:
            self.head = fusion.HeadFusion(self.glasses.axes if self.glasses else "aura")
            self.reader = imu.FakeImu(self.head.report) if args.fake else imu.ImuReader(self.head.report, log=log)
            self.reader.start()

        if args.window:
            rect = (120, 120, 1280 if not args.sbs else 1920, 720 if not args.sbs else 540)
        else:
            rect = (0, 0, 640, 360)
        self.window = win.Window("Pocket XR", rect, fullscreen=not args.window, visible=bool(args.window))
        self.log(f"OpenGL: {(gl.glGetString(0x1F01) or b'?').decode(errors='replace')}, vsync {'on' if self.window.vsync else 'off'}")
        for key_id, name in ((K_RECENTRE, "R"), (K_QUIT, "Q"), (K_BIGGER, "UP"), (K_SMALLER, "DOWN"), (K_FURTHER, "PRIOR"),
                             (K_NEARER, "NEXT"), (K_3D, "D"), (K_ONE, "1"), (K_TWO, "2"), (K_THREE, "3")):
            if not self.window.hotkey(key_id, win.VK[name]):
                log(f"Ctrl+Alt+{name} is taken by another app")
        self._displays(first=True)
        if args.settings:
            self._start_settings()

    # ---- displays ----
    def _displays(self, first=False):
        found = displays.displays()
        gd, others = displays.split(found, self.args.display)
        if self.args.window:
            gd, others = None, sorted(found, key=lambda d: (not d.primary, d.x, d.y))
        if first:
            for d in found:
                self.log(f"display: {d}{' (glasses)' if d == gd else ''}")
        if gd != self.glasses_display:
            self.glasses_display = gd
            if not self.args.window:
                if gd:
                    self.log(f"glasses picture: {gd}")
                    self.window.move((gd.x, gd.y, gd.w, gd.h))
                    self.shown = True
                else:
                    self.log("no glasses display. Plug the glasses in, or try --display N or --window.")
                    self.window.show(False)
                    self.shown = False
        want = others[:self.screens]
        if [s.display for s in self.sources] != want:
            for s in self.sources:
                s.close()
            self.sources = [Source(d, self.log) for d in want]
            self._layout()
        self.last_display_check = time.monotonic()

    def _layout(self):
        """The main screen straight ahead, the others beside it in the order they sit on the desktop."""
        if not self.sources:
            return
        row = sorted(self.sources, key=lambda s: s.display.x)
        main = next((i for i, s in enumerate(row) if s.display.primary), 0)
        for i, s in enumerate(row):
            s.yaw = (main - i) * (self.size_deg + GAP_DEG)  # left is positive
        self.log("showing: " + ", ".join(f'"{s.display.name}" at {s.yaw:+.0f} deg' for s in row))

    # ---- the glasses' settings (only with --settings) ----
    def _start_settings(self):
        if not self.glasses or not self.glasses.control_link:
            self.log("settings: these glasses have no control link Pocket XR knows")
            return
        from .settingsctl import SettingsControl
        self.settings = SettingsControl(self.glasses, self.log, lambda st: None, lambda m: self.log(f"glasses: {m}"))
        self.settings.want_3d = lambda: self.want_3d
        self.settings.start()
        self.settings.want("space")

    # ---- keys ----
    def _keys(self, pressed):
        for k in pressed:
            if k == K_QUIT:
                self.quit = True
            elif k == K_RECENTRE and self.head:
                self.head.recentre(0.0)
                self.log("recentre")
            elif k in (K_BIGGER, K_SMALLER):
                self.size_deg = min(100.0, max(12.0, self.size_deg * (1.1 if k == K_BIGGER else 1 / 1.1)))
                self._layout()
            elif k in (K_FURTHER, K_NEARER):
                self.distance = min(15.0, max(0.3, self.distance * (1.15 if k == K_FURTHER else 1 / 1.15)))
                self.log(f"distance {self.distance:.2f} m")
            elif k in (K_ONE, K_TWO, K_THREE):
                self.screens = k - K_ONE + 1
                self._displays()
            elif k == K_3D:
                if self.settings:
                    self.want_3d = not self.want_3d
                    self.settings.sbs_tries = 0
                    self.settings.sbs_first_at = None
                    self.settings.want("space")
                    self.log(f"3D side by side wanted: {self.want_3d}")
                else:
                    self.log("3D needs --settings: Pocket XR sends nothing to the glasses without it")

    # ---- drawing ----
    def _pointer_home(self):
        """The pointer cannot be found on the glasses' own display, so it is put back on the main screen."""
        g = self.glasses_display
        if not g or not self.sources:
            return
        x, y = win.cursor_pos()
        if g.x <= x < g.x + g.w and g.y <= y < g.y + g.h:
            m = next((s.display for s in self.sources if s.display.primary), self.sources[0].display)
            win.set_cursor_pos(min(max(x, m.x + 2), m.x + m.w - 3), min(max(y, m.y + 2), m.y + m.h - 3))

    def frame(self):
        w, h = self.window.size()
        if w <= 0 or h <= 0:
            return
        for s in self.sources:
            s.upload()
        gl.glClearColor(0.0, 0.0, 0.0, 1.0)
        gl.glClear(win.GL_COLOR_BUFFER_BIT)
        sbs = w >= 3 * h or self.args.sbs
        eyes = [(-EYE_M, 0, w // 2), (EYE_M, w // 2, w // 2)] if sbs else [(0.0, 0, w)]
        view = self.head.view() if self.head else (1.0, 0.0, 0.0, 0.0)
        m = (ctypes.c_float * 16)(*fusion.matrix(fusion.qconj(view)))
        cursor = win.cursor_pos()
        for eye_x, vx, vw in eyes:
            gl.glViewport(vx, 0, vw, h)
            gl.glMatrixMode(win.GL_PROJECTION)
            gl.glLoadIdentity()
            near = 0.05
            top = near * math.tan(math.radians(FOV_V_DEG) / 2)
            gl.glFrustum(-top * vw / h, top * vw / h, -top, top, near, 100.0)
            gl.glMatrixMode(win.GL_MODELVIEW)
            gl.glLoadIdentity()
            gl.glTranslatef(-eye_x, 0.0, 0.0)
            gl.glMultMatrixf(m)
            for s in self.sources:
                self._panel(s, cursor)
        self._edge_fade(w, h, sbs)
        if self.args.shot and time.monotonic() - self.started > self.args.shot_after:
            save_png(self.args.shot, w, h)
            self.log(f"saved {self.args.shot}")
            self.quit = True
        self.window.swap()
        self.frames += 1

    def _panel(self, s, cursor):
        if not s.texture.value:
            return
        d = self.distance
        sw, sh = s.tex_size
        half_w = d * math.tan(math.radians(self.size_deg) / 2)
        half_h = half_w * sh / sw
        gl.glPushMatrix()
        gl.glRotatef(s.yaw, 0.0, 1.0, 0.0)
        gl.glTranslatef(0.0, 0.0, -d)
        gl.glDisable(win.GL_BLEND)
        gl.glEnable(win.GL_TEXTURE_2D)
        gl.glBindTexture(win.GL_TEXTURE_2D, s.texture)
        gl.glColor4f(1.0, 1.0, 1.0, 1.0)
        gl.glBegin(win.GL_QUADS)
        for u, v, x, y in ((0, 0, -half_w, half_h), (1, 0, half_w, half_h), (1, 1, half_w, -half_h), (0, 1, -half_w, -half_h)):
            gl.glTexCoord2f(float(u), float(v))
            gl.glVertex3f(x, y, 0.0)
        gl.glEnd()
        gl.glDisable(win.GL_TEXTURE_2D)
        cx, cy = cursor[0] - s.display.x, cursor[1] - s.display.y
        if 0 <= cx < s.display.w and 0 <= cy < s.display.h:
            px = 2 * half_w / s.display.w  # metres a source pixel
            k = 1.5 * px
            ox, oy = -half_w + cx * px, half_h - cy * px
            pts = [(ox + ax * k, oy - ay * k) for ax, ay in ARROW]
            gl.glColor4f(1.0, 1.0, 1.0, 1.0)
            gl.glBegin(win.GL_TRIANGLES)
            for tri in ARROW_FILL:
                for i in tri:
                    gl.glVertex3f(pts[i][0], pts[i][1], 0.001)
            gl.glEnd()
            gl.glColor4f(0.0, 0.0, 0.0, 1.0)
            gl.glLineWidth(1.5)
            gl.glBegin(win.GL_LINE_LOOP)
            for x, y in pts:
                gl.glVertex3f(x, y, 0.0015)
            gl.glEnd()
        gl.glPopMatrix()

    def _edge_fade(self, w, h, sbs):
        """Pocket XR's "Soft" edge: a fade to black at each eye's edges, so panels do not end at a hard line."""
        gl.glViewport(0, 0, w, h)
        gl.glMatrixMode(win.GL_PROJECTION)
        gl.glLoadIdentity()
        gl.glOrtho(0.0, float(w), 0.0, float(h), -1.0, 1.0)
        gl.glMatrixMode(win.GL_MODELVIEW)
        gl.glLoadIdentity()
        gl.glEnable(win.GL_BLEND)
        gl.glBlendFunc(win.GL_SRC_ALPHA, win.GL_ONE_MINUS_SRC_ALPHA)
        eyes = [(0, w // 2, 96, 48), (w // 2, w // 2, 48, 96)] if sbs else [(0, w, 96, 96)]
        gl.glBegin(win.GL_QUADS)
        for x0, ew, left_px, right_px in eyes:
            x1 = x0 + ew
            # (the black edge's two corners, then the clear edge's two corners)
            for a, b, c, d in (((x0, 0), (x0, h), (x0 + left_px, h), (x0 + left_px, 0)),
                               ((x1, 0), (x1, h), (x1 - right_px, h), (x1 - right_px, 0)),
                               ((x0, 0), (x1, 0), (x1, 48), (x0, 48)),
                               ((x0, h), (x1, h), (x1, h - 48), (x0, h - 48))):
                gl.glColor4f(0.0, 0.0, 0.0, 1.0)
                gl.glVertex2f(float(a[0]), float(a[1]))
                gl.glVertex2f(float(b[0]), float(b[1]))
                gl.glColor4f(0.0, 0.0, 0.0, 0.0)
                gl.glVertex2f(float(c[0]), float(c[1]))
                gl.glVertex2f(float(d[0]), float(d[1]))
        gl.glEnd()
        gl.glDisable(win.GL_BLEND)

    # ---- the loop ----
    def run(self):
        self.started = time.monotonic()
        last_stats = self.started
        last_frames = 0
        last_imu = 0
        while not self.quit and not self.window.closed:
            self._keys(self.window.pump())
            now = time.monotonic()
            if self.window.display_changed or now - self.last_display_check > 2.0:
                self.window.display_changed = False
                self._displays()
            if self.shown or self.args.window:
                if not self.args.window:
                    self._pointer_home()
                t0 = time.perf_counter()
                self.frame()
                if not self.window.vsync or time.perf_counter() - t0 < 0.002:
                    time.sleep(0.004)  # a swap that did not wait (window covered): do not spin
            else:
                time.sleep(0.2)
            if now - last_stats >= 5.0:
                dt = now - last_stats
                fps = (self.frames - last_frames) / dt
                line = f"{fps:.0f} fps"
                if self.head:
                    n = self.head.count
                    fwd = fusion.qrot(self.head.view(), (0.0, 0.0, -1.0))
                    yaw = math.degrees(math.atan2(-fwd[0], -fwd[2]))
                    pitch = math.degrees(math.asin(max(-1.0, min(1.0, fwd[1]))))
                    line += (f", head {(n - last_imu) / dt:.0f} reports/s from {self.reader.connected_to or 'nothing'}"
                             f", yaw {yaw:+.1f} pitch {pitch:+.1f}, zero {'learnt' if self.head.bias_ready else 'not yet'}")
                    last_imu = n
                for s in self.sources:
                    line += f", {s.display.gdi[4:]} {(s.capture.frames - s.counted) / dt:.0f}/s"
                    s.counted = s.capture.frames
                self.log(line)
                last_stats, last_frames = now, self.frames
        self.close()

    def close(self):
        if self.settings:
            done = threading.Event()
            self.log("putting the glasses back...")
            self.settings.restore(done.set)
            done.wait(20.0)
            self.settings.close()
        for s in self.sources:
            s.close()
        if self.reader:
            self.reader.stop = True
        self.window.close()
        self.log("closed")


def save_png(path, w, h):
    """The frame just drawn, as a PNG (for checking the picture without glasses)."""
    buf = (ctypes.c_ubyte * (w * h * 3))()
    gl.glPixelStorei(win.GL_PACK_ALIGNMENT, 1)
    gl.glReadBuffer(win.GL_BACK)
    gl.glReadPixels(0, 0, w, h, 0x1907, win.GL_UNSIGNED_BYTE, buf)  # GL_RGB
    raw = bytes(buf)
    rows = b"".join(b"\x00" + raw[y * w * 3:(y + 1) * w * 3] for y in range(h - 1, -1, -1))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def main():
    ap = argparse.ArgumentParser(prog="pocketxr", description="Pocket XR for Windows (prototype)")
    ap.add_argument("--window", action="store_true", help="an ordinary window on the PC instead of the glasses (for tests)")
    ap.add_argument("--sbs", action="store_true", help="with --window: draw both eyes side by side")
    ap.add_argument("--display", type=int, help="use display N as the glasses (see python -m pocketxr.displays)")
    ap.add_argument("--fake", action="store_true", help="a made-up slow head turn instead of the glasses' sensors")
    ap.add_argument("--no-tracking", action="store_true", help="no head tracking: the screens are drawn straight ahead")
    ap.add_argument("--settings", action="store_true",
                    help="let Pocket XR set the glasses up (Spatial Anchor and Ultrawide off, Follow, 3D) and put them back")
    ap.add_argument("--screens", type=int, default=MAX_SCREENS, help="how many PC screens to show (1 to 3)")
    ap.add_argument("--size", type=float, default=40.0, help="a screen's width in degrees")
    ap.add_argument("--distance", type=float, default=1.4, help="metres to the screens (seen in 3D)")
    ap.add_argument("--shot", help="save one frame to this PNG and quit")
    ap.add_argument("--shot-after", type=float, default=4.0)
    args = ap.parse_args()

    displays.dpi_aware()
    sys.setswitchinterval(0.001)  # the drawing thread must not wait long behind the sensor thread
    log = Log(os.path.join(os.environ.get("LOCALAPPDATA", "."), "Pocket XR", "pocket-xr.log"))
    app = None
    try:
        app = App(args, log)
        app.run()
    except KeyboardInterrupt:
        if app:
            app.close()


if __name__ == "__main__":
    main()
