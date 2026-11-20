# Builds moonlight-pxr.dll (x64) from moonlight-common-c and Pocket XR's glue, with Zig's C compiler.
#   .\native\moonlight\build.ps1 [-Zig path\to\zig.exe] [-Out folder]
# Zig is looked for in %LOCALAPPDATA%\PocketXR-tools\zig, then on PATH.
param(
    [string]$Zig = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$mc = Join-Path $here "moonlight-common-c"
if (-not $Out) { $Out = Join-Path $here "bin" }
if (-not $Zig) {
    $local = Get-ChildItem (Join-Path $env:LOCALAPPDATA "PocketXR-tools\zig") -Recurse -Filter zig.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    $Zig = if ($local) { $local.FullName } else { (Get-Command zig -ErrorAction Stop).Source }
}
New-Item -ItemType Directory -Force $Out | Out-Null

$sources = @()
$sources += Get-ChildItem (Join-Path $mc "src") -Filter *.c | Where-Object { $_.Name -ne "PlatformCrypto.c" } | ForEach-Object { $_.FullName }
$sources += "callbacks","compress","host","list","packet","peer","protocol","win32" | ForEach-Object { Join-Path $mc "enet\$_.c" }
$sources += (Join-Path $mc "nanors\rs.c"), (Join-Path $mc "nanors\deps\obl\oblas_common.c"), (Join-Path $mc "nanors\deps\obl\oblas_lite.c")
$sources += (Join-Path $here "pxr_moonlight.c"), (Join-Path $here "pxr_crypto_bcrypt.c"), (Join-Path $here "pxr_host.c")

$flags = @(
    "cc", "-target", "x86_64-windows-gnu", "-shared", "-O2", "-std=gnu11",
    "-DNDEBUG", "-DHAS_SOCKLEN_T", "-DHAS_GETADDRINFO=1", "-DHAS_GETNAMEINFO=1", "-DHAS_INET_PTON=1", "-DHAS_INET_NTOP=1",
    "-DHAS_QOS_FLOWID=1", "-DHAS_PQOS_FLOWID=1",
    "-I", (Join-Path $mc "src"), "-I", (Join-Path $mc "enet\include"),
    "-I", (Join-Path $mc "nanors"), "-I", (Join-Path $mc "nanors\deps"), "-I", (Join-Path $mc "nanors\deps\obl"),
    "-Wno-unused-parameter", "-Wno-deprecated-declarations",
    "-o", (Join-Path $Out "moonlight-pxr.dll")
) + $sources + @("-lws2_32", "-lwinmm", "-lbcrypt", "-lqwave", "-Wl,--subsystem,windows")

& $Zig @flags
if ($LASTEXITCODE -ne 0) { throw "zig cc failed ($LASTEXITCODE)" }
Remove-Item (Join-Path $Out "moonlight-pxr.lib"), (Join-Path $Out "moonlight-pxr.pdb") -ErrorAction SilentlyContinue
Get-Item (Join-Path $Out "moonlight-pxr.dll") | Select-Object Name, Length
