using System;
using System.Linq;
using System.Text.RegularExpressions;
using MelonLoader;

namespace ZeroHourCrossPlay
{
    /// <summary>
    /// Réglages stockés dans UserData/MelonPreferences.cfg, section [ZeroHourCrossPlay].
    /// Les DEUX joueurs (Steam et Epic) doivent utiliser les mêmes valeurs.
    /// </summary>
    internal static class Config
    {
        private static MelonPreferences_Category _cat;

        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<string> AppVersion;
        public static MelonPreferences_Entry<string> AppId;
        public static MelonPreferences_Entry<string> Region;
        public static MelonPreferences_Entry<int> AuthTypeOverride;
        public static MelonPreferences_Entry<string> LobbyName;
        public static MelonPreferences_Entry<string> StripFilterKeys;
        public static MelonPreferences_Entry<bool> Diagnostics;
        public static MelonPreferences_Entry<string> OverlayKey;
        public static MelonPreferences_Entry<bool> ForceSceneSync;

        // Mots qui marquent généralement une build spécifique à une plateforme.
        private static readonly Regex PlatformTokens = new Regex(
            @"(?i)[\s_\-\.]*(epic\s*games|epicgames|epic|egs|eos|steamworks|steam)[\s_\-\.]*",
            RegexOptions.Compiled);

        public static void Init()
        {
            _cat = MelonPreferences.CreateCategory("ZeroHourCrossPlay", "Zero Hour Cross-Play");

            Enabled = _cat.CreateEntry("Enabled", true, "Activer",
                "Active les modifications de matchmaking cross-plateforme.");

            AppVersion = _cat.CreateEntry("PhotonVersion", "ZeroHourCrossPlay_2.51", "AppVersion Photon",
                "Version Photon envoyée au serveur. Photon ne met en relation que les clients ayant la MÊME AppVersion.\n" +
                "  ZeroHourCrossPlay_2.51 (défaut) = espace commun à tous les joueurs ayant le mod, quelle que soit la\n" +
                "        version Steam/Epic du jeu. Vous ne verrez plus les parties des joueurs sans mod.\n" +
                "  auto  = retire les marqueurs de plateforme (steam/epic/egs/eos) de la version d'origine\n" +
                "  keep  = ne rien changer (jeu normal)\n" +
                "  autre = valeur forcée — identique chez les deux joueurs");

            AppId = _cat.CreateEntry("AppId", "", "AppId Photon",
                "Vide = AppId d'origine. Si les builds Steam et Epic utilisent des AppId Photon différents,\n" +
                "mettez ici le même AppId chez les deux joueurs (voir la ligne [Auth] du log).");

            Region = _cat.CreateEntry("Region", "eu", "Région Photon",
                "Région forcée (eu, us, usw, asia, jp, au, sa, cae, kr, in, ru, za, tr...). Vide = choix du jeu.\n" +
                "Les deux joueurs doivent être sur la même région.");

            AuthTypeOverride = _cat.CreateEntry("AuthTypeOverride", -1, "Type d'auth Photon",
                "-1 = inchangé. 255 = aucune (anonyme), 1 = Steam, 13 = Epic, 0 = Custom.\n" +
                "À utiliser seulement si l'AppId forcé refuse l'authentification de l'autre plateforme.");

            LobbyName = _cat.CreateEntry("LobbyName", "auto", "Nom du lobby Photon",
                "auto = retire les marqueurs de plateforme du nom de lobby, keep = inchangé, autre = nom forcé.");

            StripFilterKeys = _cat.CreateEntry("StripFilterKeys", "auto", "Filtres retirés",
                "Clés de propriétés de salle retirées des FILTRES de matchmaking (JoinRandom / filtre SQL).\n" +
                "auto = toute clé contenant platform/plat/store/steam/epic/eos. Liste séparée par des virgules sinon. Vide = rien.");

            Diagnostics = _cat.CreateEntry("Diagnostics", true, "Journal détaillé",
                "Écrit dans le log MelonLoader toutes les opérations Photon (auth, création/connexion de salle, listes de salles).");

            ForceSceneSync = _cat.CreateEntry("ForceSceneSync", true, "Synchro de scène",
                "Avant de rejoindre une salle par F10, active PhotonNetwork.AutomaticallySyncScene pour charger la carte de l'hôte.");

            OverlayKey = _cat.CreateEntry("OverlayKey", "F8", "Touche overlay",
                "Touche qui affiche/masque le panneau Cross-Play.");
        }

        public static string NormalizeAppVersion(string original)
        {
            var mode = (AppVersion.Value ?? "").Trim();
            if (!Enabled.Value || original == null || mode.Equals("keep", StringComparison.OrdinalIgnoreCase))
                return original;
            if (mode.Length == 0 || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return StripPlatform(original);
            return mode;
        }

        public static string NormalizeLobbyName(string original)
        {
            var mode = (LobbyName.Value ?? "").Trim();
            if (!Enabled.Value || mode.Equals("keep", StringComparison.OrdinalIgnoreCase))
                return original;
            if (mode.Length == 0 || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(original) ? original : StripPlatform(original);
            return mode;
        }

        public static string StripPlatform(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Remplace chaque marqueur par un séparateur unique, puis nettoie.
            var r = PlatformTokens.Replace(s, "_");
            r = Regex.Replace(r, "_{2,}", "_").Trim('_', '-', '.', ' ');
            return r.Length == 0 ? s : r;
        }

        public static bool ShouldStripKey(string key)
        {
            if (!Enabled.Value || string.IsNullOrEmpty(key)) return false;
            var v = (StripFilterKeys.Value ?? "").Trim();
            if (v.Length == 0) return false;
            if (v.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var k = key.ToLowerInvariant();
                return k.Contains("platform") || k == "plat" || k.Contains("store")
                       || k.Contains("steam") || k.Contains("epic") || k.Contains("eos");
            }
            return v.Split(',').Select(x => x.Trim()).Any(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
