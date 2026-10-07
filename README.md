# Tangerine Screenshot

A hotkey-first screenshot tool for Windows that lives in the tray: press a key, grab a
window, a region or the whole screen, and the shot lands on a card in the corner — ready
to copy, save, or drag straight into another app.

Windows 10/11 · .NET 10 · WPF · MIT

![The tray menu in the light and dark theme](https://jingyi-iii.github.io/tangerine-screenshot/tray-menu.png)

> The figure is served from this repository's GitHub Pages site (`docs/`). A relative
> `docs/tray-menu.png` path is the cleaner form, but it resolves to
> `raw.githubusercontent.com`, which some networks block outright.

## Highlights

- **Global hotkeys** — `Ctrl+Shift+S` for a region or a window, `Ctrl+Shift+F` for the
  full screen. Left-clicking the tray icon starts a region capture as well.
- **Window snapping** — hovering the overlay outlines the window under the cursor; one
  click grabs it, with the invisible resize border trimmed off via DWM so the captured
  pixels match what you see.
- **Pixel loupe** — a 13-pixel magnifier follows the cursor through the whole gesture
  (nearest-neighbour, so you can see individual pixels), next to a chip showing the
  physical pixel size of the selection.
- **A card instead of a dialog** — click it to copy, drag it out to drop the file into
  Explorer or a chat, hover for *view / save / discard*. Ignore it and it slides away
  after a few seconds, archiving itself to `Pictures\Screenshots` and saying so on the
  way out.
- **Follows the system theme** — the overlay, the card and the tray menu all read from one
  light/dark theme dictionary and swap live when Windows does.
- **Nothing extra to ship** — the shutter sound is synthesised in memory on first use, and
  the icons are generated from a script.
- **Memory-conscious** — a tray utility should not sit on a working set: the app trims it
  once idle after the shot cards close.

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. A published build needs
  nothing installed — see [Publishing](#publishing-a-single-executable).

## Quick start

```powershell
git clone https://github.com/jingyi-iii/tangerine-screenshot.git
cd tangerine-screenshot
dotnet build screenshot\screenshot.csproj -c Release
.\screenshot\bin\Release\net10.0-windows\screenshot.exe
```

There is no main window: the app appears in the tray and teaches the shortcut once, on the
first run only. Press `Ctrl+Shift+S` and go.

## Usage

### Capturing

| How | What you get |
| --- | --- |
| `Ctrl+Shift+S` | The selection overlay: a window, or a region you drag |
| `Ctrl+Shift+F` | The whole virtual screen, immediately |
| Left-click the tray icon | Same as `Ctrl+Shift+S` |
| Right-click the tray icon | The menu: *Region / Window Screenshot*, *Full Screen*, *Exit* |

### The selection overlay

| Gesture | Result |
| --- | --- |
| Hover | The window under the cursor is outlined |
| Click | Capture the outlined window, or the monitor under the cursor if no window is outlined |
| Drag | Capture the region you draw |
| `Space` | Capture the monitor under the cursor |
| `Esc` | Cancel |

A tiny accidental drag is treated as a do-over rather than a request to grab the whole
monitor.

### The preview card

| Gesture | Result |
| --- | --- |
| Click | Copy to the clipboard (as an image and as PNG bytes) |
| Drag out | Drop the file into another app; drops the in-memory image if no file exists |
| Hover → view | Open the shot full screen (click anywhere or `Esc` to close) |
| Hover → save | Save as PNG, JPEG or BMP |
| Hover → discard | Delete the shot |
| `Esc` | Put the shot away safely — it is archived, not deleted |
| Do nothing | After ~6 seconds it slides off the edge and archives to `Pictures\Screenshots` |

Hovering the card, or reaching for it while it is sliding away, keeps it around.

### The tray menu

If another program already owns `Ctrl+Shift+S`, the menu grows a red
**Hotkeys unavailable — click to retry** row, which is the fix for the one failure that
makes the app useless.

## Command-line switches

| Switch | Purpose |
| --- | --- |
| `--selftest` | Headless smoke test: region and full-screen capture, DPI scale, tray icon. Writes `%TEMP%\screenshot_selftest.txt`; exit code 1 on failure |
| `--verify-flow` | Drives capture → preview without a human and asserts the card, its size, its icon and the file behind it. Exit code 1 on failure |
| `--demo-capture` | Take one full-screen shot and leave the card on screen |
| `--demo-menu` | Open the tray menu at the icon and leave it up |
| `--menu-shot[=<dir>]` | Render the tray menu — both themes, both hotkey states — to PNGs. The light and dark renders are what `docs/tray-menu.png` is composed from |

## Where your files go

| What | Where |
| --- | --- |
| Working copy of each shot | `%TEMP%\screenshot_<timestamp>.png` — removed when you discard the card and on exit; leftovers from a crash are swept at the next start once they are a day old |
| Archived shots | `Pictures\Screenshots`, falling back to a `captures` folder next to the executable if that is not writable |
| Trace log | `Pictures\Screenshots\screenshot-trace.log`, else `%TEMP%`, else next to the executable |
| First-run flags | `%LocalAppData%\screenshot\settings.txt` |
| Crash reports | `screenshot_crash.log` next to the executable, else `%TEMP%` (at most 5 per session) |
| Single-file native libraries | `%TEMP%\.net\screenshot\` on the first run of a published build; relocate it with `DOTNET_BUNDLE_EXTRACT_BASE_DIR` |

## Publishing a single executable

```powershell
scripts\publish.ps1                 # Release, win-x64, one self-contained exe
scripts\publish.ps1 -Clean -SelfTest -Run
```

The output is a single `screenshot.exe` (~66 MB) in
`screenshot\bin\Release\net10.0-windows\win-x64\publish\`, with the runtime bundled — no
.NET install needed on the target machine. The settings live in the
[`win-x64` publish profile](screenshot/Properties/PublishProfiles/win-x64.pubxml).

WPF cannot be trimmed or AOT-compiled (XAML/BAML resolves through reflection), so
self-contained single-file is the only "static" packaging form available. Note what that
does and does not mean:

- One file to distribute, but not one file on disk at runtime: native libraries are
  extracted to `%TEMP%\.net\screenshot\` on first launch.
- `EnableCompressionInSingleFile` trades a slightly slower first launch for ~66 MB instead
  of ~160 MB.
- The executable is unsigned, so other machines will show a SmartScreen warning until it
  earns reputation.

## Repository layout

```
screenshot/
  App.xaml(.cs)            Startup, wiring, single-instance-less tray lifetime
  OverlayWindow            Region selection: dim mask, window snapping, loupe, size chip
  PreviewWindow            The shot card: copy, drag-out, archive, auto-dismiss
  ViewerWindow             Full-screen view of one shot
  IntroWindow              The one-time "press Ctrl+Shift+S" pill
  TrayMenuWindow           The themed tray menu (light/dark aware)
  Services/
    ScreenshotManager      Capture orchestration, hotkeys, clipboard, temp files
    ScreenCapture          GDI BitBlt capture
    TrayService            NotifyIcon, shell registration, icon geometry
    Settings               Tiny persisted first-run flags
    ShutterSound           The synthesised shutter click
    DiagnosticLog          The trace log: a tray app has no console
    MemoryHelper           Working-set trimming
    NativeMethods          P/Invoke surface
  Themes/
    LightTheme / DarkTheme Identical key sets — the app swaps the whole dictionary
    ThemeManager           Follows the Windows app theme, live
    ButtonAssist           Per-button hover tint
    WindowIcon             The shared window icon
  Assets/                  app.ico, icon.png (both generated)
  Properties/PublishProfiles/win-x64.pubxml
scripts/
  clean.ps1                Remove build artifacts (-All for .vs and the extract cache)
  publish.ps1              Single-file publish, optional self-test and launch
  generate-assets.ps1      Regenerate Assets\icon.png and Assets\app.ico
  probe-tray-menu.ps1      Drive a real tray right-click and click a row (QA)
```

## Design notes

- **The tray menu is a WPF window, not a `ContextMenuStrip`.** WinForms draws its own
  fixed chrome and cannot follow the app's theme, which left the menu as the only surface
  that ignored light/dark. The icon's position comes from the shell
  (`Shell_NotifyIconGetRect`) and is converted with the real DPI of the monitor the window
  lands on, so the menu hugs the icon at any scale.
- **Two theme dictionaries, one key set.** `ThemeManager` follows the Windows app theme
  and swaps dictionaries wholesale, so every consumer reads its brushes through
  `DynamicResource`.
- **Captures never request `CAPTUREBLT`**, so the app's own layered windows — overlay,
  cards, menu — can never appear in a shot. The overlay hides itself and the capture waits
  ~120 ms for the compositor before grabbing pixels.
- **Window snapping uses `DWMWA_EXTENDED_FRAME_BOUNDS`**, which excludes the invisible
  resize border that `GetWindowRect` includes.
- **Diagnostics first.** A tray utility that misbehaves has no console and no window to
  read, so every lifecycle step goes to the trace log, and the startup state — tray icon
  frame, hotkey registration, log location — is written before anything else.

## Development

| Script | What it does |
| --- | --- |
| `scripts\clean.ps1 [-All] [-DryRun]` | Remove `bin`/`obj` (`-All` also `.vs` and the single-file extract cache) |
| `scripts\publish.ps1 [-Clean] [-SelfTest] [-Run]` | Single-file publish |
| `scripts\generate-assets.ps1` | Regenerate the tangerine tile and the tray glyph in `Assets` |
| `scripts\probe-tray-menu.ps1 -Shot` | Photograph the open tray menu (layered windows included via `CAPTUREBLT`) |
| `scripts\probe-tray-menu.ps1 -Row <Region\|FullScreen\|Exit\|Outside>` | Click a menu row with real input |

The tray menu is the one surface that cannot be reached by a script the ordinary way, so
`probe-tray-menu.ps1` posts the WinForms tray callback message to the icon's window and
then clicks with `SetCursorPos`/`mouse_event`. That is how the menu's dismissal logic is
regression-checked — a menu that closes on mouse-down looks fine in a screenshot while
doing nothing at all.

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| The hotkeys do nothing | Another app owns them. Open the tray menu and click the red retry row |
| Nothing seems to happen at launch | Read the trace log — it names the tray icon frame, the hotkey state and where the log itself lives |
| The tray icon is missing | The log records whether the shell accepted the icon, and the app retries registration on startup |
| Windows warns about the published exe | It is unsigned; SmartScreen reputation clears with downloads, or sign it |

## Known limitations

- **Uniform DPI is assumed.** Capture and overlay geometry convert between DIPs and
  physical pixels with the primary monitor's scale, so a multi-monitor setup with different
  scale factors per monitor will be off.
- The UI strings are English only.
- The published executable is unsigned.

## License

[MIT](LICENSE) © jingyi-iii
