# Design — v0.2

## Backend
Windows ConPTY (`CreatePseudoConsole`) is used instead of `Process.StandardOutput`. This gives the child process a pseudo-console and allows interactive terminal behavior and ANSI sequences.

## Themes
- Matrix: green-on-black.
- MikroTik: restrained gray/green/cyan terminal palette inspired by RouterOS console aesthetics.
- Classic: neutral Windows terminal palette.

## Next candidates
- PTY tab manager.
- Search/copy/paste and selection improvements.
- 256-color/truecolor ANSI renderer.
- Settings JSON.
- RouterOS SSH profile and syntax highlighting.
- Matrix rain as an optional idle visual effect.
