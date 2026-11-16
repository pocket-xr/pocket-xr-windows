"""A PC screen's picture, through DXGI Desktop Duplication (in Windows since 8, no prompt, no border).

One thread per screen. Each new picture is copied to a staging texture, read back, and kept as BGRA bytes for the
drawing thread to upload. The pointer is not in these pictures; the app draws its own.
"""
import ctypes
import threading
import time
import uuid
from ctypes import POINTER, byref, c_int, c_long, c_uint, c_void_p
from ctypes import wintypes as wt

HRESULT = c_long
WAIT_TIMEOUT = 0x887A0027
ACCESS_LOST = 0x887A0026
D3D11_SDK_VERSION = 7
USAGE_STAGING = 3
CPU_ACCESS_READ = 0x20000
MAP_READ = 1


class GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_uint32), ("d2", ctypes.c_uint16), ("d3", ctypes.c_uint16), ("d4", ctypes.c_ubyte * 8)]


def guid(s):
    return GUID.from_buffer_copy(uuid.UUID(s).bytes_le)


IID_FACTORY1 = guid("770aae78-f26f-4dba-a829-253c83d1b387")
IID_OUTPUT1 = guid("00cddea8-939b-4b83-a340-a685226666cc")
IID_TEXTURE2D = guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")


class OutputDesc(ctypes.Structure):
    _fields_ = [("name", ctypes.c_wchar * 32), ("rect", wt.RECT), ("attached", wt.BOOL), ("rotation", c_uint),
                ("monitor", c_void_p)]


class FrameInfo(ctypes.Structure):
    _fields_ = [("present_time", ctypes.c_int64), ("mouse_time", ctypes.c_int64), ("frames", c_uint),
                ("coalesced", wt.BOOL), ("masked", wt.BOOL), ("px", c_long), ("py", c_long), ("visible", wt.BOOL),
                ("metadata", c_uint), ("shape", c_uint)]


class TextureDesc(ctypes.Structure):
    _fields_ = [("w", c_uint), ("h", c_uint), ("mips", c_uint), ("array", c_uint), ("format", c_uint),
                ("samples", c_uint), ("quality", c_uint), ("usage", c_uint), ("bind", c_uint), ("cpu", c_uint),
                ("misc", c_uint)]


class Mapped(ctypes.Structure):
    _fields_ = [("data", c_void_p), ("row_pitch", c_uint), ("depth_pitch", c_uint)]


def method(obj, index, restype, *argtypes):
    """The COM method at [index] of obj's vtable, bound to obj."""
    vtable = ctypes.cast(obj, POINTER(c_void_p))[0]
    address = ctypes.cast(vtable, POINTER(c_void_p))[index]
    fn = ctypes.WINFUNCTYPE(restype, c_void_p, *argtypes)(address)
    return lambda *a: fn(obj, *a)


def release(obj):
    if obj:
        method(obj, 2, ctypes.c_ulong)()


def check(hr, what):
    if hr < 0:
        raise OSError(f"{what} failed (0x{hr & 0xFFFFFFFF:08X})")


def find_output(gdi_name):
    """(adapter, output) for a display like \\\\.\\DISPLAY1; the caller releases both."""
    dxgi = ctypes.WinDLL("dxgi")
    factory = c_void_p()
    check(dxgi.CreateDXGIFactory1(byref(IID_FACTORY1), byref(factory)), "CreateDXGIFactory1")
    try:
        a = 0
        while True:
            adapter = c_void_p()
            if method(factory, 12, HRESULT, c_uint, POINTER(c_void_p))(a, byref(adapter)) < 0:  # EnumAdapters1
                return None, None
            o = 0
            while True:
                output = c_void_p()
                if method(adapter, 7, HRESULT, c_uint, POINTER(c_void_p))(o, byref(output)) < 0:  # EnumOutputs
                    break
                desc = OutputDesc()
                method(output, 7, HRESULT, POINTER(OutputDesc))(byref(desc))  # GetDesc
                if desc.name == gdi_name:
                    return adapter, output
                release(output)
                o += 1
            release(adapter)
            a += 1
    finally:
        release(factory)


class ScreenCapture(threading.Thread):
    """Keeps the newest picture of one screen. take(version) gives (version, w, h, buffer) when there is a newer one."""

    def __init__(self, gdi_name, log=print, max_fps=60):
        super().__init__(daemon=True)
        self.gdi = gdi_name
        self.log = log
        self.interval = 1.0 / max_fps
        self.lock = threading.Lock()
        self.buf = None
        self.w = self.h = 0
        self.version = 0
        self.frames = 0
        self.stop = False
        self.error = None

    def run(self):
        while not self.stop:
            try:
                self._session()
            except OSError as e:
                if str(e) != self.error:
                    self.error = str(e)
                    self.log(f"capture {self.gdi}: {e}")
            time.sleep(1.0)  # the screen was lost (a mode change, the lock screen): open it again

    def _session(self):
        adapter, output = find_output(self.gdi)
        if not output:
            raise OSError("that display is not there")
        device, context, output1, dupl, staging = c_void_p(), c_void_p(), c_void_p(), c_void_p(), c_void_p()
        try:
            d3d11 = ctypes.WinDLL("d3d11")
            level = c_uint()
            check(d3d11.D3D11CreateDevice(adapter, 0, None, 0, None, 0, D3D11_SDK_VERSION, byref(device), byref(level),
                                          byref(context)), "D3D11CreateDevice")
            check(method(output, 0, HRESULT, POINTER(GUID), POINTER(c_void_p))(byref(IID_OUTPUT1), byref(output1)),
                  "IDXGIOutput1")
            check(method(output1, 22, HRESULT, c_void_p, POINTER(c_void_p))(device, byref(dupl)), "DuplicateOutput")
            acquire = method(dupl, 8, HRESULT, c_uint, POINTER(FrameInfo), POINTER(c_void_p))
            release_frame = method(dupl, 14, HRESULT)
            create_texture = method(device, 5, HRESULT, POINTER(TextureDesc), c_void_p, POINTER(c_void_p))
            copy_resource = method(context, 47, None, c_void_p, c_void_p)
            map_ = method(context, 14, HRESULT, c_void_p, c_uint, c_uint, c_uint, POINTER(Mapped))
            unmap = method(context, 15, None, c_void_p, c_uint)
            size = (0, 0)
            self.error = None
            self.log(f"capture {self.gdi}: open")
            while not self.stop:
                started = time.perf_counter()
                info = FrameInfo()
                resource = c_void_p()
                hr = acquire(100, byref(info), byref(resource)) & 0xFFFFFFFF
                if hr == WAIT_TIMEOUT:
                    continue
                if hr == ACCESS_LOST:
                    raise OSError("the screen changed")
                check(c_long(hr).value, "AcquireNextFrame")
                try:
                    if info.present_time == 0:
                        continue  # only the pointer moved
                    texture = c_void_p()
                    check(method(resource, 0, HRESULT, POINTER(GUID), POINTER(c_void_p))(
                        byref(IID_TEXTURE2D), byref(texture)), "ID3D11Texture2D")
                    try:
                        desc = TextureDesc()
                        method(texture, 10, None, POINTER(TextureDesc))(byref(desc))
                        if (desc.w, desc.h) != size:
                            release(staging)
                            staging = c_void_p()
                            desc.usage, desc.bind, desc.cpu, desc.misc = USAGE_STAGING, 0, CPU_ACCESS_READ, 0
                            desc.mips, desc.array, desc.samples, desc.quality = 1, 1, 1, 0
                            check(create_texture(byref(desc), None, byref(staging)), "CreateTexture2D")
                            size = (desc.w, desc.h)
                        copy_resource(staging, texture)
                    finally:
                        release(texture)
                    m = Mapped()
                    check(map_(staging, 0, MAP_READ, 0, byref(m)), "Map")
                    try:
                        self._keep(m, size)
                    finally:
                        unmap(staging, 0)
                finally:
                    release_frame()
                    release(resource)
                wait = self.interval - (time.perf_counter() - started)
                if wait > 0:
                    time.sleep(wait)
        finally:
            for obj in (staging, dupl, output1, context, device, output, adapter):
                release(obj)

    def _keep(self, m, size):
        w, h = size
        with self.lock:
            if self.buf is None or (self.w, self.h) != size:
                self.buf = (ctypes.c_ubyte * (w * h * 4))()
                self.w, self.h = w, h
            if m.row_pitch == w * 4:
                ctypes.memmove(self.buf, m.data, w * h * 4)
            else:
                dst = ctypes.addressof(self.buf)
                for y in range(h):
                    ctypes.memmove(dst + y * w * 4, m.data + y * m.row_pitch, w * 4)
            self.version += 1
            self.frames += 1

    def take(self, have, use):
        """If there is a picture newer than version [have], call use(w, h, buffer) with it held still. Returns the version."""
        with self.lock:
            if self.buf is not None and self.version != have:
                use(self.w, self.h, self.buf)
            return self.version
