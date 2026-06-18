using CounterStrikeSharp.API.Core.Capabilities;
using Clientprefs.API;
using System.Collections.Concurrent;

namespace Clientprefs;

public partial class Clientprefs
{
    public class Cookie
    {
        public int Id { get; set; } = -1;
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public CookieAccess Access { get; set; } = CookieAccess.CookieAccess_Public;
    }

    public class ClientCookie
    {
        public int Id { get; set; } = -1;
        public string? OldValue { get; set; } = null;  // null = no persisted DB row, "" = persisted empty value
        public string NewValue { get; set; } = "";     // save if oldvalue != newvalue
    }

    public class PlayerSetting
    {
        public bool Loaded { get; set; } = false;
        public bool LoadFailed { get; set; } = false;
        private ConcurrentDictionary<int, ClientCookie> _cookieDict { get; set; } = new();

        public void LoadCookie(int id, string dbValue)
        {
            _cookieDict.AddOrUpdate(id,
                _ => new ClientCookie
                {
                    Id = id,
                    OldValue = dbValue,
                    NewValue = dbValue
                },
                (_, cookie) =>
                {
                    cookie.OldValue = dbValue;
                    cookie.NewValue = dbValue;
                    return cookie;
                });
        }

        public void SetCookie(int id, string value)
        {
            _cookieDict.AddOrUpdate(id,
                _ => new ClientCookie
                {
                    Id = id,
                    OldValue = null,
                    NewValue = value
                },
                (_, cookie) =>
                {
                    cookie.NewValue = value;
                    return cookie;
                });
        }

        public bool TryGetCookie(int id, out ClientCookie clientCookie)
        {
            return _cookieDict.TryGetValue(id, out clientCookie!);
        }

        public int CookieCount
        {
            get => _cookieDict.Count;
        }

        public List<ClientCookie> Cookies
        {
            get => _cookieDict.Values.ToList();
        }
    }

    public ClientprefsConfig Config { get; set; } = new();
    public PluginCapability<IClientprefsApi> PluginCapability = new("Clientprefs");
    public required ClientprefsApi ClientprefsApi { get; set; }
    public ConcurrentDictionary<int, Cookie> Cookies = new();
    public ConcurrentDictionary<string, PlayerSetting> PlayerSettings = new();
    public int LatestClientprefID = 0;
    private readonly object _clientprefIdLock = new();

    private ConcurrentDictionary<string, Cookie> _cookiesByName = new();
    private readonly HashSet<CounterStrikeSharp.API.Modules.Timers.Timer> _saveTimers = new();
    private MySqlConnector.MySqlDataSource? _mySqlDataSource;
    private bool _databaseLoaded = false, _isMySQL = false;
    private bool _allPluginsLoaded = false, _databaseLoadedEventFired = false;
}