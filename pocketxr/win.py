"""A borderless OpenGL window, global hotkeys and the pointer, through ctypes. Nothing is installed: user32, gdi32 and
opengl32 come with Windows. The drawing is fixed-function OpenGL, as on the Deck.
"""
import ctypes
from ctypes import wintypes as wt

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
gl = ctypes.WinDLL("opengl32")

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)

WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000
WS_OVERLAPPEDWINDOW = 0x00CF0000
WS_EX_TOPMOST = 0x8
WS_EX_TOOLWINDOW = 0x80
WS_EX_NOACTIVATE = 0x08000000
WM_DESTROY = 0x0002
WM_CLOSE = 0x0010
WM_SETCURSOR = 0x0020
WM_DISPLAYCHANGE = 0x007E
WM_HOTKEY = 0x0312
PM_REMOVE = 1
SW_SHOWNOACTIVATE = 4
SW_HIDE = 0
SWP_NOACTIVATE = 0x10
SWP_SHOWWINDOW = 0x40
HWND_TOPMOST = -1
MOD_ALT, MOD_CONTROL, MOD_NOREPEAT = 0x1, 0x2, 0x4000
PFD_DRAW_TO_WINDOW, PFD_SUPPORT_OPENGL, PFD_DOUBLEBUFFER = 0x4, 0x20, 0x1

VK = {"R": 0x52, "Q": 0x51, "D": 0x44, "UP": 0x26, "DOWN": 0x28, "LEFT": 0x25, "RIGHT": 0x27, "PRIOR": 0x21,
      "NEXT": 0x22, "1": 0x31, "2": 0x32, "3": 0x33}


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("style", wt.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE), ("hIcon", wt.HANDLE),
                ("hCursor", wt.HANDLE), ("hbrBackground", wt.HANDLE), ("lpszMenuName", wt.LPCWSTR),
                ("lpszClassName", wt.LPCWSTR), ("hIconSm", wt.HANDLE)]


class PIXELFORMATDESCRIPTOR(ctypes.Structure):
    _fields_ = [("nSize", wt.WORD), ("nVersion", wt.WORD), ("dwFlags", wt.DWORD), ("iPixelType", ctypes.c_ubyte),
                ("cColorBits", ctypes.c_ubyte), ("rest1", ctypes.c_ubyte * 13), ("cDepthBits", ctypes.c_ubyte),
                ("cStencilBits", ctypes.c_ubyte), ("cAuxBuffers", ctypes.c_ubyte), ("iLayerType", ctypes.c_ubyte),
                ("bReserved", ctypes.c_ubyte), ("dwLayerMask", wt.DWORD), ("dwVisibleMask", wt.DWORD),
                ("dwDamageMask", wt.DWORD)]


user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.CreateWindowExW.restype = wt.HWND
user32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]
user32.GetDC.restype = wt.HDC
user32.GetDC.argtypes = [wt.HWND]
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
user32.DestroyWindow.argtypes = [wt.HWND]
user32.RegisterHotKey.argtypes = [wt.HWND, ctypes.c_int, wt.UINT, wt.UINT]
user32.PeekMessageW.argtypes = [ctypes.POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT, wt.UINT]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.SetCursor.argtypes = [wt.HANDLE]
kernel32.GetModuleHandleW.restype = wt.HMODULE
kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
gdi32.ChoosePixelFormat.argtypes = [wt.HDC, ctypes.POINTER(PIXELFORMATDESCRIPTOR)]
gdi32.SetPixelFormat.argtypes = [wt.HDC, ctypes.c_int, ctypes.POINTER(PIXELFORMATDESCRIPTOR)]
gdi32.SwapBuffers.argtypes = [wt.HDC]
gl.wglCreateContext.restype = wt.HANDLE
gl.wglCreateContext.argtypes = [wt.HDC]
gl.wglMakeCurrent.argtypes = [wt.HDC, wt.HANDLE]
gl.wglDeleteContext.argtypes = [wt.HANDLE]
gl.wglGetProcAddress.restype = ctypes.c_void_p
gl.wglGetProcAddress.argtypes = [ctypes.c_char_p]

GL_COLOR_BUFFER_BIT = 0x4000
GL_PROJECTION = 0x1701
GL_MODELVIEW = 0x1700
GL_TEXTURE_2D = 0x0DE1
GL_RGBA = 0x1908
GL_BGRA = 0x80E1
GL_UNSIGNED_BYTE = 0x1401
GL_LINEAR = 0x2601
GL_TEXTURE_MIN_FILTER = 0x2801
GL_TEXTURE_MAG_FILTER = 0x2800
GL_TEXTURE_WRAP_S = 0x2802
GL_TEXTURE_WRAP_T = 0x2803
GL_CLAMP_TO_EDGE = 0x812F
GL_QUADS = 0x0007
GL_LINE_LOOP = 0x0002
GL_TRIANGLES = 0x0004
GL_POLYGON = 0x0009
GL_BLEND = 0x0BE2
GL_SRC_ALPHA = 0x0302
GL_ONE_MINUS_SRC_ALPHA = 0x0303
GL_UNPACK_ALIGNMENT = 0x0CF5
GL_PACK_ALIGNMENT = 0x0D05
GL_FRONT = 0x0404
GL_BACK = 0x0405

for _n, _k in (("glClearColor", 4), ("glTexCoord2f", 2), ("glVertex3f", 3), ("glVertex2f", 2), ("glColor3f", 3),
               ("glColor4f", 4), ("glTranslatef", 3), ("glRotatef", 4), ("glLineWidth", 1)):
    getattr(gl, _n).argtypes = [ctypes.c_float] * _k
    getattr(gl, _n).restype = None
gl.glFrustum.argtypes = [ctypes.c_double] * 6
gl.glOrtho.argtypes = [ctypes.c_double] * 6
gl.glTexImage2D.argtypes = [ctypes.c_uint, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                            ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p]
gl.glTexSubImage2D.argtypes = [ctypes.c_uint, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                               ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p]
gl.glReadPixels.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint,
                            ctypes.c_void_p]
gl.glMultMatrixf.argtypes = [ctypes.POINTER(ctypes.c_float)]
gl.glGenTextures.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_uint)]
gl.glDeleteTextures.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_uint)]
gl.glGetString.restype = ctypes.c_char_p
gl.glGetString.argtypes = [ctypes.c_uint]


class Window:
    """One OpenGL window: borderless and topmost over a display, or an ordinary window for tests."""

    def __init__(self, title, rect, fullscreen=True, visible=True):
        self.closed = False
        self.display_changed = False
        self.hotkeys = []  # ids pressed since the last pump()
        self._proc = WNDPROC(self._wndproc)
        hinst = kernel32.GetModuleHandleW(None)
        cls = WNDCLASSEXW()
        cls.cbSize = ctypes.sizeof(WNDCLASSEXW)
        cls.style = 0x20  # CS_OWNDC
        cls.lpfnWndProc = self._proc
        cls.hInstance = hinst
        cls.lpszClassName = "PocketXRView"
        user32.RegisterClassExW(ctypes.byref(cls))
        x, y, w, h = rect
        self.fullscreen = fullscreen
        if fullscreen:
            style, ex = WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        else:
            style, ex = WS_OVERLAPPEDWINDOW, 0
        self.hwnd = user32.CreateWindowExW(ex, "PocketXRView", title, style, x, y, w, h, None, None, hinst, None)
        if not self.hwnd:
            raise OSError(f"CreateWindowEx failed ({ctypes.get_last_error()})")
        self.hdc = user32.GetDC(self.hwnd)
        pfd = PIXELFORMATDESCRIPTOR()
        pfd.nSize = ctypes.sizeof(PIXELFORMATDESCRIPTOR)
        pfd.nVersion = 1
        pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER
        pfd.cColorBits = 32
        fmt = gdi32.ChoosePixelFormat(self.hdc, ctypes.byref(pfd))
        if not fmt or not gdi32.SetPixelFormat(self.hdc, fmt, ctypes.byref(pfd)):
            raise OSError("no OpenGL pixel format")
        self.ctx = gl.wglCreateContext(self.hdc)
        if not self.ctx or not gl.wglMakeCurrent(self.hdc, self.ctx):
            raise OSError("no OpenGL context")
        addr = gl.wglGetProcAddress(b"wglSwapIntervalEXT")
        self.vsync = bool(addr) and bool(ctypes.WINFUNCTYPE(wt.BOOL, ctypes.c_int)(addr)(1))
        if visible:
            user32.ShowWindow(self.hwnd, SW_SHOWNOACTIVATE)

    def _wndproc(self, hwnd, msg, wparam, lparam):
        if msg == WM_CLOSE:
            self.closed = True
            return 0
        if msg == WM_DISPLAYCHANGE:
            self.display_changed = True
        if msg == WM_SETCURSOR and self.fullscreen:
            user32.SetCursor(None)
            return 1
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def hotkey(self, key_id, vk, mods=MOD_CONTROL | MOD_ALT):
        return bool(user32.RegisterHotKey(self.hwnd, key_id, mods | MOD_NOREPEAT, vk))

    def pump(self):
        """Handles waiting messages; returns the hotkey ids pressed."""
        msg = wt.MSG()
        pressed = []
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            if msg.message == WM_HOTKEY:
                pressed.append(int(msg.wParam))
                continue
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        return pressed

    def size(self):
        r = wt.RECT()
        user32.GetClientRect(self.hwnd, ctypes.byref(r))
        return r.right - r.left, r.bottom - r.top

    def move(self, rect):
        x, y, w, h = rect
        user32.SetWindowPos(self.hwnd, HWND_TOPMOST if self.fullscreen else None, x, y, w, h,
                            SWP_NOACTIVATE | SWP_SHOWWINDOW)

    def show(self, on):
        user32.ShowWindow(self.hwnd, SW_SHOWNOACTIVATE if on else SW_HIDE)

    def swap(self):
        gdi32.SwapBuffers(self.hdc)

    def close(self):
        gl.wglMakeCurrent(None, None)
        if self.ctx:
            gl.wglDeleteContext(self.ctx)
        user32.DestroyWindow(self.hwnd)


def cursor_pos():
    p = wt.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def set_cursor_pos(x, y):
    user32.SetCursorPos(int(x), int(y))
