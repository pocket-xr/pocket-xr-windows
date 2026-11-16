"""Reaching the glasses over their USB network on Windows.

A PC often has several adapters with 169.254 addresses (unplugged Ethernet, Wi-Fi with no lease), so the routing
table cannot be trusted to pick the glasses' link. A socket bound to the local address on that link goes out there.
"""
import ctypes
import socket
import struct


def local_addresses():
    """The PC's own IPv4 addresses, as strings (iphlpapi GetIpAddrTable)."""
    iphlp = ctypes.WinDLL("iphlpapi")
    size = ctypes.c_ulong(0)
    iphlp.GetIpAddrTable(None, ctypes.byref(size), 0)
    buf = ctypes.create_string_buffer(size.value or 4)
    if iphlp.GetIpAddrTable(buf, ctypes.byref(size), 0) != 0:
        return []
    n = struct.unpack_from("<I", buf, 0)[0]
    out = []
    for i in range(n):
        addr = buf.raw[4 + i * 24:8 + i * 24]  # MIB_IPADDRROW is 24 bytes, the address first, in network order
        out.append(socket.inet_ntoa(addr))
    return out


def link_addresses(host):
    """Local addresses to try for host, best first: same /24, then any other 169.254 address."""
    net = host.rsplit(".", 1)[0] + "."
    mine = [a for a in local_addresses() if a.startswith("169.254.")]
    return [a for a in mine if a.startswith(net)] + [a for a in mine if not a.startswith(net)]


def connect(host, port, timeout=3.0):
    """A TCP connection to the glasses, bound to their link when one is found; a plain connect last."""
    last = None
    for src in link_addresses(host) + [None]:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        # A wrong link answers nothing, so each try is short; the plain connect gets the full time.
        s.settimeout(timeout if src is None else min(timeout, 1.0))
        try:
            if src:
                s.bind((src, 0))
            s.connect((host, port))
            s.settimeout(timeout)
            return s
        except OSError as e:
            last = e
            s.close()
    raise last or OSError("no route to the glasses")
