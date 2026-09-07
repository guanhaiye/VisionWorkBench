# Watchdog and update operations

`scripts/VisionWorkbench-watchdog.ps1` only supervises the packaged executable. It does not contain production logic, does not modify the database, and must run under the same service account that owns the data directory.

Before an upgrade, stop production, training, and communication, create a `.vwbackup`, then verify `release-manifest.json`. `OfflineUpdateService` stages the signed package and writes an explicit rollback marker. The process itself is never replaced while it is running; the installer or service host performs the final activation and can return to the marker target.

Example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/VisionWorkbench-watchdog.ps1 -Executable 'C:\Program Files\VisionWorkbench\VisionWorkbench.exe'
```
