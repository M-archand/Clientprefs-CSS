using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;

using Clientprefs.API;
using Microsoft.Extensions.Logging;

namespace Clientprefs;

[MinimumApiVersion(369)]
public partial class Clientprefs : BasePlugin, IPluginConfig<ClientprefsConfig>
{
    public override string ModuleName => "Clientprefs";
    public override string ModuleDescription => "Clientprefs plugin for CounterStrikeSharp";
    public override string ModuleAuthor => "Cruze";
    public override string ModuleVersion => "1.0.7";

    public void OnConfigParsed(ClientprefsConfig config)
    {
        Config = config;
        _isMySQL = Config.DatabaseType.Equals("mysql", StringComparison.OrdinalIgnoreCase);
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);

        Cookies = new();
        _cookiesByName = new();
        PlayerSettings = new();

        ClientprefsApi = new ClientprefsApi(this);
        Capabilities.RegisterPluginCapability(PluginCapability, () => ClientprefsApi);

        _databaseLoaded = false;
        _allPluginsLoaded = false;
        _databaseLoadedEventFired = false;

        _ = Task.Run(ConnectDatabaseTableWithRetry);

        AddCommandListener("changelevel", OnMapEnd, HookMode.Pre);
        AddCommandListener("map", OnMapEnd, HookMode.Pre);
        AddCommandListener("host_workshop_map", OnMapEnd, HookMode.Pre);
        AddCommandListener("ds_workshop_changelevel", OnMapEnd, HookMode.Pre);
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

        foreach (var timer in _saveTimers)
        {
            timer.Kill();
        }
        _saveTimers.Clear();

        try
        {
            SavePlayerCookies().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"An error occurred while saving player(s) data during unload: {ex.Message}");
        }
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

        if (!PlayerSettings.TryGetValue(steamId, out var settings) || !settings.TryGetCookie(cookieId, out var cookie))
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie not Found", name]);
            return;
        }

        string value = cookie.NewValue;
        string description = Cookies[cookieId].Description;

        command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Value", name, description, value]);

        if (access == CookieAccess.CookieAccess_Protected)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["Protected Cookie", name]);
            return;
        }

        value = command.GetArg(2);

        cookie.NewValue = value;
        command.ReplyToCommand(Localizer["Prefix"] + Localizer["Cookie Changed Value", name, value]);
    }

    /*
    [ConsoleCommand("css_settings", "Settings command for clientprefs")]
	public void OnSettingsCommand(CCSPlayerController? player, CommandInfo command)
	{
        if (player == null || !player.IsValid)
        {
            command.ReplyToCommand(Localizer["Prefix"] + Localizer["No Console"]);
            return;
        }
        command.ReplyToCommand($"{Localizer["Prefix"]} Not yet implemented");
        // ClientprefsApi.ShowCookieMenu(player);
    }
    */

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo _)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValidPlayer())
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID.ToString();
        GetPlayerCookies(steamId);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo _)
    {
        var player = @event.Userid;

        // Reason 1 = NetworkDisconnectionReason.NETWORK_DISCONNECT_SHUTDOWN (server shutdown / map change).
        // Those cookies are already flushed by OnMapEnd and Unload, so skip the per-player save here.
        if (player == null || !player.IsValidPlayer() || @event.Reason == 1)
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID.ToString();
        SaveDisconnectedPlayerCookies(steamId, 50);
        return HookResult.Continue;
    }

    private void SaveDisconnectedPlayerCookies(string steamId, int attemptsLeft)
    {
        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = AddTimer(0.1f, () =>
        {
            _saveTimers.Remove(timer!);

            if (PlayerSettings.TryGetValue(steamId, out var pref))
            {
                if (pref.LoadFailed)
                {
                    PlayerSettings.TryRemove(steamId, out _);
                    return;
                }

                if (attemptsLeft > 0 && !pref.Loaded)
                {
                    SaveDisconnectedPlayerCookies(steamId, attemptsLeft - 1);
                    return;
                }
            }

            SavePlayerCookies(steamId);
        });
        _saveTimers.Add(timer);
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
            Console.Write($"[");
            Console.ForegroundColor = ConsoleColor.Gray;    // Green
            Console.Write($"ClientPrefs");
            Console.ResetColor();
            Console.Write($"] ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}