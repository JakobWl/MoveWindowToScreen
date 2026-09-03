# Move Window To Screen

A lightweight Windows background app that lets you move any window between monitors using a modern popup UI, keyboard hotkey, or system menu integration.

## Features

- **Popup UI (Ctrl+Alt+M)**: Opens a themed popup listing all windows with per-screen move buttons and a "Current" button to move to the screen the popup is on
- **Screen Identification**: When the popup opens, large numbered overlays appear on each monitor so you know which screen is which
- **System Menu Integration**: Shift+right-click any taskbar icon or title bar to see a "Move to screen" panel next to the window's system menu (with a fallback panel for apps that don't show a classic system menu)
- **System Tray**: Lives quietly in the system tray with a Win11-styled right-click menu
- **App Icons**: Each window in the popup shows its application icon
- **Theme Aware**: Reads the Windows dark/light theme setting from the registry and styles the UI accordingly
- **Smart Moving**: Preserves relative window position and size proportionally across screens
- **Minimized Window Support**: Minimized windows are automatically restored and brought to the front when moved
- **Restore on the Clicked Screen**: Clicking a minimized app's taskbar button on another monitor's taskbar restores the window onto that monitor instead of its original one — works with mouse clicks and with touch/pen taps on touch displays
- **Maximized Support**: Keeps maximized state — re-maximizes on the target monitor
- **DPI Aware**: Works correctly with mixed-DPI multi-monitor setups
- **Tray App Filtering**: Excludes tray-only programs and cloaked/suspended UWP apps from the window list
- **Startup**: Can launch automatically on Windows startup

## How to Use

1. Launch the app — it runs silently in the background with a system tray icon
2. Press **Ctrl+Alt+M** or left-click the tray icon to open the popup
3. Click a screen number button to move a window, or click **Current** to move it to the screen the popup is on
4. Alternatively, **Shift+right-click** any taskbar icon to get the "Move to screen" panel next to the system menu
5. To stop the app: right-click the tray icon → **Exit**

## Finding the App

The app runs as a background task. Look for it in:
- The **system tray** (notification area, bottom-right of taskbar)
- The **arrow/overflow menu** (▲) if the icon is hidden

## Building

Requires .NET 8 SDK.

```bash
dotnet build
dotnet run --project MoveWindowToScreen
```

## Publishing

### Self-contained executable

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

The output will be in `MoveWindowToScreen\bin\Release\net8.0-windows\win-x64\publish\`.

### MSIX package for Microsoft Store

1. Install the Windows Application Packaging tools in Visual Studio
2. Add a "Windows Application Packaging Project" to the solution, referencing this project
3. Right-click the packaging project → **Publish** → **Create App Packages**
4. Choose "Microsoft Store" or "Sideloading" and follow the wizard
5. Sign the package with your certificate
6. Submit the `.msixupload` to [Partner Center](https://partner.microsoft.com/dashboard)

### Sideloading (no Store)

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Distribute the single `.exe` from the publish folder. Users can run it directly — no installation needed.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 8 Runtime (or self-contained deployment)
- Multiple monitors (to make it useful!)

## License

MIT
