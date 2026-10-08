# Build notes

This package targets Windows 10/11 and .NET 8.

The development environment used to assemble this source package does not contain the Windows .NET SDK/runtime, so an actual Windows binary build was not performed here. Build and test on Windows with:

```powershell
dotnet restore MatrixTerminal.sln
dotnet build MatrixTerminal.sln -c Release
```

Then run:

```powershell
dotnet run --project src/MatrixTerminal -c Release
```

ConPTY APIs are available on supported Windows versions; the application checks the platform at startup.

v0.2.2 was reviewed statically; it still has not been compiled in the authoring environment (no Windows .NET SDK). The GitHub Actions workflow will build it on `windows-latest`.
