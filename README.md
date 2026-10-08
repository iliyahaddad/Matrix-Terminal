# Matrix Terminal

A Windows terminal UI with real **ConPTY** backend, supporting CMD and Windows PowerShell plus Matrix, MikroTik-inspired and Classic themes.

## Features
- Real Windows pseudo-console (ConPTY), not redirected stdout.
- CMD and Windows PowerShell sessions.
- Matrix green/black theme.
- MikroTik-inspired dark terminal theme.
- Classic theme.
- Built-in VT/xterm screen emulator: cursor addressing, erase/scroll, alternate screen, 16/256/truecolor SGR, window title.
- Scrollback (5000 lines), live resize of the pseudo console with the window.
- Copy/paste: Ctrl+Shift+C / Ctrl+Shift+V (Ctrl+C copies when text is selected, otherwise sends Ctrl+C to the shell).
- Keyboard shortcuts: F6 Matrix, F7 MikroTik, F8 Classic, Ctrl+L clear scrollback.
- GitHub Actions build workflow.

## Build
Requires Windows 10/11 and .NET 8 SDK.

```powershell
dotnet build MatrixTerminal.sln -c Release
```

Run:
```powershell
dotnet run --project src/MatrixTerminal -c Release
```

## Architecture
The UI is WPF. The shell is attached to a Windows ConPTY using `CreatePseudoConsole`, so applications see a real console environment and can emit ANSI control sequences.

## Scope note
The MikroTik theme is a visual theme. It does not emulate RouterOS or provide a MikroTik device. A future release may add RouterOS syntax highlighting and an SSH profile.

## License
MIT. See `LICENSE`.
