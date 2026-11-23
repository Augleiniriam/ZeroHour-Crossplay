using System;
using System.Text;
using Il2CppPhoton.Pun;
using Il2CppPhoton.Realtime;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ZeroHourCrossPlay.CrossPlayMod), "Zero Hour Cross-Play", "1.0.0", "ZeroHour Debloat")]
[assembly: MelonGame("M7 production & Attrito", "Zero Hour")]

namespace ZeroHourCrossPlay
{
    public class CrossPlayMod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        private bool _overlay;
        private KeyCode _overlayKey = KeyCode.F8;
        private string _status = "";
        private float _statusUntil;
        private bool _guiBroken;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Config.Init();
            if (!Enum.TryParse(Config.OverlayKey.Value, true, out _overlayKey)) _overlayKey = KeyCode.F8;

            Log.Msg("Cross-Play Steam <-> Epic chargé.");
            Log.Msg($"  AppVersion={Config.AppVersion.Value} | Region='{Config.Region.Value}' | AppId={(string.IsNullOrWhiteSpace(Config.AppId.Value) ? "(origine)" : "forcé")}" +
                    $" | Lobby={Config.LobbyName.Value} | Filtres retirés={Config.StripFilterKeys.Value}");
            Log.Msg($"  {_overlayKey} = panneau | F9 = copier le nom de la salle | F10 = rejoindre la salle du presse-papier");
        }

        public override void OnUpdate()
        {
            try
            {
                if (Input.GetKeyDown(_overlayKey)) _overlay = !_overlay;
                if (Input.GetKeyDown(KeyCode.F9)) CopyRoomName();
                if (Input.GetKeyDown(KeyCode.F10)) JoinFromClipboard();
                TickPendingJoin();
            }
            catch (Exception e)
            {
                Log.Error("Input: " + e.Message);
            }
        }

        // ------------------------------------------------------------ Actions

        private void CopyRoomName()
        {
            var room = PhotonNetwork.CurrentRoom;
            if (room == null) { SetStatus("Pas dans une salle Photon."); return; }
            GUIUtility.systemCopyBuffer = room.Name;
            SetStatus($"Nom de salle copié : {room.Name}");
        }

        private void JoinFromClipboard()
        {
            var name = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (name.Length == 0) { SetStatus("Presse-papier vide."); return; }
            if (name.Length > 128 || name.Contains('\n')) { SetStatus("Le presse-papier ne contient pas un nom de salle."); return; }
            if (PhotonNetwork.InRoom) { SetStatus("Déjà dans une salle : quittez-la d'abord."); return; }

            _pendingRoom = name;
            _pendingSince = Time.realtimeSinceStartup;
            JoinError = null;
            PhotonPatches.GuardRoom = name;
            PhotonPatches.GuardUntil = Environment.TickCount64 + 45_000;
            var state = PhotonNetwork.NetworkClientState;
            if (state == ClientState.ConnectedToMasterServer || state == ClientState.JoinedLobby)
            {
                DoJoin();
                return;
            }
            if (state == ClientState.PeerCreated || state == ClientState.Disconnected)
            {
                // Le jeu ne se connecte à Photon qu'au lancement d'une mission : on le fait nous-mêmes.
                _authAttempt = 0;
                StartModConnect();
                return;
            }
            SetStatus($"Connexion Photon en cours ({state}), la salle '{name}' sera rejointe ensuite.");
        }

        private string _pendingRoom;
        private float _pendingSince;
        private int _authAttempt;          // 0 = auth plateforme fraîche, 1 = anonyme
        private float _connectAt = -1f;    // connexion différée (le ticket Steam doit être validé côté Steam)

        /// <summary>Vrai pendant une connexion lancée par le mod (pour réagir aux échecs d'auth).</summary>
        internal static bool ModConnecting;
        internal static volatile bool AuthFailed;

        private void StartModConnect()
        {
            if (string.IsNullOrEmpty(PhotonNetwork.NickName))
                PhotonNetwork.NickName = "Player";
            AuthFailed = false;
            ModConnecting = true;

            if (_authAttempt == 0 && SteamTicket.IsAvailable)
            {
                var hex = SteamTicket.GetHex(out var err);
                if (hex != null)
                {
                    var av = new AuthenticationValues();
                    av.AuthType = CustomAuthenticationType.Steam;
                    av.AddAuthParameter("ticket", hex);
                    PhotonNetwork.AuthValues = av;
                    _connectAt = Time.realtimeSinceStartup + 2f; // laisse Steam enregistrer le ticket
                    SetStatus("Ticket Steam neuf généré, connexion à Photon…");
                    return;
                }
                Log.Warning("Ticket Steam impossible : " + err + " — on garde l'auth du jeu.");
            }
            else if (_authAttempt == 1)
            {
                var av = new AuthenticationValues();
                av.AuthType = CustomAuthenticationType.None;
                PhotonNetwork.AuthValues = av;
                SetStatus("Auth plateforme refusée, nouvel essai en anonyme…");
            }
            _connectAt = Time.realtimeSinceStartup;
        }

        internal static volatile string JoinError;

        private void TickPendingJoin()
        {
            if (JoinError != null) { SetStatus(JoinError); JoinError = null; }
            if (PhotonPatches.GuardRoom != null && (PhotonNetwork.InRoom || !PhotonPatches.Guarding))
            {
                if (PhotonNetwork.InRoom) SetStatus($"Salle rejointe : {PhotonNetwork.CurrentRoom?.Name}");
                PhotonPatches.GuardRoom = null;
            }
            if (_pendingRoom == null) { ModConnecting = false; return; }
            if (PhotonNetwork.InRoom) { _pendingRoom = null; ModConnecting = false; return; }
            if (Time.realtimeSinceStartup - _pendingSince > 45f)
            {
                SetStatus($"Délai dépassé pour rejoindre '{_pendingRoom}' (état {PhotonNetwork.NetworkClientState}). Voir le log.");
                _pendingRoom = null;
                ModConnecting = false;
                return;
            }

            var state = PhotonNetwork.NetworkClientState;

            if (_connectAt >= 0 && Time.realtimeSinceStartup >= _connectAt
                && (state == ClientState.PeerCreated || state == ClientState.Disconnected))
            {
                _connectAt = -1f;
                bool ok = PhotonNetwork.ConnectUsingSettings();
                if (!ok) { SetStatus("ConnectUsingSettings refusé."); _pendingRoom = null; ModConnecting = false; }
                else SetStatus($"Connexion à Photon… puis salle '{_pendingRoom}'.");
                return;
            }

            if (AuthFailed && state == ClientState.Disconnected && _connectAt < 0)
            {
                AuthFailed = false;
                if (_authAttempt == 0)
                {
                    _authAttempt = 1;
                    StartModConnect();
                }
                else
                {
                    SetStatus("Authentification Photon refusée même en anonyme. Envoie le log MelonLoader.");
                    _pendingRoom = null;
                    ModConnecting = false;
                }
                return;
            }

            if (state == ClientState.ConnectedToMasterServer || state == ClientState.JoinedLobby)
                DoJoin();
        }

        private void DoJoin()
        {
            var name = _pendingRoom;
            _pendingRoom = null;
            ModConnecting = false;
            if (Config.ForceSceneSync.Value)
                PhotonNetwork.AutomaticallySyncScene = true; // charge la carte de l'hôte en arrivant
            bool ok = PhotonNetwork.JoinRoom(name);
            SetStatus(ok ? $"Connexion à la salle '{name}'…" : $"JoinRoom('{name}') refusé par le client.");
        }

        private void SetStatus(string s)
        {
            _status = s;
            _statusUntil = Time.realtimeSinceStartup + 8f;
            Log.Msg("[Overlay] " + s);
        }

        // ------------------------------------------------------------ Overlay (IMGUI, sans GUILayout)

        public override void OnGUI()
        {
            if (!_overlay || _guiBroken) return;
            try
            {
                const float w = 460f, x = 20f, line = 22f;
                float y = 20f;
                GUI.Box(new Rect(x - 8, y - 8, w + 16, 330), "");

                Label(ref y, x, w, line, "<b>Zero Hour Cross-Play</b>  (Steam ⇄ Epic)");
                Label(ref y, x, w, line, $"État : {PhotonNetwork.NetworkClientState}   Région : {PhotonNetwork.CloudRegion ?? "-"}");
                Label(ref y, x, w, line, $"AppVersion : {PhotonPatches.LastOriginalAppVersion ?? "?"} → {PhotonPatches.LastSentAppVersion ?? "?"}");
                Label(ref y, x, w, line, $"Auth : type {PhotonPatches.LastAuthType}   Pseudo : {PhotonNetwork.NickName}");

                var room = PhotonNetwork.CurrentRoom;
                if (room != null)
                {
                    Label(ref y, x, w, line, $"Salle : <b>{room.Name}</b>  ({room.PlayerCount}/{room.MaxPlayers})");
                    var sb = new StringBuilder();
                    var players = PhotonNetwork.PlayerList;
                    for (int i = 0; players != null && i < players.Length; i++)
                    {
                        var p = players[i];
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(p.NickName).Append(p.IsMasterClient ? " (hôte)" : "");
                    }
                    Label(ref y, x, w, line * 2, "Joueurs : " + sb);
                    y += line;
                }
                else
                {
                    Label(ref y, x, w, line, "Salle : aucune");
                }

                y += 6;
                if (GUI.Button(new Rect(x, y, 220, 28), "Copier nom de salle (F9)")) CopyRoomName();
                if (GUI.Button(new Rect(x + 230, y, 230, 28), "Rejoindre presse-papier (F10)")) JoinFromClipboard();
                y += 34;
                if (GUI.Button(new Rect(x, y, 220, 28), "Copier AppId Photon"))
                {
                    GUIUtility.systemCopyBuffer = PhotonPatches.LastAppId ?? "";
                    SetStatus("AppId copié dans le presse-papier.");
                }
                y += 34;

                if (Time.realtimeSinceStartup < _statusUntil)
                    Label(ref y, x, w, line * 2, _status);
            }
            catch (Exception e)
            {
                _guiBroken = true;
                Log.Warning("Overlay IMGUI indisponible (" + e.Message + "). Utilisez F9 / F10.");
            }
        }

        private static void Label(ref float y, float x, float w, float h, string text)
        {
            GUI.Label(new Rect(x, y, w, h), text);
            y += h;
        }
    }
}
