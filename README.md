# Move Window To Screen

A lightweight Windows background app that lets you move any window between monitors using a modern popup UI, keyboard hotkey, or system menu integration.

## Features

- **Popup UI (Ctrl+Alt+M)**: Opens a themed popup listing all windows with per-screen move buttons and a "Current" button to move to the screen the popup is on
- **Screen Identification**: When the popup opens, large numbered overlays appear on each monitor so you know which screen is which
- **System Menu Integration**: Shift+right-click any taskbar icon or title bar to see "Move to current screen" and "Move to screen >" submenu entries
- **System Tray**: Lives quietly in the system tray with a Win11-styled right-click menu
- **App Icons**: Each window in the popup shows its application icon
- **Theme Aware**: Reads the Windows dark/light theme setting from the registry and styles the UI accordingly
- **Smart Moving**: Preserves relative window position and size proportionally across screens
- **Minimized Window Support**: Minimized windows are automatically restored and brought to the front when moved
- **Maximized Support**: Keeps maximized state — re-maximizes on the target monitor
- **DPI Aware**: Works correctly with mixed-DPI multi-monitor setups
- **Tray App Filtering**: Excludes tray-only programs and cloaked/suspended UWP apps from the window list
- **Startup**: Starts at Windows sign-in by default after the first launch. Right-click the tray icon and select **Start at sign-in** to change it.

## How to Use

1. Launch the app — it runs silently in the background with a system tray icon
2. Press **Ctrl+Alt+M** (or your configured shortcut) or left-click the tray icon to open the popup
3. Click a screen number button to move a window, or click **Current** to move it to the screen the popup is on
4. Alternatively, **Shift+right-click** any taskbar icon to access "Move to current screen" or the per-screen submenu
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
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The output will be in `MoveWindowToScreen\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\`.

### MSIX package for Microsoft Store

1. Install the Windows Application Packaging tools in Visual Studio
2. Add a "Windows Application Packaging Project" to the solution, referencing this project
3. Right-click the packaging project → **Publish** → **Create App Packages**
4. Choose "Microsoft Store" or "Sideloading" and follow the wizard
5. Sign the package with your certificate
6. Submit the `.msixupload` to [Partner Center](https://partner.microsoft.com/dashboard)

### GitHub Actions Store updates

Run **Microsoft Store release** from the Actions tab. Store verification and publishing run from the default branch. **build** creates an x86/x64
Store upload artifact. **verify** also checks API access to the existing listing.
**publish** submits the package for Store certification. Set `package_version` to
an unused Store version ending in `.0` when preparing an update.

The workflow uses repository variable `MSSTORE_APP_ID` (`9N66VVZBN23B`). Add
`PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`,
and `PARTNER_CENTER_CLIENT_SECRET` as secrets on the `microsoft-store` GitHub
environment. Use a Microsoft Entra application that has Store submission access
in Partner Center.

### Sideloading (no Store)

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Distribute the single `.exe` from the publish folder. Users can run it directly — no installation needed.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 8 Runtime (or self-contained deployment)
- Multiple monitors (to make it useful!)

## License

MIT
