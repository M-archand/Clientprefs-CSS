using CounterStrikeSharp.API.Core;
using Clientprefs.API;
using Microsoft.Extensions.Logging;

namespace Clientprefs;

public partial class Clientprefs
{
    public int AddClientprefCommands(int id, string name, string description, CookieAccess access)
    {
        lock (_clientprefIdLock)
        {
            var cookie = new Cookie()
            {
                Id = id,
                Name = name,
                Description = description,
                Access = access
            };
            Cookies[cookie.Id] = cookie;
            _cookiesByName[cookie.Name] = cookie;
            return id;
        }
    }

    public bool TryFindCookieId(string name, out int id)
    {
        if (_cookiesByName.TryGetValue(name, out var cookie))
        {
            id = cookie.Id;
            return true;
        }
        id = -1;
        return false;
    }

    public int FindPlayerCookie(string name)
    {
        return TryFindCookieId(name, out var id) ? id : -1;
    }

    public bool TryGetCookieAccess(int cookieId, out CookieAccess access)
    {
        if (Cookies.TryGetValue(cookieId, out var cookie))
        {
            access = cookie.Access;
            return true;
        }
        access = default;
        return false;
    }

    public CookieAccess GetCookieAccess(int cookieId)
    {
        TryGetCookieAccess(cookieId, out var access);
        return access;
    }

    public void AddPlayerClientPrefNewValue(string steamId, int cookieId, string value)
    {
        PlayerSettings.GetOrAdd(steamId, _ => new()).SetCookie(cookieId, value);
    }

    public int ClientPrefCount()
    {
        return Cookies.Count;
    }

    public void LogWarning(string message)
    {
        Logger.LogWarning(message);
    }
}

public class ClientprefsApi : IClientprefsApi
{
    private Clientprefs _plugin;

    public ClientprefsApi(Clientprefs plugin)
    {
        _plugin = plugin;
    }

    public event Action<CCSPlayerController>? OnPlayerCookiesCached;
    public event Action? OnDatabaseLoaded;

    // The event 'ClientprefsApi.OnPlayerCookiesCached' can only appear on the left hand side of +=
    // or -= (except when used from within the type 'ClientprefsApi')
    public void CallOnDatabaseLoaded()
    {
        InvokeGuarded(OnDatabaseLoaded, nameof(OnDatabaseLoaded), handler => handler());
    }

    public void CallOnPlayerCookiesCached(CCSPlayerController player)
    {
        InvokeGuarded(OnPlayerCookiesCached, nameof(OnPlayerCookiesCached), handler => handler(player));
    }

    private void InvokeGuarded<T>(T? handlers, string eventName, Action<T> invoke) where T : Delegate
    {
        if (handlers == null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((T)handler);
            }
            catch (Exception ex)
            {
                var method = handler.Method;
                _plugin.Logger.LogError($"{eventName} subscriber {method.DeclaringType?.FullName}.{method.Name} ({method.DeclaringType?.Assembly.GetName().Name}) threw: {ex}");
            }
        }
    }

    public async Task<int> RegPlayerCookieAsync(string name, string description, CookieAccess access = CookieAccess.CookieAccess_Public)
    {
        if (_plugin.TryFindCookieId(name, out var existingId))
        {
            return existingId;
        }

        if (name.Length > IClientprefsApi.COOKIE_MAX_NAME_LENGTH)
        {
            _plugin.LogWarning($"RegPlayerCookieAsync was used with name being too long");
        }
        if (description.Length > IClientprefsApi.COOKIE_MAX_DESCRIPTION_LENGTH)
        {
            _plugin.LogWarning($"RegPlayerCookieAsync was used with description being too long");
        }

        int id = await _plugin.CreatePlayerCookieNew(name, description, access);

        if (id < 0)
        {
            return -1;
        }

        return _plugin.AddClientprefCommands(id, name, description, access);
    }

    public int FindPlayerCookie(string name)
    {
        return _plugin.TryFindCookieId(name, out var id) ? id : -1;
    }

    public string GetPlayerCookie(CCSPlayerController player, int cookieId)
    {
        if (!player.IsValidPlayer())
        {
            throw new Exception($"GetPlayerCookie failed due to player being invalid");
        }

        if (!_plugin.PlayerSettings.TryGetValue(player.SteamID.ToString(), out var pref) || !pref.Loaded)
        {
            throw new Exception($"GetPlayerCookie failed due to player not being loaded yet. Use OnPlayerCookiesCached");
        }

        if (pref.TryGetCookie(cookieId, out var cookie))
        {
            return cookie.NewValue;
        }
        return "";
    }

    public void SetPlayerCookie(CCSPlayerController player, int cookieId, string value)
    {
        if (!player.IsValidPlayer())
        {
            throw new Exception($"SetPlayerCookie failed due to player being invalid");
        }

        if (value.Length > IClientprefsApi.COOKIE_MAX_VALUE_LENGTH)
        {
            throw new Exception($"SetPlayerCookie failed: value length {value.Length} exceeds COOKIE_MAX_VALUE_LENGTH ({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}) for cookie id {cookieId}");
        }

        if (!_plugin.PlayerSettings.TryGetValue(player.SteamID.ToString(), out var pref) || !pref.Loaded)
        {
            throw new Exception($"SetPlayerCookie failed due to player not being loaded");
        }

        var steamId = player.SteamID.ToString();

        _plugin.AddPlayerClientPrefNewValue(steamId, cookieId, value);
    }

    public void SetPlayerCookie(string steamId, int cookieId, string value)
    {
        if (value.Length > IClientprefsApi.COOKIE_MAX_VALUE_LENGTH)
        {
            throw new Exception($"SetPlayerCookie failed: value length {value.Length} exceeds COOKIE_MAX_VALUE_LENGTH ({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}) for cookie id {cookieId}");
        }

        if (!_plugin.PlayerSettings.TryGetValue(steamId, out var pref) || !pref.Loaded)
        {
            throw new Exception($"SetPlayerCookie failed due to it being called before cookies were loaded for player {steamId}");
        }

        _plugin.AddPlayerClientPrefNewValue(steamId, cookieId, value);
    }

    public bool ArePlayerCookiesCached(CCSPlayerController player)
    {
        if (!player.IsValidPlayer())
        {
            throw new Exception($"ArePlayerCookiesCached failed due to player being invalid");
        }

        var steamId = player.SteamID.ToString();

        return _plugin.PlayerSettings.TryGetValue(steamId, out var pref) && pref.Loaded;
    }
}