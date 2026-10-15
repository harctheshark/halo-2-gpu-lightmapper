# Changelog

## 0.6.1 (2026-10-15): pre-release

First public release. Changes since the internal 0.6.0 build:

- **Custom quality is shown in the window, labelled Experimental.** Pick your own photon count (1,000,000-100,000,000, default
  50,000,000) and gather rays (1-64, default 16). A note explains that only Super's own values (20,000,000 photons, 8 rays) are
  validated and that large values mean very long bakes. The ETA turns amber above one hour and red at eight hours or more.
  Custom runs a guarded copy of the GPU host that refuses a photon count its 32-bit arithmetic cannot hold. No development
  switch is needed any more (`--dev-custom` is still accepted and does nothing).
- **No personal build paths in the exe.** The compiler path map keeps the build machine's folders out of the exe, and the
  Python bundle records the user site-packages path generically. The build is still reproducible byte for byte.
- **Documentation** corrected: known issue 5 (stock-cyclotron per-vertex records) says what was measured and that the cause is
  unknown; the limits, VRAM and light-probe statements agree everywhere.
- **Checked on this exe:** the production-capture identity run on cyclotron (equal to the validated bake), Custom at Super's
  values (equal to Super), light probes on triplicate (99.533% against a 99.53% target), cancel during the GPU bake (exit 4,
  nothing left running), and Retry after a held lightmap file (installed, then undone).

## 0.6.0 (2026-10-15): internal

- The direct tag writer reads the decorator data of a stock Steam H2EK's original scenarios, so all 73 scenario files of that
  H2EK work (0.5.0 hit 16 scenarios).
- Every install, including Lightmap Only and Standard, goes through the app's own installer with **Retry** when Sapien or
  Guerilla holds a file.
- Ambient occlusion shoots 1,024 rays per texel and the seam filter never blends texels with a surface stacked underneath
  (no more blotches on ramps). The seam filter runs on numpy alone, so scipy is no longer bundled.
- The launcher patches of the engine were folded into the production engine scripts.
- README: new Limits and Known issues sections.

## 0.5.0 (2026-10-14): first internal build

- One exe (WPF window plus the same-exe `--cli`) that bundles PowerShell 7.6.6, Python 3.10.5 with numpy 2.2.6 and the GPU
  engine.
- Quality Low and Super; modes Lightmap Standard, Lightmap Lightprobes and Lightmap Only; ambient occlusion with strength and
  exponent; capture reuse; backups, Restore and Undo; crash and power-loss recovery.
