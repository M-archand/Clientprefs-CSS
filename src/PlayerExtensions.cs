using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Clientprefs;

internal static class CCSPlayerControllerEx
{
    internal static bool IsValidPlayer(this CCSPlayerController? controller)
    {
        if (controller == null || !controller.IsValid || controller.Handle == IntPtr.Zero
            || controller.Connected != PlayerConnectedState.Connected || controller.IsHLTV
            || controller.IsBot || controller.SteamID.ToString().Length != 17)
        {
            return false;
        }

        return true;
    }
}