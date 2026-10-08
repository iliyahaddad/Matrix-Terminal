# Changelog

## 0.2.2
- Hardened ConPTY startup cleanup: if child-process creation succeeds but a later setup step fails, the child process and native handle are now terminated and released.
- Version metadata updated to 0.2.2.


## 0.2.1
Fixes (v0.2.0 did not compile and would not have worked even if it had):
- `ConPtyTerminal`: `SafeFileHandle` has no `Read/Write` methods -> pipes are now wrapped in `FileStream`.
- Pseudo-console pipe ends were never closed (`SetHandleAsInvalid` leaked them), so the output pipe never ended.
- UTF-8 sequences split across reads are now decoded correctly (`Decoder`).
- `STARTUPINFO` used ANSI `string` fields in a Unicode call; now `IntPtr`.
- Dispose could deadlock: `Dispatcher.Invoke` from the reader thread vs. `ClosePseudoConsole` on the UI thread. Output is now queued and applied by a UI timer; close happens off-thread.
- Restarting a session reused one object, so the old reader thread read from the *new* pipe and a stale "SHELL EXITED" appeared. Each session now has its own terminal/screen/queue.
- Shell exit is now detected via the process handle (ConPTY does not close the pipe by itself).
- Child process is terminated when the window closes.
- Renderer: every output chunk started a new paragraph (each typed character on its own line), and ConPTY's cursor-positioning/OSC sequences were printed as garbage. Replaced with a real VT screen buffer (`VtScreen`) + scrollback. Answers ConPTY's cursor-position query (`ESC[6n`).
- Input: Ctrl+C always sent ^C so copying was impossible; added copy/paste, Ctrl+A..Z, Ctrl+arrows, Insert, Shift+Tab, AltGr safe. Buttons no longer steal keyboard focus.
- Console size now follows the window (ConPTY resize was never called).
- Font fallback (Cascadia Mono -> Consolas -> Courier New) for Windows 10 without Cascadia.
- Docs/version updated to 0.2.1.
