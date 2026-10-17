# Clientprefs for CSSharp
This plugin exposes natives for developers to save player data to SQLite / MySQL without adding any SQL code to their plugin, giving developers easy access to persist player cookies to a database.

Cookies are loaded on player connect, cached in memory while the player is on the server, and flushed back to the database on disconnect, map change, and plugin unload. Other plugins consume the API through the shared `ClientprefsApi` assembly and the `Clientprefs` plugin capability.

## Requirements
- CounterStrikeSharp with API version **369** or newer (`MinimumApiVersion(369)`).
- .NET 10 runtime (the plugin targets `net10.0`).
- A database: SQLite (default, zero-config, stored as `clientprefs.db` in the plugin folder) or MySQL.

## Example
```c#
using Clientprefs.API;

private readonly PluginCapability<IClientprefsApi> PluginCapability = new("Clientprefs");
private IClientprefsApi? ClientprefsApi;
private int CookieID = -1;
private Dictionary<CCSPlayerController, string> PlayerCookie = new();

public override void Unload(bool hotReload)
{
    base.Unload(hotReload);

    if (ClientprefsApi == null) return;

    ClientprefsApi.OnDatabaseLoaded -= OnClientprefDatabaseReady;
    ClientprefsApi.OnPlayerCookiesCached -= OnPlayerCookiesCached;
}

public override void OnAllPluginsLoaded(bool hotReload)
{
    ClientprefsApi = PluginCapability.Get();

    if (ClientprefsApi == null) return;

    ClientprefsApi.OnDatabaseLoaded += OnClientprefDatabaseReady;
    ClientprefsApi.OnPlayerCookiesCached += OnPlayerCookiesCached;

    if (hotReload)
    {
        if (CookieID == -1) return;

        foreach (var player in Utilities.GetPlayers().Where(p => !p.IsBot))
        {
            if (!ClientprefsApi.ArePlayerCookiesCached(player)) continue;
            PlayerCookie[player] = ClientprefsApi.GetPlayerCookie(player, CookieID);
        }
    }
}

public void OnClientprefDatabaseReady()
{
    if (ClientprefsApi == null) return;

    Task.Run(async () =>
    {
        CookieID = await ClientprefsApi.RegPlayerCookieAsync("example_cookie", "Example cookie description", CookieAccess.CookieAccess_Public);

        if (CookieID == -1)
        {
            Logger.LogError("[Clientprefs-Example] Failed to register/load cookie");
            return;
        }

        Logger.LogInformation($"[Clientprefs-Example] Registered/Loaded cookie with ID: {CookieID}");
    });
}

public void OnPlayerCookiesCached(CCSPlayerController player)
{
    if (ClientprefsApi == null || CookieID == -1) return;

    PlayerCookie[player] = ClientprefsApi.GetPlayerCookie(player, CookieID);

    Logger.LogInformation($"[Clientprefs-Example] Cookie value: {PlayerCookie[player]}");
}

[ConsoleCommand("css_clientprefs_example", "Saves example clientprefs cookie value")]
public void OnExampleCommand(CCSPlayerController? caller, CommandInfo _)
{
    if (caller == null || !caller.IsValid || ClientprefsApi == null || CookieID == -1)
    {
        return;
    }

    ClientprefsApi.SetPlayerCookie(caller, CookieID, "example_value");
}
```

## API
The `IClientprefsApi` interface exposes:
- `Task<int> RegPlayerCookieAsync(name, description, access)` - registers a cookie (or returns the existing id). **Async** - `await` it from inside `OnDatabaseLoaded`. Returns `-1` on failure.
- `int FindPlayerCookie(name)` - returns the id of a registered cookie, or `-1`.
- `string GetPlayerCookie(player, cookieId)` - reads a cached cookie value (only after `OnPlayerCookiesCached`).
- `void SetPlayerCookie(player, cookieId, value)` and `void SetPlayerCookie(steamId, cookieId, value)` - set a cookie value in memory; it is persisted on the next flush.
- `bool ArePlayerCookiesCached(player)` - true once the player's cookies are loaded.
- Events: `OnDatabaseLoaded`, `OnPlayerCookiesCached(player)`.


## Commands
- `css_cookies` / `css_cookie <name> [value]` - list cookies, or view/change the value of a public cookie for the calling player.

## Config
```json
{
  "TableName": "clientprefs_cookies",
  "TableNamePlayerData": "clientprefs_playerdata",
  "DatabaseType": "sqlite",
  "DatabaseHost": "",
  "DatabaseName": "",
  "DatabaseUsername": "",
  "DatabasePassword": "",
  "DatabasePort": 3306,
  "DatabaseSslmode": "preferred",
  "Debug": false,
  "ConfigVersion": 1
}
```
Set `DatabaseType` to `"mysql"` and fill in the connection fields to use MySQL; leave it as `"sqlite"` for a zero-config local database. `DatabaseSslmode` accepts `none`, `preferred` (default), `required`, `verifyca` or `verifyfull`. Table names must be 1-64 letters, digits or underscores and must differ; an invalid config disables the plugin at load with an error in the console. Enable `Debug` for verbose load/save logging.


## Changelog

See [CHANGELOG.md](CHANGELOG.md).
