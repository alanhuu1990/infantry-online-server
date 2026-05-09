# Bot testing guide

How to exercise **script bots** (e.g. DuelBot, TacticalDuelBot) and related **zone bot** features in a local or staging Infantry zone server.

## Prerequisites

1. **Zone assets** include at least one **duel-style vehicle**: name contains `(d)` or `duelbot` (case-insensitive). These are the only chassis listed by the `?duelbot` / `?tacticalduelbot` commands.
2. **Scripts compiled**: the zone process loads C# scripts from the `scripts/` tree; `TacticalDuelBot` lives under `scripts/Bots/TacticalDuelBot/`. Invoker `TacticalDuelBot` is registered when the script namespace `InfServer.Script.TacticalDuelBot` is present (and may be auto-registered if `scripts.xml` omits or leaves the invoker empty).
3. **Private arena**: chat spawn commands only work when the arena name **starts with `#`**.
4. **Permissions**: `*spawnbot` requires **mod** level (e.g. Level2) as configured for your server.

## Player chat commands (private arenas)

Both commands share the same spawn rules, limits, and vehicle list; they differ only in **script invoker** (`DuelBot` vs `TacticalDuelBot`).

| Command | Script invoker | Purpose |
|--------|----------------|---------|
| `?duelbot …` | `DuelBot` | Classic duel bot (player-focused behavior in `scripts/Bots/DuelBot/`). |
| `?tacticalduelbot …` | `TacticalDuelBot` | Tactical duel bot: engages **hostile players and hostile bots**, with fire pacing, strafing, and forward terrain avoidance (`scripts/Bots/TacticalDuelBot/`). |

### Syntax

```text
?<cmd> options          # Help: syntax, examples, list hint
?<cmd> list             # Lists valid vehicle IDs and names for this zone
?<cmd> <vehicleId>      # Spawn at your position, your yaw
?<cmd> <vehicleId> <tileX>,<tileY>   # Spawn at map tile coordinates (values ×16 internally)
?<cmd> <vehicleId> <coord>           # e.g. A4 — grid letter + number (center of cell)
```

Replace `<cmd>` with `duelbot` or `tacticalduelbot`.

### Limits (from server code)

- Fails if **script invoker** is missing (`Script type doesn't exist`).
- **Zone bot cap** from `zoneConfig` when set; also a hard cap of **20** bots per arena for these chat commands.
- Only **`#...` private arenas**.

## Mod command: `*spawnbot`

Spawns a `ScriptBot` with an arbitrary **script type** name that exists in the scripting engine (must match `scripts.xml` / compiled invokers).

```text
*spawnbot <scriptType>, <vehicleId>
*spawnbot <scriptType>, <vehicleId>, <location>
```

Examples:

```text
*spawnbot TacticalDuelBot, 453
*spawnbot DuelBot, 453, 10, 20
```

Location forms match mod spawn conventions (tile pair or map coord). Types whose name contains **`Team`** attach the bot to your team and creator (see `Mod/Bots.cs`).

## Walkthrough: TacticalDuelBot (quick)

1. Start the zone server with a cfg that includes duel vehicles and compiled scripts.
2. Join a **private arena** (name starts with `#`).
3. Run `?tacticalduelbot list` and note a valid **vehicle ID**.
4. Run `?tacticalduelbot <id>` to spawn at your feet.
5. **Combat checks**
   - Bot acquires you if you are on a **different team** (or it has no team and treats others as hostile).
   - Bot **fires** with noticeable **cadence** (reaction delay), **strafes** near its preferred range, and tries to **avoid driving into blocked tiles** ahead.
6. **Bot vs bot**: spawn a second bot on another team (e.g. via `*spawnbot` with teams, or another player’s spawn) and confirm the tactical bot **targets enemy bots** as well as players.

## Walkthrough: DuelBot (quick)

Same as above but use `?duelbot` and compare baseline dueling behavior against `TacticalDuelBot`.

## Optional: vehicle `bot=` tuning (description field)

Script bots such as DuelBot and TacticalDuelBot support a vehicle **Description** prefix:

```text
bot=<key>:<value>,<key>:<value>,...
```

TacticalDuelBot keys include:

| Key | Meaning |
|-----|--------|
| `radius` / `detect` | Detection range (pixels/ticks per existing duel convention). |
| `distance` | Preferred standoff distance. |
| `tolerance` | Duel-band tolerance. |
| `strafe` | Strafe / band width tuning (matches duel script naming). |
| `reaction` | Minimum ms between shot decisions (floored). |
| `jitter` | Max yaw jitter applied when firing. |

## Zone Bot Skirmish (`GameType_BotSkirmish`)

Separate from `ScriptBot` duel commands: arena script **`GameType_BotSkirmish`** spawns server-managed `Bot` instances with skirmish AI. Tuned via **`server.xml`** keys under `bots/` (e.g. `bots/enabled`, `bots/count`, `bots/team`, `bots/vehicleId`, `bots/difficulty`, objective and squad settings). Use that gametype’s zone to validate **mass bot** behavior, respawn, and objectives.

## Checklist when adding or changing bot features

- [ ] Document any **new chat or mod commands** and **script invoker** names.
- [ ] Note **arena / permission / vehicle** requirements.
- [ ] Add a short **repro walkthrough** (spawn → expected behavior).
- [ ] If config-driven, list **server.xml / cfg keys**.
- [ ] Run `dotnet build dotnetcore/InfServerNetCore.sln` (or your CI target) after server changes.

---

Keep this file aligned with the code paths in `dotnetcore/ZoneServer/Game/Commands/Chat/Commands.cs` (duel helpers) and `dotnetcore/ZoneServer/Game/Commands/Mod/Bots.cs` (`*spawnbot`).
