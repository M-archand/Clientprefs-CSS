using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Text.RegularExpressions;
using Clientprefs.API;

namespace Clientprefs;

[MinimumApiVersion(369)]
public partial class Clientprefs : BasePlugin, IPluginConfig<ClientprefsConfig>
{
    public override string ModuleName => "Clientprefs";
    public override string ModuleAuthor => "Cruze, Marchand";
    public override string ModuleVersion => "1.1.1";

    private const int UnloadFlushTimeoutSeconds = 30;

    private static readonly Regex TableNamePattern = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);

    public void OnConfigParsed(ClientprefsConfig config)
    {
        Config = config;
        _isMySQL = Config.DatabaseType.Equals("mysql", StringComparison.OrdinalIgnoreCase);
        _configError = ValidateConfig(config);
    }

    private string? ValidateConfig(ClientprefsConfig config)
    {
        if (!TableNamePattern.IsMatch(config.TableName))
        {
            return $"TableName \"{config.TableName}\" is invalid; use 1-64 letters, digits or underscores.";
        }

        if (!TableNamePattern.IsMatch(config.TableNamePlayerData))
        {
            return $"TableNamePlayerData \"{config.TableNamePlayerData}\" is invalid; use 1-64 letters, digits or underscores.";
        }

        if (string.Equals(config.TableName, config.TableNamePlayerData, StringComparison.OrdinalIgnoreCase))
        {
            return "TableName and TableNamePlayerData must be different tables.";
        }

        if (_isMySQL && !Enum.TryParse(config.DatabaseSslmode, true, out _mySqlSslMode))
        {
            return $"DatabaseSslmode \"{config.DatabaseSslmode}\" is invalid; use one of: {string.Join(", ", Enum.GetNames<MySqlSslMode>())}.";
        }

        return null;
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);

        Cookies = new();
        _cookiesByName = new();
        PlayerSettings = new();
        _connectedSteamIds.Clear();

        ClientprefsApi = new ClientprefsApi(this);
        Capabilities.RegisterPluginCapability(PluginCapability, () => ClientprefsApi);

        _databaseLoaded = false;
        _disabled = false;
        _allPluginsLoaded = false;
        _databaseLoadedEventFired = false;

        _ = Task.Run(ConnectDatabaseTableWithRetry);

        AddCommandListener("changelevel", OnMapEnd, HookMode.Pre);
        AddCommandListener("map", OnMapEnd, HookMode.Pre);
        AddCommandListener("host_workshop_map", OnMapEnd, HookMode.Pre);
        AddCommandListener("ds_workshop_changelevel", OnMapEnd, HookMode.Pre);
        RegisterListener<Listeners.OnMapEnd>(() => { SavePlayerCookies(); });
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        base.OnAllPluginsLoaded(hotReload);

        _allPluginsLoaded = true;
        TryFireDatabaseLoaded();
    }

    private void TryFireDatabaseLoaded()
    {
        if (_databaseLoadedEventFired || !_databaseLoaded || !_allPluginsLoaded)
        {
            return;
        }

        _databaseLoadedEventFired = true;
        ClientprefsApi.CallOnDatabaseLoaded();
    }

    public override void Unload(bool hotReload)
    {
        base.Unload(hotReload);

        if (_databaseLoaded)
        {
            try
            {
                Logger.LogInformation($"Flushing player cookies to database before unload (timeout {UnloadFlushTimeoutSeconds}s)...");
                SavePlayerCookies();
                Task.WhenAll(_pendingSaves).WaitAsync(TimeSpan.FromSeconds(UnloadFlushTimeoutSeconds)).GetAwaiter().GetResult();
                Logger.LogInformation("Player cookie flush complete, unloading.");
            }
            catch (TimeoutException)
            {
                Logger.LogWarning($"Player cookie flush did not finish within {UnloadFlushTimeoutSeconds}s; unloading anyway. Some changes may not have been saved.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Player cookie flush failed during unload: {ScrubSecrets(ex.Message)}");
            }
        }

        _pendingSaves.Clear();
        _mySqlDataSource?.Dispose();
        _mySqlDataSource = null;
    }

    [ConsoleCommand("css_cookies", "sm_cookies <name> [value]")]
    [ConsoleCommand("css_cookie", "sm_cookie <name> [value]")]
    public void OnCookiesCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount <= 1)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Usage"]);
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Printing Cookie List"]);

            int count = 1;
            foreach (var pref in Cookies.Values)
            {
                command.ReplyToCommand($"{Localizer["Prefix"]} [{count}] {pref.Name} {pref.Description}");
                count++;
            }
            return;
        }

        if (player == null || !player.IsValid)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["No Console"]);
            return;
        }

        var name = command.GetArg(1);

        int cookieId = FindPlayerCookie(name);

        if (cookieId < 0)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie not Found", name]);
            return;
        }

        if (!TryGetCookieAccess(cookieId, out var access))
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie not Found", name]);
            return;
        }

        if (access == CookieAccess.CookieAccess_Private)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie not Found", name]);
            return;
        }

        var steamId = player.SteamID.ToString();

        if (!PlayerSettings.TryGetValue(steamId, out var settings) || !settings.Loaded)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookies Not Loaded"]);
            return;
        }

        string value = settings.TryGetCookie(cookieId, out var cookie) ? cookie.NewValue : "";
        string description = Cookies[cookieId].Description;

        command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Value", name, description, value]);

        if (command.ArgCount < 3)
        {
            return;
        }

        if (access == CookieAccess.CookieAccess_Protected)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Protected Cookie", name]);
            return;
        }

        value = command.GetArg(2);

        if (value.Length > IClientprefsApi.COOKIE_MAX_VALUE_LENGTH)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Value Too Long", name, IClientprefsApi.COOKIE_MAX_VALUE_LENGTH]);
            return;
        }

        settings.SetCookie(cookieId, value);
        command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Changed Value", name, value]);
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo _)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValidPlayer())
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID.ToString();
        _connectedSteamIds.Add(steamId);
        GetPlayerCookies(steamId);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo _)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValidPlayer())
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID.ToString();
        _connectedSteamIds.Remove(steamId);

        // Reason 1 = NetworkDisconnectionReason.NETWORK_DISCONNECT_SHUTDOWN (server shutdown / map change).
        // Those cookies are already flushed by OnMapEnd and Unload, so skip the per-player save here.
        if (@event.Reason == 1)
        {
            return HookResult.Continue;
        }

        Server.NextWorldUpdate(() => SaveDisconnectedPlayerCookies(steamId));
        return HookResult.Continue;
    }

    private void SaveDisconnectedPlayerCookies(string steamId)
    {
        if (!PlayerSettings.TryGetValue(steamId, out var pref))
        {
            return;
        }

        var load = pref.LoadTask;
        if (load == null || load.IsCompleted)
        {
            FinishDisconnectedPlayerSave(steamId);
            return;
        }

        load.ContinueWith(_ => Server.NextWorldUpdate(() => FinishDisconnectedPlayerSave(steamId)), TaskScheduler.Default);
    }

    private void FinishDisconnectedPlayerSave(string steamId)
    {
        if (!PlayerSettings.TryGetValue(steamId, out var pref))
        {
            return;
        }

        if (!pref.Loaded)
        {
            EvictIfDisconnected(steamId);
            return;
        }

        SavePlayerCookies(steamId);
    }

    private HookResult OnMapEnd(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (string.IsNullOrEmpty(commandInfo.ArgString)) return HookResult.Continue;

        SavePlayerCookies();
        return HookResult.Continue;
    }

    private int GetEpochTime()
    {
        return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private void DebugLog(string message)
    {
        if (Config.Debug)
        {
            Logger.LogInformation(message);
        }
    }
}