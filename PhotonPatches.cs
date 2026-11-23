using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppExitGames.Client.Photon;
using Il2CppInterop.Runtime;
using Il2CppPhoton.Realtime;
using MelonLoader;

namespace ZeroHourCrossPlay
{
    /// <summary>
    /// Patches sur la couche "peer" de Photon Realtime : c'est le dernier point avant l'envoi
    /// des opérations sur le réseau, donc indépendant du code (obfusqué) du jeu.
    /// </summary>
    internal static class PhotonPatches
    {
        internal static string LastOriginalAppVersion;
        internal static string LastSentAppVersion;
        internal static string LastAppId;
        internal static string LastRegion;
        internal static int LastAuthType = -1;

        private static MelonLogger.Instance Log => CrossPlayMod.Log;
        private static bool Diag => Config.Diagnostics.Value;

        // ---------------------------------------------------------------- Auth

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpAuthenticate))]
        private static class OpAuthenticatePatch
        {
            private static void Prefix(ref string appId, ref string appVersion, AuthenticationValues authValues, ref string regionCode)
                => RewriteAuth("OpAuthenticate", ref appId, ref appVersion, authValues, ref regionCode);
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpAuthenticateOnce))]
        private static class OpAuthenticateOncePatch
        {
            private static void Prefix(ref string appId, ref string appVersion, AuthenticationValues authValues, ref string regionCode)
                => RewriteAuth("OpAuthenticateOnce", ref appId, ref appVersion, authValues, ref regionCode);
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpGetRegions))]
        private static class OpGetRegionsPatch
        {
            private static void Prefix(ref string appId)
            {
                if (Config.Enabled.Value && !string.IsNullOrWhiteSpace(Config.AppId.Value))
                    appId = Config.AppId.Value.Trim();
            }
        }

        private static void RewriteAuth(string op, ref string appId, ref string appVersion, AuthenticationValues authValues, ref string regionCode)
        {
            try
            {
                var origVersion = appVersion;
                var origAppId = appId;
                var origRegion = regionCode;
                int origAuth = authValues != null ? (int)authValues.AuthType : -1;

                if (Config.Enabled.Value)
                {
                    appVersion = Config.NormalizeAppVersion(appVersion);

                    if (!string.IsNullOrWhiteSpace(Config.AppId.Value))
                        appId = Config.AppId.Value.Trim();

                    // regionCode n'est renseigné que lors de l'auth sur le NameServer.
                    if (!string.IsNullOrEmpty(regionCode) && !string.IsNullOrWhiteSpace(Config.Region.Value))
                        regionCode = Config.Region.Value.Trim().ToLowerInvariant();

                    if (authValues != null && Config.AuthTypeOverride.Value >= 0)
                        authValues.AuthType = (CustomAuthenticationType)(byte)Config.AuthTypeOverride.Value;
                }

                LastOriginalAppVersion = origVersion;
                LastSentAppVersion = appVersion;
                LastAppId = appId;
                if (!string.IsNullOrEmpty(regionCode)) LastRegion = regionCode;
                LastAuthType = authValues != null ? (int)authValues.AuthType : -1;

                Log.Msg($"[Auth] {op}: appId={Mask(origAppId)}{(origAppId != appId ? " -> " + Mask(appId) : "")}" +
                        $" | appVersion='{origVersion}'{(origVersion != appVersion ? " -> '" + appVersion + "'" : "")}" +
                        $" | region='{origRegion}'{(origRegion != regionCode ? " -> '" + regionCode + "'" : "")}" +
                        $" | authType={origAuth}{(origAuth != LastAuthType ? " -> " + LastAuthType : "")}" +
                        (Diag && authValues != null ? $" | authParams='{Truncate(authValues.AuthGetParameters, 200)}' userId='{authValues.UserId}'" : ""));
            }
            catch (Exception e) { Log.Error("RewriteAuth: " + e); }
        }

        // ---------------------------------------------------------------- Lobby

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpJoinLobby))]
        private static class OpJoinLobbyPatch
        {
            private static void Prefix(TypedLobby lobby)
            {
                try
                {
                    if (lobby == null) { if (Diag) Log.Msg("[Lobby] OpJoinLobby(default)"); return; }
                    var before = lobby.Name;
                    var after = Config.NormalizeLobbyName(before);
                    if (after != before) lobby.Name = after;
                    Log.Msg($"[Lobby] OpJoinLobby name='{before}'{(after != before ? " -> '" + after + "'" : "")} type={lobby.Type}");
                }
                catch (Exception e) { Log.Error("OpJoinLobby: " + e); }
            }
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpGetGameList))]
        private static class OpGetGameListPatch
        {
            private static void Prefix(TypedLobby lobby, ref string queryData)
            {
                try
                {
                    NormalizeLobby(lobby, "OpGetGameList");
                    var before = queryData;
                    queryData = StripSql(queryData);
                    Log.Msg($"[Lobby] OpGetGameList sql='{before}'{(before != queryData ? " -> '" + queryData + "'" : "")}");
                }
                catch (Exception e) { Log.Error("OpGetGameList: " + e); }
            }
        }

        // ---------------------------------------------------------------- Rooms

        // Pendant un F10, le jeu réagit à la connexion Photon en lançant SA recherche de partie
        // (partie publique). On bloque alors toute opération de salle qui n'est pas la nôtre.
        internal static string GuardRoom;          // salle visée par F10
        internal static long GuardUntil;           // Environment.TickCount64
        internal static bool Guarding => GuardRoom != null && Environment.TickCount64 < GuardUntil;

        private static bool Block(string op, string detail)
        {
            Log.Msg($"[Garde F10] {op} du jeu bloqué ({detail}) — on attend la salle '{GuardRoom}'.");
            return false;
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpCreateRoom))]
        private static class OpCreateRoomPatch
        {
            private static bool Prefix(EnterRoomParams opParams, ref bool __result)
            {
                LogEnter("OpCreateRoom", opParams);
                if (Guarding) { __result = false; return Block("OpCreateRoom", opParams?.RoomName); }
                return true;
            }
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpJoinRoom))]
        private static class OpJoinRoomPatch
        {
            private static bool Prefix(EnterRoomParams opParams, ref bool __result)
            {
                LogEnter("OpJoinRoom", opParams);
                if (Guarding && opParams?.RoomName != GuardRoom) { __result = false; return Block("OpJoinRoom", opParams?.RoomName); }
                return true;
            }
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpJoinRandomRoom))]
        private static class OpJoinRandomRoomPatch
        {
            private static bool Prefix(OpJoinRandomRoomParams opJoinRandomRoomParams, ref bool __result)
            {
                FixRandom("OpJoinRandomRoom", opJoinRandomRoomParams);
                if (Guarding) { __result = false; return Block("OpJoinRandomRoom", "matchmaking"); }
                return true;
            }
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpJoinRandomOrCreateRoom))]
        private static class OpJoinRandomOrCreateRoomPatch
        {
            private static bool Prefix(OpJoinRandomRoomParams opJoinRandomRoomParams, EnterRoomParams createRoomParams, ref bool __result)
            {
                FixRandom("OpJoinRandomOrCreateRoom", opJoinRandomRoomParams);
                LogEnter("OpJoinRandomOrCreateRoom(create)", createRoomParams);
                if (Guarding) { __result = false; return Block("OpJoinRandomOrCreateRoom", "matchmaking"); }
                return true;
            }
        }

        private static void FixRandom(string op, OpJoinRandomRoomParams p)
        {
            try
            {
                if (p == null) { Log.Msg($"[Room] {op}(null)"); return; }
                NormalizeLobby(p.TypedLobby, op);
                var removed = StripKeys(p.ExpectedCustomRoomProperties);
                var sqlBefore = p.SqlLobbyFilter;
                var sqlAfter = StripSql(sqlBefore);
                if (sqlAfter != sqlBefore) p.SqlLobbyFilter = sqlAfter;

                Log.Msg($"[Room] {op}: expected={Fmt(p.ExpectedCustomRoomProperties)}" +
                        (removed.Count > 0 ? $" (retiré: {string.Join(",", removed)})" : "") +
                        $" maxPlayers={p.ExpectedMaxPlayers} mode={p.MatchingType} lobby={LobbyStr(p.TypedLobby)}" +
                        $" sql='{sqlBefore}'{(sqlAfter != sqlBefore ? " -> '" + sqlAfter + "'" : "")}");
            }
            catch (Exception e) { Log.Error(op + ": " + e); }
        }

        private static void LogEnter(string op, EnterRoomParams p)
        {
            try
            {
                if (p == null) { Log.Msg($"[Room] {op}(null)"); return; }
                NormalizeLobby(p.Lobby, op);
                var sb = new StringBuilder();
                sb.Append($"[Room] {op}: name='{p.RoomName}' lobby={LobbyStr(p.Lobby)} joinMode={p.JoinMode}");
                var o = p.RoomOptions;
                if (o != null)
                {
                    sb.Append($" visible={o.IsVisible} open={o.IsOpen} max={o.MaxPlayers}");
                    if (Diag)
                    {
                        sb.Append($" props={Fmt(o.CustomRoomProperties)}");
                        if (o.CustomRoomPropertiesForLobby != null)
                            sb.Append(" lobbyProps=[" + string.Join(",", ToList(o.CustomRoomPropertiesForLobby)) + "]");
                    }
                }
                if (Diag) sb.Append($" playerProps={Fmt(p.PlayerProperties)}");
                Log.Msg(sb.ToString());
            }
            catch (Exception e) { Log.Error(op + ": " + e); }
        }

        // ---------------------------------------------------------------- Propriétés (diagnostic)

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpSetPropertiesOfActor))]
        private static class OpSetPropsActorPatch
        {
            private static void Prefix(int actorNr, Hashtable actorProperties)
            {
                if (Diag) Log.Msg($"[Props] Actor {actorNr}: {Fmt(actorProperties)}");
            }
        }

        [HarmonyPatch(typeof(LoadBalancingPeer), nameof(LoadBalancingPeer.OpSetPropertiesOfRoom))]
        private static class OpSetPropsRoomPatch
        {
            private static void Prefix(Hashtable gameProperties)
            {
                if (Diag) Log.Msg($"[Props] Room: {Fmt(gameProperties)}");
            }
        }

        // ---------------------------------------------------------------- Réponses serveur (diagnostic)

        [HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnOperationResponse))]
        private static class OnOperationResponsePatch
        {
            private static void Prefix(OperationResponse operationResponse)
            {
                try
                {
                    if (operationResponse == null) return;
                    var code = operationResponse.OperationCode;
                    // 230 Authenticate, 231 AuthOnce, 227 CreateGame, 226 JoinGame, 225 JoinRandom
                    bool interesting = code == 230 || code == 231 || code == 227 || code == 226 || code == 225;
                    // 32755 = CustomAuthenticationFailed (ticket Steam/Epic refusé)
                    if ((code == 230 || code == 231) && operationResponse.ReturnCode == 32755 && CrossPlayMod.ModConnecting)
                        CrossPlayMod.AuthFailed = true;
                    // Échec du JoinRoom lancé par F10 : on lève la garde et on prévient le joueur.
                    if (code == 226 && operationResponse.ReturnCode != 0 && GuardRoom != null)
                    {
                        CrossPlayMod.JoinError = operationResponse.ReturnCode == 32758
                            ? $"Salle '{GuardRoom}' introuvable : mauvais nom, région ou version différente (comparez la ligne AppVersion du panneau F8)."
                            : operationResponse.ReturnCode == 32765 ? $"Salle '{GuardRoom}' pleine."
                            : operationResponse.ReturnCode == 32764 ? $"Salle '{GuardRoom}' fermée par l'hôte."
                            : $"Impossible de rejoindre '{GuardRoom}' : erreur {operationResponse.ReturnCode} {operationResponse.DebugMessage}";
                        GuardRoom = null;
                    }
                    if (operationResponse.ReturnCode != 0)
                        Log.Warning($"[Réponse] op={code} ERREUR {operationResponse.ReturnCode}: {operationResponse.DebugMessage}");
                    else if (Diag && interesting)
                        Log.Msg($"[Réponse] op={code} OK");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnEvent))]
        private static class OnEventPatch
        {
            private static int _listLogs;
            private static void Prefix(EventData photonEvent)
            {
                try
                {
                    if (!Diag || photonEvent == null) return;
                    var c = photonEvent.Code;
                    // 230 GameList (complète), 229 GameListUpdate
                    if (c == 230 || (c == 229 && _listLogs < 50))
                    {
                        _listLogs++;
                        Log.Msg($"[Liste] event {c}: {Truncate(photonEvent.ToStringFull(), 3000)}");
                    }
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- Helpers

        private static void NormalizeLobby(TypedLobby lobby, string op)
        {
            if (lobby == null) return;
            var before = lobby.Name;
            var after = Config.NormalizeLobbyName(before);
            if (after != before)
            {
                lobby.Name = after;
                Log.Msg($"[Lobby] {op}: lobby '{before}' -> '{after}'");
            }
        }

        private static List<string> StripKeys(Hashtable table)
        {
            var removed = new List<string>();
            if (table == null) return removed;
            var toRemove = new List<Il2CppSystem.Object>();
            foreach (var k in table.Keys)
                if (k != null && Config.ShouldStripKey(Str(k))) toRemove.Add(k);
            foreach (var k in toRemove)
            {
                removed.Add(Str(k));
                table.Remove(k);
            }
            return removed;
        }

        // Supprime d'un filtre SQL de lobby les clauses portant sur des clés filtrées.
        private static string StripSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql) || !Config.Enabled.Value) return sql;
            var clauses = Regex.Split(sql, @"\s+AND\s+", RegexOptions.IgnoreCase);
            var kept = clauses.Where(c =>
            {
                var m = Regex.Match(c.Trim().TrimStart('('), @"^([A-Za-z0-9_]+)");
                return !(m.Success && Config.ShouldStripKey(m.Groups[1].Value));
            }).ToList();
            return kept.Count == clauses.Length ? sql : string.Join(" AND ", kept);
        }

        /// <summary>Convertit un objet Il2Cpp "boxé" (string, byte, int, bool...) en texte lisible.</summary>
        internal static string Str(Il2CppSystem.Object o)
        {
            if (o == null) return "null";
            try
            {
                var type = o.GetIl2CppType().FullName;
                switch (type)
                {
                    case "System.String": return IL2CPP.Il2CppStringToManaged(o.Pointer);
                    case "System.Byte": return "b" + o.Unbox<byte>();
                    case "System.Int16": return o.Unbox<short>().ToString();
                    case "System.Int32": return o.Unbox<int>().ToString();
                    case "System.Int64": return o.Unbox<long>().ToString();
                    case "System.Boolean": return o.Unbox<bool>().ToString();
                    case "System.Single": return o.Unbox<float>().ToString();
                    case "System.Double": return o.Unbox<double>().ToString();
                    default:
                        var s = o.ToString();
                        return s == "Il2CppSystem.Object" ? "<" + type + ">" : s;
                }
            }
            catch { return "?"; }
        }

        internal static string Fmt(Hashtable t)
        {
            if (t == null) return "null";
            try
            {
                var parts = new List<string>();
                foreach (var k in t.Keys)
                {
                    var v = t[k];
                    parts.Add($"{Str(k)}={Str(v)}");
                }
                return "{" + string.Join(", ", parts) + "}";
            }
            catch { return t.ToString(); }
        }

        private static IEnumerable<string> ToList(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray a)
        {
            for (int i = 0; i < a.Length; i++) yield return a[i];
        }

        private static string LobbyStr(TypedLobby l) => l == null ? "default" : $"'{l.Name}'/{l.Type}";

        private static string Mask(string appId) =>
            string.IsNullOrEmpty(appId) || appId.Length < 12 ? appId : appId.Substring(0, 8) + "…" + appId.Substring(appId.Length - 4);

        private static string Truncate(string s, int n) => s == null ? null : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
