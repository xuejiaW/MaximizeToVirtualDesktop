# Development conventions

- Use Angular-style commit subjects: `type(scope): imperative description`.
- Keep Windows-version compatibility separate from window-management behavior.
- Run `dotnet test -c Release` and the isolated integration smoke test before handoff.
- Integration tests may manage only their own test windows and temporary desktops, and must restore the starting desktop.
- Never enable startup or start global automatic management as part of a test.
- Use native PowerShell `Remove-Item -LiteralPath ... -ErrorAction Stop` without `-Force` for task temporary files. Verify absolute paths and contents before recursive deletion.
