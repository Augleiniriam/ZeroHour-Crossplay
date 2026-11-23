using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeroHourCrossPlay
{
    /// <summary>
    /// Génère un ticket de session Steam neuf via steam_api64.dll (déjà chargée par la build Steam du jeu).
    /// Sur la build Epic, steam_api64.dll n'est pas présente : IsAvailable renvoie false.
    /// </summary>
    internal static class SteamTicket
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SteamUserAccessor();

        // Signature SDK >= 1.57 : (self, buffer, max, out len, const SteamNetworkingIdentity*).
        // Avec les SDK plus anciens, le 5e argument (registre) est simplement ignoré en x64.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetAuthSessionTicketFn(IntPtr self, byte[] ticket, int max, out uint len, IntPtr identity);

        private static IntPtr Module => GetModuleHandleW("steam_api64.dll");

        public static bool IsAvailable => Module != IntPtr.Zero;

        /// <summary>Renvoie le ticket en hexadécimal, ou null en cas d'échec.</summary>
        public static string GetHex(out string error)
        {
            error = null;
            var mod = Module;
            if (mod == IntPtr.Zero) { error = "steam_api64.dll non chargée (pas la build Steam ?)"; return null; }

            IntPtr user = IntPtr.Zero;
            for (int v = 30; v >= 17 && user == IntPtr.Zero; v--)
            {
                var p = GetProcAddress(mod, $"SteamAPI_SteamUser_v{v:000}");
                if (p != IntPtr.Zero) user = Marshal.GetDelegateForFunctionPointer<SteamUserAccessor>(p)();
            }
            if (user == IntPtr.Zero)
            {
                // Très vieux SDK : SteamUser() exporté directement.
                var p = GetProcAddress(mod, "SteamUser");
                if (p != IntPtr.Zero) user = Marshal.GetDelegateForFunctionPointer<SteamUserAccessor>(p)();
            }
            if (user == IntPtr.Zero) { error = "interface ISteamUser introuvable"; return null; }

            var fn = GetProcAddress(mod, "SteamAPI_ISteamUser_GetAuthSessionTicket");
            if (fn == IntPtr.Zero) { error = "SteamAPI_ISteamUser_GetAuthSessionTicket introuvable"; return null; }

            var buf = new byte[2048];
            uint handle = Marshal.GetDelegateForFunctionPointer<GetAuthSessionTicketFn>(fn)(user, buf, buf.Length, out uint len, IntPtr.Zero);
            if (handle == 0 || len == 0 || len > buf.Length) { error = $"ticket invalide (handle={handle}, len={len})"; return null; }

            var sb = new StringBuilder((int)len * 2);
            for (int i = 0; i < len; i++) sb.Append(buf[i].ToString("X2"));
            return sb.ToString();
        }
    }
}
