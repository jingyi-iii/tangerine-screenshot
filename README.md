# tangerine-screenshot

A hotkey-first screenshot tool for Windows that lives in the tray.

Press a key, grab a window, a region or the whole screen. The shot lands on a card in the
corner, ready to copy, save, or drag into another app. Windows 10/11, .NET 10, WPF, MIT.

[Download the latest release](https://github.com/jingyi-iii/tangerine-screenshot/releases/latest):
one self-contained `screenshot.exe` for Windows x64, no .NET install required.

![The tray menu in the light and dark theme](https://jingyi-iii.github.io/tangerine-screenshot/tray-menu.png)

## Install

Building from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/jingyi-iii/tangerine-screenshot.git
cd tangerine-screenshot
dotnet build screenshot\screenshot.csproj -c Release
.\screenshot\bin\Release\net10.0-windows\screenshot.exe
```

There is no main window. The app appears in the tray and teaches you the shortcut once, on the
first run only.

## Use

| How | What you get |
| --- | --- |
| `Ctrl+Shift+S` | The selection overlay: a window, or a region you drag |
| `Ctrl+Shift+F` | The whole virtual screen, immediately |
| Left-click the tray icon | Same as `Ctrl+Shift+S` |
| Right-click the tray icon | The menu: region/window, full screen, exit |

In the overlay, hovering outlines the window under the cursor and a click captures it. Click
with nothing outlined and you get the monitor under the cursor, which is also what `Space`
does. Drag to draw a region, and `Esc` cancels. A 13-pixel magnifier follows the cursor, next
to a chip showing the pixel size of the selection.

The card: click to copy, drag out to drop the file into another app, hover for *view* / *save*
/ *discard*. `Esc` archives the shot to `Pictures\Screenshots`, and so does ignoring it for
about 6 seconds. Hovering it keeps it around.

`--selftest` runs a headless smoke test and `--verify-flow` drives capture through preview with
no human. `--menu-shot` renders the tray menu in both themes, which is where
`docs/tray-menu.png` comes from.

## Notes

Mixed-DPI multi-monitor is broken. Geometry is converted using the primary monitor's scale, so
a second monitor at a different scale factor will be off. I have one monitor, so I never hit
it.

The UI is English only, and the executable is unsigned, so other machines will show a
SmartScreen warning until it earns reputation. If another program already owns `Ctrl+Shift+S`,
the tray menu grows a red **Hotkeys unavailable** row you can click to retry.

## License

MIT
