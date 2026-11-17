# Porting rules: Pocket XR for Mac (Swift) to Pocket XR for Windows (C#)

The shared logic in `src/PocketXR.Core` is the same logic as `Sources/PocketXRCore` in Pocket XR for Mac, file for
file, and `src/PocketXR.Checks` holds the same checks as the Mac's `Checks/`. These rules keep the two readable side
by side, and make names line up when several people port different files.

## Names

- One C# file per Swift file, same file name (`HeadFusion.swift` becomes `HeadFusion.cs`). Namespace `PocketXR.Core`,
  file-scoped. Checks: `Checks/TrackingChecks.swift` becomes `src/PocketXR.Checks/TrackingChecks.cs`, a
  `public static class TrackingChecks` with `public static void Run()`, namespace `PocketXR.Checks`.
- Type names stay exactly as they are (`HeadFusion`, `PanelPlacement`, `SpaceSettings`).
- Every member name is the Swift name with its first letter in upper case, nothing else changed:
  `widthDeg` becomes `WidthDeg`, `arrange(layout:aspects:widthDeg:distance:curve:)` becomes
  `Arrange(layout, aspects, widthDeg, distance, curve)`, `isEmpty`-style computed properties become properties.
  Parameters keep their Swift label (the label, not the inner name, when they differ), in lower camel case, in the
  same order, with the same default values. Unlabelled parameters keep their inner name.
- Enum cases: first letter upper case (`.bodyAnchor` becomes `TrackingMode.BodyAnchor`). A Swift enum used only as a
  namespace (`public enum SpaceLayout { static func ... }`) becomes a `public static class`.
- A Swift enum with a String raw value keeps the raw strings: add `RawValue()` and `FromRawValue(string)` in a static
  `<Enum>Ext` class next to it, and `AllCases` (an array in declaration order) where Swift has `CaseIterable`.
- Swift enums with associated values become a small class hierarchy or a readonly struct with a `Kind`; keep equality.
- Swift tuples with labels become C# tuples with the same labels, first letter upper case:
  `(width: Int, height: Int)` becomes `(int Width, int Height)`.
- Swift `struct` becomes a C# `struct` when it is small and plain data, or a `record`/`class` with value equality
  when it is large or has many defaults. Keep `Equatable` meaning: `==` must compare by value. Remember Swift structs
  copy on assignment: where the Swift code relies on a copy, copy.
- Optionals become nullable (`double?`, `string?`). `throws` becomes exceptions.

## Types

- `SIMD3<Double>` is `Vec3`, `SIMD2<Double>` is `Vec2`, `SIMD2<Float>` `Vec2f`, `SIMD3<Float>` `Vec3f`,
  `SIMD4<Float>` `Vec4f`, `simd_float4x4` is `Mat4` (column-major, multiplies column vectors, same order of
  multiplication as simd). All in `Simd.cs`. `simd_dot`, `simd_cross`, `simd_length`, `simd_normalize`, `simd_mix`
  are `Vec3.Dot`, `Vec3.Cross`, `.Length`, `.Normalized`, `Vec3.Mix`. Add a helper to `Simd.cs` only if one is truly
  missing, and say so in your report.
- `Quat` is in `Quat.cs` (`Rotate`, `Conjugate`, `Normalized`, `AboutY`, `AboutX`, `Yaw`, `Matrix`).
- `plog("...")` is `Log.P("...")`.
- `Data` and `[UInt8]` are `byte[]` (or `ReadOnlySpan<byte>` for parameters that are only read).
- `Date()` used as "now" for timing: keep whatever the Swift code uses as its clock injectable in the same way; where
  it reads the wall clock directly, use `System.Diagnostics.Stopwatch.GetTimestamp()` based seconds for intervals and
  `DateTime.Now` only for dates shown to a person.
- `NSLock` is `lock (gate)`. `Thread` is `System.Threading.Thread`. `@unchecked Sendable` needs nothing.
- `Codable` types use `System.Text.Json` with the **same JSON key names as Swift** (the Swift property names, lower
  camel case), enums as their raw strings, and a missing key takes the default, exactly as the Swift decoder does, so a
  settings file saved on a Mac opens on Windows and the other way round.
- Numbers: Swift `Int` is `int` unless it can pass 2^31 (timestamps in nanoseconds are `long`/`ulong`). `Float` is
  `float`, `Double` is `double`. Keep every constant, threshold and formula exactly; keep the order of floating point
  operations where a check depends on it.
- Strings shown to people stay word for word, except that "Mac" becomes "PC" where the sentence is about the computer
  (British spelling, plain words). List every sentence you changed in your report.

## Comments

Keep every doc comment, as `/// <summary>` or `//`, reworded only where it names a Mac-only thing. Comments record what
was observed; do not add claims.

## Checks

`Check.That(condition, "what")` and `Check.Near(a, b, tolerance)` (see `Check.cs`). Port every check, in the same
order, with the same message. Do not weaken, skip or delete a check to make it pass: a failing check means the port is
wrong (or, rarely, the check relies on a Swift-only behaviour: then say exactly which and why in your report).

## Never

- No vendor code, no decompiled code, nothing copied from anywhere but the Pocket XR for Mac checkout.
- Do not change files under `pocket-xr-mac` or `pocket-xr-tutorial` (read only). Do not run git commands that change
  anything (no add, commit, stash, checkout, reset). Do not touch files another porter owns.
