# AGENTS.md

## Cursor Cloud specific instructions

### Project Overview

Infantry Online Server Emulator — a C# / .NET 8.0 multiplayer game server. The primary solution is at `dotnetcore/InfServerNetCore.sln`.

### Build

```bash
cd dotnetcore && dotnet build InfServerNetCore.sln
```

**Known issue (as of commit 0262b44):** `ZoneServer` fails to build due to `ScriptArena.BotSkirmish.cs` referencing undefined members. All other projects (DatabaseServer, DirectoryServer, AccountServer, Daemon, DaemonConsole) build successfully. To build individual working projects:

```bash
dotnet build dotnetcore/DatabaseServer/DatabaseServer.csproj
dotnet build dotnetcore/DirectoryServer/DirectoryServer.csproj
dotnet build dotnetcore/AccountServer/AccountServer.csproj
dotnet build dotnetcore/Daemon/Daemon.csproj
dotnet build dotnetcore/DaemonConsole/DaemonConsole.csproj
```

### Running Services

Each server reads a `server.xml` from its working directory (the build output at `bin/Debug/net8.0/`).

- **AccountServer** (HTTP on port 1010): Requires `sudo` since port < 1024. Responds to `GET /` with "Works!" without a database. Other endpoints require SQL Server.
- **DatabaseServer** (UDP): Requires SQL Server for full operation.
- **DirectoryServer** (UDP port 4850): Requires SQL Server.
- **ZoneServer** (UDP port 1337): Supports standalone mode via `<connectionDelay value="0" />` in `server.xml` (bypasses database). Requires zone asset files in a `bin/assets/` folder.

### Testing

There are no automated test projects in this solution. Verification is done by building and running the servers.

### Lint

No dedicated linter configuration. The C# compiler warnings serve as the lint check:

```bash
cd dotnetcore && dotnet build --no-restore 2>&1 | grep -E "(warning|error) CS"
```

### Configuration

All servers use XML config (`server.xml`). Minimal required structure:

```xml
<xml>
  <protocol>
    <udpMaxSize value="1024" />
    <crcLength value="0" />
    <connectionTimeout value="30000" />
  </protocol>
  <bindIP value="127.0.0.1" />
  <bindPort value="1337" />
  <connectionDelay value="0" />
  <database>
    <connectionString value="Server=localhost;Database=Infantry;Trusted_Connection=True;" />
  </database>
</xml>
```

### CI

GitHub Actions workflow at `.github/workflows/dotnet.yml` builds all projects with `dotnet publish` targeting both `win-x64` and `linux-x64`.
