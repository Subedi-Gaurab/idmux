# idmux

Intuitive CLI for `runas` on Windows — run PowerShell as any identity, keep long-lived persistent sessions, detach long jobs in the background, and watch everything live in a web UI. Zero `runas` boilerplate, zero plaintext passwords on the command line, zero `psexec`.

## Installation

Requires Windows, .NET 10 SDK, and PowerShell 7+ on `PATH`.

```powershell
dotnet build idmux.slnx -c Release
.\packaging\Install-Idmux.ps1
```

This copies the binaries to `%LOCALAPPDATA%\Programs\idmux`, adds that folder to your user `PATH`, registers the daemon for autostart at logon, and starts it immediately.

Open a new shell so the new `PATH` takes effect, then verify:

```powershell
idmux version
```

## Examples

### Run as the current user

```powershell
idmux run --script 'Get-Process pwsh | Select-Object Id, CPU'
```

### Run as a different identity (auto-prompts for password the first time)

```powershell
idmux run --identity DOMAIN\svc --script 'Restart-Service Spooler -Force'
```

Cross-domain identities are auto-detected and use `runas /netonly` semantics — no extra flag needed.

### Run a script file with typed arguments

```powershell
idmux run --script-file .\Deploy.ps1 --args-json '{"Env":"prod","DryRun":false}'
```

### Detach a long-running job and tail its output

```powershell
$id = idmux run --detach --script 'Start-Sleep 30; "done"'
idmux blocks tail --id $id --follow
```

### Persistent named session (variables, modules, connections survive across runs)

```powershell
idmux session create --name svc --identity DOMAIN\svc
idmux run --session svc --script '$counter = 0; Import-Module ActiveDirectory'
idmux run --session svc --script '$counter++; "run #$counter"'
idmux run --session svc --script 'Get-ADUser -Identity $env:USERNAME | Select-Object Name, Enabled'
idmux session destroy --name svc
```

### List sessions and recent runs

```powershell
idmux session list
idmux blocks list --limit 20
idmux blocks get --id <ID>
```

### Pre-cache a credential without running anything

```powershell
idmux capture --identity DOMAIN\svc
idmux drop    --identity DOMAIN\svc
```

### Daemon control

```powershell
idmux daemon status
idmux daemon pause    # reject new runs
idmux daemon resume
idmux daemon stop
```

### Web UI

```powershell
idmux ui              # start daemon + open the UI in your browser
idmux serve           # start daemon, print URL, don't open a browser
```

### Get help on any command

```powershell
idmux help
idmux run --help
idmux help session create
```
