using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;

namespace Clientprefs.API;

/// <summary>
/// Cookie access types for client viewing
/// </summary>
public enum CookieAccess
{
    /// <summary>
    /// Visible and Changeable by users.
    /// </summary>
    CookieAccess_Public,
    /// <summary>
    /// Read only to users.
    /// </summary>
    CookieAccess_Protected,
    /// <summary>
    /// Completely hidden cookie.
    /// </summary>
    CookieAccess_Private
};

/// <summary>
/// API for Clienprefs.
/// </summary>
public interface IClientprefsApi
{
    /// <summary>
    /// Maximum length of a cookie name.
    /// </summary>
    public const int COOKIE_MAX_NAME_LENGTH = 64;
    /// <summary>
    /// Maximum length of a cookie description.
    /// </summary>
    public const int COOKIE_MAX_DESCRIPTION_LENGTH = 128;
    /// <summary>
    /// Maximum length of a cookie value.
    /// </summary>
    public const int COOKIE_MAX_VALUE_LENGTH = 256;

    event Action<CCSPlayerController>? OnPlayerCookiesCached;
    event Action? OnDatabaseLoaded;

    /// <summary>
    /// Creates a new player preference cookie async.
    /// if a cookie with the same name or id exists,
    /// it will return the existing cookie id.
    /// -1 if unable to create.
    /// </summary>
    /// <param name="handler">ID of registered player cookie.</param>
    public Task<int> RegPlayerCookieAsync(string name, string description, CookieAccess access = CookieAccess.CookieAccess_Public);

    /// <summary>
    /// Returns cookieId for the given cookie name.
    /// -1 if cookie not found.
    /// </summary>
    /// <param name="handler">Handler to find player cookie.</param>
    public int FindPlayerCookie(string name);

    /// <summary>
    /// Set the value of a player preference cookie.
    /// </summary>
    /// <param name="handler">Handler to set player cookie.</param>
    public void SetPlayerCookie(CCSPlayerController player, int cookieId, string name);

    /// <summary>
    /// Set the value of a steamId preference cookie.
    /// </summary>
    /// <param name="handler">Handler to set steamId cookie.</param>
    public void SetPlayerCookie(string steamId, int cookieId, string name);

    /// <summary>
    /// Get the value of a Player preference cookie.
    /// </summary>
    /// <param name="handler">Handler to set player cookie.</param>
    public string GetPlayerCookie(CCSPlayerController player, int cookieId);

    /// <summary>
    /// Checks if a players cookies have been loaded from the database.
    /// </summary>
    /// <param name="handler">Handler to check if player cookies are loaded.</param>
    public bool ArePlayerCookiesCached(CCSPlayerController player);
}