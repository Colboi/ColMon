Colmon portable package
=======================

System requirements
-------------------
- Windows 11 x64.
- No separate .NET installation is required.
- The Codex weekly, Codex 5h limit, and Tokens today windows require Codex to be
  installed and signed in for the current Windows user.

Start
-----
1. Extract the entire ZIP to a normal writable folder.
2. Double-click Colmon.exe.
3. Use the violet C/M icon in the notification area to show or hide all
   taskbar windows, or to exit.

The Pomodoro timer works without Codex. On first start, Colmon reads Codex data
from the current Windows user's Codex installation and .codex directory. It
does not need credentials copied from the computer that built this package.

Configuration
-------------
The default configuration is built into Colmon. To use a custom configuration,
copy config\colmon.example.json to a file outside this package, edit it, and
start Colmon from PowerShell:

  .\Colmon.exe --config "C:\path\to\colmon.json"

Per-user window settings, Pomodoro state, diagnostics, and logs are stored under:

  %LocalAppData%\Colmon

They are intentionally excluded from this portable package. Deleting the
extracted package therefore does not delete those settings.

Troubleshooting
---------------
- If Codex weekly shows --%, start Codex once and confirm that it is signed in.
- If Tokens today shows --, confirm that the current user has Codex session
  files under %USERPROFILE%\.codex\sessions.
- Windows SmartScreen may warn about an unsigned application. Extract the ZIP
  to a normal writable folder before running it.
- Runtime logs are under %LocalAppData%\Colmon\artifacts\runtime.

Privacy
-------
The package contains no account credentials, browser cookies, Codex sessions,
usage history, LocalAppData settings, or logs from the build computer.
