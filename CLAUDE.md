# CLAUDE.md

When any command, argument, or behavior changes, update all three of these in lockstep, following the existing pattern in each:

1. **README.md** — keep the relevant example accurate; add a new example only if the change introduces a new use case.
2. **CLI help text** — `src/Idmux.Cli/Program.cs` (the `HelpText` class). Update both the per-command/per-subcommand topic and the root help if the change affects globals.
3. **idmux skill** — keep the agent-facing skill in sync with the new behavior.

A change is not done until all three reflect it.
