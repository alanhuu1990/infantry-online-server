# Agent and contributor notes

## Documentation maintenance

### Bot features

Whenever you **implement or materially change** server-side or script bot behavior (new `ScriptBot` invoker, chat/mod spawn command, Bot Skirmish settings, movement/combat logic, or player-facing spawn rules), update **[`bot-testing.md`](bot-testing.md)** in the same change (or as a immediate follow-up in the same PR) so that:

- New or changed **commands** and **syntax** are recorded.
- **Script invoker** names match what `ScriptBot` / `Scripts.invokerTypeExists` expects.
- **Walkthrough steps** exist so a human can verify behavior in a private arena (and mod commands when relevant).
- Any new **config keys** or **vehicle/description conventions** are listed.

If you only fix an internal bug with no player-visible or operator-visible difference, a short note under an existing section is enough; skip redundant copy-paste.

### Other areas

Prefer updating the **closest existing doc** (e.g. `README.md`, `quick-start.md`) when your change affects setup or workflow; avoid orphan markdown files unless they fill a clear gap.

## Repository tooling

- **.NET 8**: Cloud Agent environments can bootstrap the SDK via [`.cursor/environment.json`](.cursor/environment.json) and [`scripts/cursor-cloud-init.sh`](scripts/cursor-cloud-init.sh). Local developers should install the SDK matching **`net8.0`** projects under `dotnetcore/`.

## Build sanity check

After C# changes under `dotnetcore/`:

```bash
dotnet build dotnetcore/InfServerNetCore.sln -c Release
```
