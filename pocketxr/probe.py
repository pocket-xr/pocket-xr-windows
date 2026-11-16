"""Step 1 of the plan: does Windows see the glasses, their display and their USB network, and does the sensor stream
arrive? Reads only. Nothing is sent to the glasses.

    python -m pocketxr.probe            a report, then 5 s of the stream
    python -m pocketxr.probe --watch    then yaw and pitch, 5 times a second, until Ctrl+C
"""
import math
import subprocess
import sys
import time

from . import displays, fusion, glasses, imu, netlink


def adapters():
    """Network adapters as Windows names them (PowerShell; only for this report)."""
    cmd = ("Get-NetAdapter | ForEach-Object { $a = $_; $ip = (Get-NetIPAddress -InterfaceIndex $a.ifIndex "
           "-AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress + ' (' + $_.PrefixOrigin + ')' }) -join ', '; "
           "'{0} | {1} | {2} | {3}' -f $a.Name, $a.InterfaceDescription, $a.Status, $ip }")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=20)
        return [line for line in out.stdout.splitlines() if line.strip()]
    except (OSError, subprocess.TimeoutExpired):
        return []


def usb_nodes():
    """Every node Windows made for an XREAL or VITURE USB device, with the driver it picked."""
    cmd = ("Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'VID_3318|VID_35CA' } | ForEach-Object { "
           "'{0} | {1} | {2} | {3}' -f $_.Status, $_.Class, $_.FriendlyName, $_.InstanceId }")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=20)
        return [line for line in out.stdout.splitlines() if line.strip()]
    except (OSError, subprocess.TimeoutExpired):
        return []


def main():
    watch = "--watch" in sys.argv
    displays.dpi_aware()
    g = glasses.detect()
    print("1. USB:", f"{g.name} ({g.vendor:04x}:{g.product:04x})" if g else "no glasses Pocket XR knows are plugged in")
    for line in usb_nodes():
        print("     ", line)
    print("2. Displays:")
    for d in displays.displays():
        print("     ", d, "<- the glasses" if d.is_glasses else "")
    print("3. Network adapters:")
    for line in adapters():
        print("     ", line)
    mine = netlink.local_addresses()
    link = [a for a in mine if a.startswith("169.254.")]
    print("   this PC's 169.254 addresses:", ", ".join(link) or "none")
    print("4. Sensor stream (reading only):")
    seen = []
    accel_sum = [0.0, 0.0, 0.0]
    gyro_abs = [0.0, 0.0, 0.0]
    head = fusion.HeadFusion(g.axes if g else "aura")

    def on_report(ts, gyro, accel):
        seen.append(ts)
        for i in range(3):
            accel_sum[i] += accel[i]
            gyro_abs[i] += abs(gyro[i])
        head.report(ts, gyro, accel)

    reader = imu.ImuReader(on_report, log=lambda m: print("     ", m))
    reader.start()
    time.sleep(8.0 if not watch else 3.0)
    n = len(seen)
    if n < 2:
        print("      no reports. The glasses' network link is not up, or the stream is not served.")
        if not watch:
            return 1
    else:
        span = (seen[-1] - seen[0]) / 1e9
        f = reader.framer
        print(f"      {n} reports in {span:.2f} s of the glasses' clock = {n / span:.0f} a second, from {reader.connected_to}")
        print(f"      bytes skipped {f.dropped}, wrong lengths {f.bad_length}")
        print("      mean accel (x, y, z): " + ", ".join(f"{a / n:+.2f}" for a in accel_sum) + "  (gravity along +y when worn level)")
        print("      mean |gyro| deg/s:    " + ", ".join(f"{math.degrees(a / n):.2f}" for a in gyro_abs))
        print(f"      gyro zero learnt: {head.bias_ready}")
    while watch:
        try:
            time.sleep(0.2)
            fwd = fusion.qrot(head.view(), (0.0, 0.0, -1.0))
            yaw = math.degrees(math.atan2(-fwd[0], -fwd[2]))
            pitch = math.degrees(math.asin(max(-1.0, min(1.0, fwd[1]))))
            print(f"      yaw {yaw:+7.1f}  pitch {pitch:+6.1f}  reports {head.count}  zero {'learnt' if head.bias_ready else 'not yet'}")
        except KeyboardInterrupt:
            break
    return 0


if __name__ == "__main__":
    sys.exit(main())
