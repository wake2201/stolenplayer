// N1.2 F8 multiplayer session lifecycle (Unity-free; the plugin glue in src/Plugin/MultiplayerMenu.cs feeds it real game
// facts and executes the actions it returns). Rules:
//  - Host only from a loaded, playable single-player world; never from the main menu.
//  - Join only from the original main menu; never from an active solo world (no live migration).
//  - Save isolation is armed only after authentication + host readiness and before any client gameplay message or
//    scene load; once armed it stays armed for the rest of the process (solo play again = restart the game).
//  - The client never loads into the world before ClientReady was sent and the host's world snapshot arrived.
using System;

namespace TS2MP.Shared
{
    public enum MpState { Offline, HostStarting, HostReady, HostWaiting, ClientConnecting, ClientPreparing, ClientSynchronizing, Connected, Disconnecting, Error }

    public enum GameContext { Unknown, MainMenu, Loading, InWorld }

    // Real game state sampled by the plugin for the host readiness gate.
    public struct HostFacts
    {
        public GameContext Context;
        public bool LoadedMap;          // GameManager.loadedMap (ContinueButtonActivate finished)
        public bool PlayerPresent;      // GameManager.PS != null
        public bool ProfileValid;       // PlayerPrefs Profile 0..4 and FBPP initialised on the real save file
        public bool RegionKnown;        // WorldSync resolved the region (expected additive scene loaded)
        public bool PersistenceReady;   // Player2Sync host book bound to this profile (p2inv loaded)
        public bool SystemsReady;       // session systems constructed without fatal init errors
        public double SecondsSinceSceneLoad;
    }

    public static class HostReadiness
    {
        public const double SettleSeconds = 3.0;

        // Null = gameplay-ready; otherwise a short German reason (shown in the F8 menu, logged).
        public static string Check(HostFacts f)
        {
            if (f.Context == GameContext.MainMenu) return "Hauptmenü – lade zuerst deinen Spielstand";
            if (f.Context != GameContext.InWorld) return "Ladevorgang läuft";
            if (!f.LoadedMap || !f.PlayerPresent) return "Welt wird noch geladen";
            if (!f.ProfileValid) return "kein gültiger Spielstand geladen";
            if (f.SecondsSinceSceneLoad < SettleSeconds) return "Welt wird noch initialisiert";
            if (!f.SystemsReady) return "Mehrspieler-Systeme nicht bereit";
            if (!f.RegionKnown) return "Region noch nicht erkannt";
            if (!f.PersistenceReady) return "Speicherdaten von Spieler 2 noch nicht geladen";
            return null;
        }
    }

    public struct Availability
    {
        public bool Enabled; public string Reason;
        public static Availability Yes { get { return new Availability { Enabled = true }; } }
        public static Availability No(string why) { return new Availability { Enabled = false, Reason = why }; }
    }

    public static class MenuRules
    {
        public const string JoinFromWorldText = "Um einem Multiplayer-Spiel beizutreten, musst du zuerst zum Hauptmenü zurückkehren.";
        public const string HostFromMenuText = "Hosten ist erst möglich, wenn dein Einzelspieler-Spielstand vollständig geladen ist.";
        public const string ClientModeStickyText = "Dieses Spiel lief bereits als Spieler 2 (geschützter Speicherbereich). Für Einzelspieler oder Hosten bitte das Spiel neu starten.";
        public const string LegacyText = "Vom Entwickler-Launcher gestartet – Rolle ist festgelegt.";
        public const string IsolationUnavailableText = "Speicherschutz für Spieler 2 ist nicht verfügbar (siehe Log). Beitreten ist deaktiviert.";

        public static bool Busy(MpState s) { return s != MpState.Offline && s != MpState.Error; }

        public static Availability Host(GameContext ctx, MpState s, bool legacy, bool clientModeSticky)
        {
            if (legacy) return Availability.No(LegacyText);
            if (clientModeSticky) return Availability.No(ClientModeStickyText);
            if (Busy(s)) return Availability.No("Es läuft bereits eine Mehrspieler-Sitzung.");
            if (ctx != GameContext.InWorld) return Availability.No(HostFromMenuText);
            return Availability.Yes;
        }

        public static Availability Join(GameContext ctx, MpState s, bool legacy, bool isolationAvailable)
        {
            if (legacy) return Availability.No(LegacyText);
            if (ctx == GameContext.InWorld && (s == MpState.Offline || s == MpState.Error)) return Availability.No(JoinFromWorldText);
            if (Busy(s)) return Availability.No("Es läuft bereits eine Mehrspieler-Sitzung.");
            if (ctx != GameContext.MainMenu) return Availability.No(JoinFromWorldText);
            if (!isolationAvailable) return Availability.No(IsolationUnavailableText);
            return Availability.Yes;
        }
    }

    public sealed class JoinRequest { public string Host; public int Port; public string Code; }

    public enum ClientAction { None, ArmIsolation, BuildSystemsAndConfirm, ContinueGame, Abort, GiveUp }

    public sealed class Lifecycle
    {
        public MpState State { get; private set; }
        public string Status { get; private set; }     // German, for the menu
        public string Error { get; private set; }
        public bool ClientModeSticky { get; private set; }   // save isolation armed in this process
        public bool Legacy;                                   // launcher dev mode: role fixed at startup
        public double ConnectTimeoutSeconds = 30, SyncTimeoutSeconds = 30, HostWaitTimeoutSeconds = 180;
        double _since; bool _continued, _hostWaitSeen; string _lastReason;

        public Lifecycle() { State = MpState.Offline; Status = "Offline"; }

        void Set(MpState s, string status, double now) { State = s; Status = status; _since = now; }

        public void Fail(string error, double now) { Error = error; Set(MpState.Error, "Fehler: " + error, now); }
        public void ClearError() { if (State == MpState.Error) { Error = null; State = MpState.Offline; Status = "Offline"; } }

        // ---- host ----
        public string RequestHost(GameContext ctx, int port, double now)
        {
            var a = MenuRules.Host(ctx, State, Legacy, ClientModeSticky);
            if (!a.Enabled) return a.Reason;
            if (!NetConfig.ValidPort(port)) return "Ungültiger Port (1024–65535).";
            Error = null;
            Set(MpState.HostStarting, "Host wird gestartet …", now);
            return null;
        }

        public void HostStarted(bool ok, string error, double now)
        {
            if (State != MpState.HostStarting) return;
            if (!ok) { Fail(error ?? "Host konnte nicht gestartet werden", now); return; }
            Set(MpState.HostWaiting, "Host läuft – Welt wird geprüft …", now);
        }

        // Called every frame while hosting. Returns true when the session must end (host left to the main menu).
        public bool HostTick(GameContext ctx, string readinessReason, bool clientConnected, double now)
        {
            if (State != MpState.HostReady && State != MpState.HostWaiting && State != MpState.Connected) return false;
            if (ctx == GameContext.MainMenu) { Set(MpState.Disconnecting, "Sitzung wird beendet (Hauptmenü) …", now); return true; }
            if (clientConnected) { if (State != MpState.Connected) Set(MpState.Connected, "Spieler 2 ist verbunden", now); }
            else if (readinessReason == null) { if (State != MpState.HostReady) Set(MpState.HostReady, "Bereit – warte auf Spieler 2", now); }
            else if (State != MpState.HostWaiting || !ReferenceEquals(_lastReason, readinessReason)) { _lastReason = readinessReason; Set(MpState.HostWaiting, "Beitritt pausiert: " + readinessReason, now); }
            return false;
        }

        // ---- client ----
        public string RequestJoin(GameContext ctx, bool isolationAvailable, string host, string portText, string codeText, out JoinRequest req, double now)
        {
            req = null;
            var a = MenuRules.Join(ctx, State, Legacy, isolationAvailable);
            if (!a.Enabled) return a.Reason;
            host = (host ?? "").Trim();
            if (!NetConfig.ValidConnectAddress(host)) return "Ungültige Adresse (IPv4 oder DNS-Name, ohne Port).";
            int port;
            if (!NetConfig.TryParsePort(portText, out port)) return "Ungültiger Port (1024–65535).";
            string code = JoinAuth.Normalize(codeText);
            if (code == null) return "Ungültiger Beitrittscode – bitte auf Tippfehler prüfen.";
            req = new JoinRequest { Host = host, Port = port, Code = code };
            Error = null; _continued = false; _hostWaitSeen = false;
            Set(MpState.ClientConnecting, "Verbinde mit " + host + ":" + port + " …", now);
            return null;
        }

        // Called every frame while joining/connected. The caller executes the returned action and reports back with
        // IsolationArmed / IsolationFailed / SystemsConfirmed / GameContinued.
        public ClientAction ClientTick(GameContext ctx, ConnState node, RejectCode? lastReject, string fatal, string hostWait, bool worldSynced, double now)
        {
            switch (State)
            {
                case MpState.ClientConnecting:
                    if (node == ConnState.Failed) { Fail(Explain(fatal, lastReject), now); return ClientAction.GiveUp; }
                    if (node == ConnState.Preparing) { Set(MpState.ClientPreparing, "Verbunden – Speicherschutz wird aktiviert …", now); return ClientAction.ArmIsolation; }
                    if (node == ConnState.Connected && ClientModeSticky) { Set(MpState.ClientSynchronizing, "Weltdaten werden synchronisiert …", now); return ClientAction.None; }
                    if (ctx != GameContext.MainMenu) { Fail("Hauptmenü wurde während des Verbindens verlassen", now); return ClientAction.GiveUp; }
                    if (hostWait != null) { _hostWaitSeen = true; Status = "Host ist noch nicht bereit (lädt) – neuer Versuch …"; }
                    if (now - _since > (_hostWaitSeen ? HostWaitTimeoutSeconds : ConnectTimeoutSeconds))
                    { Fail(_hostWaitSeen ? "Host wurde nicht rechtzeitig bereit" : "Zeitüberschreitung – Host nicht erreichbar (Adresse, Port, Portfreigabe prüfen)", now); return ClientAction.GiveUp; }
                    return ClientAction.None;
                case MpState.ClientPreparing:
                    if (node == ConnState.Failed) { Fail(Explain(fatal, lastReject), now); return ClientAction.GiveUp; }
                    return ClientAction.None;
                case MpState.ClientSynchronizing:
                    if (node == ConnState.Failed) { Fail(Explain(fatal, lastReject), now); return ClientAction.GiveUp; }
                    if (node == ConnState.Connected && worldSynced && ctx == GameContext.MainMenu && !_continued) { _continued = true; return ClientAction.ContinueGame; }
                    if (ctx == GameContext.Loading || ctx == GameContext.InWorld) { Set(MpState.Connected, "Verbunden als Spieler 2", now); return ClientAction.None; }
                    if (!worldSynced && now - _since > SyncTimeoutSeconds) { Fail("Weltdaten vom Host kamen nicht an", now); return ClientAction.Abort; }
                    return ClientAction.None;
                case MpState.Connected:
                    if (node == ConnState.Failed) { Fail(Explain(fatal, lastReject), now); return ClientAction.GiveUp; }
                    Status = node == ConnState.Connected ? "Verbunden als Spieler 2" : "Verbindung unterbrochen – neuer Versuch …";
                    return ClientAction.None;
            }
            return ClientAction.None;
        }

        public void IsolationArmed(double now) { ClientModeSticky = true; if (State == MpState.ClientPreparing) Status = "Speicherschutz aktiv – Systeme werden gestartet …"; }
        public void IsolationFailed(string why, double now) { Fail("Speicherschutz konnte nicht bestätigt werden: " + why, now); }
        public void SystemsConfirmed(double now) { if (State == MpState.ClientPreparing) Set(MpState.ClientSynchronizing, "Weltdaten werden synchronisiert …", now); }

        public void Stopped(double now) { if (State != MpState.Error) Set(MpState.Offline, ClientModeSticky ? "Offline (Spieler-2-Modus bleibt bis zum Neustart aktiv)" : "Offline", now); }
        public void BeginStop(double now) { if (Busy()) Set(MpState.Disconnecting, "Sitzung wird beendet …", now); }
        public bool Busy() { return MenuRules.Busy(State); }
        public bool IsHostState { get { return State == MpState.HostStarting || State == MpState.HostReady || State == MpState.HostWaiting; } }

        public static string Explain(string fatal, RejectCode? reject)
        {
            if (reject.HasValue)
                switch (reject.Value)
                {
                    case RejectCode.AuthFailed: return "Beitrittscode falsch oder veraltet (der Host bekommt bei jedem Start einen neuen Code)";
                    case RejectCode.ProtocolMismatch: case RejectCode.ModVersionMismatch: return "Unterschiedliche Mod-Versionen – beide brauchen dasselbe Release";
                    case RejectCode.GameVersionMismatch: return "Unterschiedliche Spielversionen";
                    case RejectCode.SessionFull: return "Es ist bereits ein Spieler 2 verbunden";
                    case RejectCode.RateLimited: return "Zu viele Fehlversuche – bitte 2 Minuten warten";
                    case RejectCode.HostNotReady: return "Host ist nicht bereit";
                }
            if (fatal != null)
            {
                if (fatal.StartsWith("AUTH_REQUIRED")) return "Host verlangt einen Beitrittscode";
                if (fatal.StartsWith("HOST_NOT_AUTHENTICATED")) return "Gegenstelle ist nicht der erwartete Host (Adresse prüfen)";
                if (fatal.StartsWith("PREPARATION_FAILED")) return "Vorbereitung fehlgeschlagen";
                return "Verbindung abgelehnt";
            }
            return "Verbindung fehlgeschlagen";
        }
    }
}
