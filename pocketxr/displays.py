"""The PC's displays as Windows sees them: the name from the monitor itself, where it sits on the desktop, its size and
its refresh rate. The glasses are found by name ("XREAL 1 Ultra" for the Aura), as on the Deck.
"""
import ctypes
from ctypes import wintypes as wt
from dataclasses import dataclass

user32 = ctypes.WinDLL("user32", use_last_error=True)

QDC_ONLY_ACTIVE_PATHS = 2
GET_SOURCE_NAME = 1
GET_TARGET_NAME = 2
ENUM_CURRENT_SETTINGS = 0xFFFFFFFF

GLASSES_NAMES = ("xreal", "nreal", "viture", "rokid", "rayneo")


class LUID(ctypes.Structure):
    _fields_ = [("low", wt.DWORD), ("high", wt.LONG)]


class PathSource(ctypes.Structure):
    _fields_ = [("adapter", LUID), ("id", ctypes.c_uint32), ("mode", ctypes.c_uint32), ("status", ctypes.c_uint32)]


class PathTarget(ctypes.Structure):
    _fields_ = [("adapter", LUID), ("id", ctypes.c_uint32), ("mode", ctypes.c_uint32), ("tech", ctypes.c_uint32),
                ("rotation", ctypes.c_uint32), ("scaling", ctypes.c_uint32), ("hz_num", ctypes.c_uint32),
                ("hz_den", ctypes.c_uint32), ("scan", ctypes.c_uint32), ("available", wt.BOOL),
                ("status", ctypes.c_uint32)]


class PathInfo(ctypes.Structure):
    _fields_ = [("source", PathSource), ("target", PathTarget), ("flags", ctypes.c_uint32)]


class ModeInfo(ctypes.Structure):  # only its size matters here
    _fields_ = [("type", ctypes.c_uint32), ("id", ctypes.c_uint32), ("adapter", LUID), ("body", ctypes.c_uint64 * 6)]


class InfoHeader(ctypes.Structure):
    _fields_ = [("type", ctypes.c_uint32), ("size", ctypes.c_uint32), ("adapter", LUID), ("id", ctypes.c_uint32)]


class SourceName(ctypes.Structure):
    _fields_ = [("header", InfoHeader), ("gdi", ctypes.c_wchar * 32)]


class TargetName(ctypes.Structure):
    _fields_ = [("header", InfoHeader), ("flags", ctypes.c_uint32), ("tech", ctypes.c_uint32),
                ("maker", ctypes.c_uint16), ("product", ctypes.c_uint16), ("connector", ctypes.c_uint32),
                ("name", ctypes.c_wchar * 64), ("path", ctypes.c_wchar * 128)]


class DevMode(ctypes.Structure):
    _fields_ = [("dmDeviceName", ctypes.c_wchar * 32), ("dmSpecVersion", wt.WORD), ("dmDriverVersion", wt.WORD),
                ("dmSize", wt.WORD), ("dmDriverExtra", wt.WORD), ("dmFields", wt.DWORD),
                ("x", wt.LONG), ("y", wt.LONG), ("dmDisplayOrientation", wt.DWORD), ("dmDisplayFixedOutput", wt.DWORD),
                ("dmColor", ctypes.c_short), ("dmDuplex", ctypes.c_short), ("dmYResolution", ctypes.c_short),
                ("dmTTOption", ctypes.c_short), ("dmCollate", ctypes.c_short), ("dmFormName", ctypes.c_wchar * 32),
                ("dmLogPixels", wt.WORD), ("dmBitsPerPel", wt.DWORD), ("w", wt.DWORD), ("h", wt.DWORD),
                ("dmDisplayFlags", wt.DWORD), ("hz", wt.DWORD), ("rest", wt.DWORD * 8)]


@dataclass(frozen=True)
class Display:
    gdi: str  # \\.\DISPLAY3: what capture and window placement use
    name: str  # the monitor's own name, "XREAL 1 Ultra"
    x: int
    y: int
    w: int
    h: int
    hz: int
    primary: bool

    @property
    def is_glasses(self):
        n = self.name.lower()
        return any(k in n for k in GLASSES_NAMES)

    def __str__(self):
        return f'"{self.name}" {self.w}x{self.h} at {self.hz} Hz, at {self.x},{self.y} ({self.gdi})'


def dpi_aware():
    """Real pixels for every monitor, whatever its scaling. Before any window or size is read."""
    try:
        user32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per monitor, version 2
    except (AttributeError, OSError):
        pass


def displays():
    """Every display that is on, in Windows' own order."""
    np, nm = ctypes.c_uint32(0), ctypes.c_uint32(0)
    if user32.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ctypes.byref(np), ctypes.byref(nm)) != 0:
        return []
    paths = (PathInfo * np.value)()
    modes = (ModeInfo * nm.value)()
    if user32.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ctypes.byref(np), paths, ctypes.byref(nm), modes, None) != 0:
        return []
    out = []
    seen = set()
    for p in paths[:np.value]:
        src = SourceName()
        src.header = InfoHeader(GET_SOURCE_NAME, ctypes.sizeof(SourceName), p.source.adapter, p.source.id)
        if user32.DisplayConfigGetDeviceInfo(ctypes.byref(src)) != 0 or src.gdi in seen:
            continue
        seen.add(src.gdi)  # a cloned screen shows once
        tgt = TargetName()
        tgt.header = InfoHeader(GET_TARGET_NAME, ctypes.sizeof(TargetName), p.target.adapter, p.target.id)
        name = tgt.name if user32.DisplayConfigGetDeviceInfo(ctypes.byref(tgt)) == 0 else ""
        dm = DevMode()
        dm.dmSize = ctypes.sizeof(DevMode)
        if not user32.EnumDisplaySettingsExW(src.gdi, ENUM_CURRENT_SETTINGS, ctypes.byref(dm), 0):
            continue
        out.append(Display(src.gdi, name or "Display", dm.x, dm.y, dm.w, dm.h, dm.hz, dm.x == 0 and dm.y == 0))
    return out


def split(found, glasses_index=None):
    """(the glasses' display or None, the other displays with the main one first)."""
    glasses = None
    if glasses_index is not None:
        glasses = found[glasses_index] if 0 <= glasses_index < len(found) else None
    else:
        glasses = next((d for d in found if d.is_glasses), None)
    others = sorted((d for d in found if d is not glasses), key=lambda d: (not d.primary, d.x, d.y))
    return glasses, others


if __name__ == "__main__":
    dpi_aware()
    for i, d in enumerate(displays()):
        print(i, d, "(glasses)" if d.is_glasses else "(main)" if d.primary else "")
