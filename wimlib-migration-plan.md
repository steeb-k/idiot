# wimlib Migration Feasibility Report

## Purpose

This document assesses the feasibility of replacing the application’s use of **DISM** (Deployment Image Servicing and Management) for modifying bootable WIM files with **wimlib** and its command-line front-end **wimlib-imagex** ([https://github.com/ebiggers/wimlib](https://github.com/ebiggers/wimlib)). The goal is to determine whether wimlib can cover current DISM-based WIM operations and what gaps or alternative designs would be required.

---

## Current Architecture

The application modifies bootable WIM images (e.g. `boot.wim` from Windows installation media) to inject drivers, then optionally optimizes and exports the result. The flow is:

1. **Mount** – DISM mounts a WIM image index to a directory (read-write).
2. **Inject drivers** – DISM `/Add-Driver` is used to add drivers into the mounted image (offline driver store and registry).
3. **Unmount or capture** – Either unmount with `/Commit` (if editing in place) or capture the mounted directory to a new WIM with `/Capture-Image` (with `/Bootable` for the first boot.wim index).
4. **Append / export / optimize** – DISM `/Export-Image` is used to append images to a WIM or to re-export with maximum compression for optimization.
5. **Cleanup** – DISM `/Get-MountedWimInfo` and `/Unmount-Wim` /Discard plus `/Cleanup-Wim` are used to find and clean orphaned mounts.

DISM operations are invoked by spawning `dism.exe` from:

- **Core/ImageProcessor.cs** – Get-WimInfo, Mount-Wim, Unmount-Wim, Add-Driver, Capture-Image, Export-Image, Get-MountedWimInfo.
- **Core/CleanupService.cs** – Unmount-Wim /Discard and Cleanup-Wim for sweep-up.

---

## wimlib Capability Overview

wimlib is a free, open-source C library for creating, modifying, extracting, and mounting WIM files. It and its CLI **wimlib-imagex** are documented as a cross-platform alternative to Microsoft’s WIMGAPI, ImageX, and DISM.

**On Windows specifically** ([README.WINDOWS.md](https://github.com/ebiggers/wimlib/blob/master/README.WINDOWS.md)):

- **Supported:** The Windows distribution provides `wimlib-imagex.exe` and `libwim-15.dll`. Supported operations include:
  - **info** – Display (and optionally change) WIM/image information, including bootable image.
  - **apply** – Extract a WIM image to a directory (full “apply” of an image).
  - **capture** – Create a new WIM from a directory; supports `--boot` for the bootable Windows PE image.
  - **append** – Append a captured image to an existing WIM.
  - **export** – Export image(s) from one WIM to another.
  - **update** – Add, delete, or rename files inside a WIM image (no mount required).
  - **optimize** – Rebuild and optionally recompress a WIM.

- **Not supported on Windows:** Mounting and unmounting WIM images. Mount is implemented on Linux via FUSE; the Windows build does not support mount/unmount. Therefore any workflow that today relies on “mount → edit → commit” must be replaced with “extract → edit → capture” (or “update” where applicable).

- **Compression:** wimlib supports XPRESS, LZX, and LZMS (including solid/ESD-style). It can produce WIMs compatible with Microsoft tools, with some caveats for non-default chunk sizes or solid format.

---

## Operation-by-Operation Mapping

| DISM operation           | wimlib equivalent | Notes |
|--------------------------|-------------------|--------|
| **Get-WimInfo**          | `wimlib-imagex info WIMFILE` | Output format differs from DISM; application would need to parse wimlib’s text output (index, name, etc.) instead of DISM’s “Index :” / “Name :” lines. |
| **Mount-Wim**            | *None on Windows* | No mount on Windows. Use **apply** to extract the image to a directory instead. |
| **Unmount-Wim** /Commit  | *N/A*             | With extract-based flow, “commit” is achieved by capturing the modified directory to a (new or existing) WIM. |
| **Unmount-Wim** /Discard | *N/A*             | With extract-based flow, “discard” is achieved by deleting the extraction directory. |
| **Add-Driver**           | *No equivalent*  | wimlib has no command that performs offline driver staging (driver store + registry). See Gap Analysis. |
| **Capture-Image**        | `wimlib-imagex capture DIR WIMFILE [NAME] [--boot] [--compress=...]` | Direct equivalent. Use `--boot` for the first boot.wim index (Windows PE bootable image). |
| **Export-Image**         | `wimlib-imagex export` or **append** | Exporting an index to another WIM is supported; append is used to add a new image to an existing WIM. |
| **Get-MountedWimInfo**   | *N/A*             | wimlib does not manage mounts on Windows. Application would track extraction directories itself. |
| **Cleanup-Wim**          | *N/A*             | No DISM mounts to clean. Cleanup = delete extraction directories and any temp WIMs. |

---

## Gap Analysis

### 1. Driver injection

**DISM** `/Image:<path> /Add-Driver /Driver:<dir> /Recurse` performs *offline driver staging*: it registers drivers in the offline image’s driver store and updates registry so that Windows (or Windows PE) will load them at boot. This is more than copying files.

**wimlib** has no “add driver” or “inject driver” command. The **update** command can add files or directories into a WIM, but that only adds file content; it does not perform driver store registration or the registry changes required for boot-time driver loading.

**Options:**

- **Hybrid (recommended):** Use wimlib for all WIM handling (info, apply, capture, export, optimize), but keep **DISM only for driver injection** on the *extracted* directory. Flow: extract image with wimlib → run DISM `/Image:<extract_path> /Add-Driver ...` → capture with wimlib (with `--boot` when needed). This removes DISM mount/unmount/capture/export and DISM cleanup from the main flow, while retaining proven driver injection behavior.
- **Full wimlib:** Implement or script offline driver staging against the extracted directory (e.g. using Windows APIs or tools that operate on an offline registry and driver store). This is non-trivial, platform- and PE-specific, and may have compatibility and support risks.

### 2. No mount on Windows

Because wimlib does not support mounting WIM images on Windows, the workflow must change from:

- **Current:** Mount WIM → modify (e.g. add drivers) → unmount/commit or capture.

To:

- **With wimlib:** Extract image to directory (apply) → modify (e.g. add drivers via DISM or custom) → capture directory to WIM (and optionally export/append/optimize).

Implications:

- **Cleanup:** Instead of DISM unmount + `Cleanup-Wim`, cleanup is “delete extraction directories.” No equivalent to `Get-MountedWimInfo` is needed if the app tracks its own extract paths.
- **Disk usage:** Extract uses more disk space than a mount (full copy of image contents). Same as today when capture is used (mount dir is also a full copy).
- **Bootable flag:** wimlib’s **capture** supports `--boot` to mark the image as the bootable Windows PE image; this covers the current use of DISM’s `/Bootable` for the first index of boot.wim.

---

## Alternative Architecture (Extract-Based Flow)

A feasible design using wimlib (with optional hybrid DISM for drivers) is:

1. **List images** – `wimlib-imagex info <wimfile>` (parse output for index and name).
2. **Extract image** – `wimlib-imagex apply <wimfile> <index> <extract_dir>` to get a directory tree.
3. **Driver injection** – Either:
   - **Hybrid:** `dism.exe /Image:<extract_dir> /Add-Driver /Driver:<dir> /Recurse` (one or more times), or
   - **Full wimlib:** Custom/scripted offline driver staging (high effort).
4. **Capture** – `wimlib-imagex capture <extract_dir> <output.wim> "Image Name" [--boot] [--compress=...]` (use `--boot` for first boot.wim index).
5. **Append** (if needed) – `wimlib-imagex append <extract_dir> <existing.wim> "Image Name" [options]`.
6. **Export / optimize** – `wimlib-imagex export` or **optimize** as needed to replace DISM export and optimization.
7. **Cleanup** – Delete extraction directories (and any temp WIMs). No DISM mount list or `Cleanup-Wim`.

Progress reporting and cancellation would need to be adapted to wimlib-imagex’s stdout/stderr (and any progress options it provides) instead of DISM’s progress output.

---

## Licensing and Distribution

- wimlib and wimlib-imagex are **free software**. The main license is **GPL-3**; the library is also available under **LGPL** ([COPYING](https://github.com/ebiggers/wimlib/blob/master/COPYING), [README](https://github.com/ebiggers/wimlib)).
- **Bundling:** Distributing wimlib-imagex.exe and libwim-15.dll (or equivalent) with this application may create obligations under GPL/LGPL (e.g. source availability, license compatibility). This document does not constitute legal advice; the project should confirm licensing and distribution strategy (e.g. whether to bundle or to require a separate user-installed wimlib) before committing to bundling.

---

## Risks and Effort

- **Parsing:** Replacing `Get-WimInfo` with `wimlib-imagex info` requires a new parser for wimlib’s output format.
- **Progress and cancellation:** Progress and cancel behavior must be reimplemented around wimlib-imagex’s process output and process termination.
- **Bootable image:** Testing with boot.wim and `--boot` is needed to confirm Windows PE boot behavior is unchanged.
- **Hybrid approach:** Retaining DISM for `/Add-Driver` only still requires DISM and Administrator for driver injection; mount/unmount/capture/export and DISM cleanup can be removed.
- **Full wimlib driver injection:** Implementing offline driver staging without DISM is significant effort and has compatibility and support risks.

---

## Recommendation

**Feasibility:** Replacing DISM with wimlib for WIM manipulation is **feasible** with one critical constraint: **driver injection**.

- **Recommended approach (hybrid):**
  - Use **wimlib-imagex** for: WIM info, extract (apply), capture (with `--boot` where needed), append, export, and optimize.
  - Use **DISM only** for: `/Image:<path> /Add-Driver` on the *extracted* directory.
  - Cleanup: delete extraction directories; remove reliance on DISM mount list and `Cleanup-Wim`.

This removes DISM mount, unmount, capture, export, and cleanup from the main flow, simplifies mount timeouts and cleanup, and keeps driver injection behavior unchanged. A full wimlib-only solution is feasible only if driver injection is implemented or delegated elsewhere, and is higher effort and risk.

---

## References

- wimlib: [https://github.com/ebiggers/wimlib](https://github.com/ebiggers/wimlib)
- wimlib README: [https://github.com/ebiggers/wimlib#readme](https://github.com/ebiggers/wimlib#readme)
- Windows-specific notes: [README.WINDOWS.md](https://github.com/ebiggers/wimlib/blob/master/README.WINDOWS.md)
- wimlib-imagex (overview): [https://wimlib.net/man1/wimlib-imagex.html](https://wimlib.net/man1/wimlib-imagex.html)
- wimcapture (including `--boot`): [https://wimlib.net/man1/wimcapture.html](https://wimlib.net/man1/wimcapture.html)
- wiminfo: [https://wimlib.net/man1/wiminfo.html](https://wimlib.net/man1/wiminfo.html)
- Application: [Core/ImageProcessor.cs](Core/ImageProcessor.cs), [Core/CleanupService.cs](Core/CleanupService.cs)
