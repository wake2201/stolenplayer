// W6A.1: developer menu (F10, only with Developer.EnableDeveloperMenu = true; plain IMGUI, function first). Everything the
// former F8 "multiplayer test campaign" offered, plus a story-step selector and a scenario reset:
//   - Dev Host: prepares the isolated test campaign (W4.2B.2: SaveGuard.PrepareTestHostSave = redirect + guards onto
//     <data>\testcampaign\host-save, a COPY of the real save folder made once), optionally sets the selected story step in that
//     copy, then prepares the host session exactly like F8 (same StartHost path, Welcome carries the test flag).
//   - Dev Join: the normal join path with JoinKind.Developer (MenuRouting.JoinMismatch: only a test-campaign host is accepted;
//     the client then uses the test sandbox, W4.2B.2).
//   - Story step (0..80, StoryCatalog): written as the copy's "Story" / "StoryBackup" keys only while the copy is armed and
//     verified (SaveGuard.Armed + DevHost + RuntimeCheck) and only for steps StoryCatalog does not refuse. The original
//     MainMenu.Continue then loads the copy: StoryManager.BeginStory reads the key and runs InitiateStory for that step - the
//     same path as loading any save at that step (its start effects once, earlier steps never replayed). Player 2 receives
//     the host's campaign (CampaignBootstrap) and the live index (W6A "story" state).
//   - Reset: the whole <data>\testcampaign folder is moved aside to testcampaign.reset-<time> (never deleted) while nothing
//     uses it; the next Dev Host copies the real save again = clean, repeatable starting point.
using System;
using System.IO;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class MultiplayerMenu
    {
        int _devStory = -1, _appliedStory = -1;
        Rect _devRect; Vector2 _devScroll, _stepScroll; string[] _stepLabels; string _devInfo; float _devInfoAt;
        static readonly string[] SupportMark = { "OK ", "~  ", "X  " };

        // ---------------------------------------------------------------- test campaign (moved from the F8 menu, unchanged)
        bool PrepareTestCampaign()
        {
            var a = MenuRules.PrepareTest(Context(), _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _lc.TestPrepared, _worldLoadedOnce);
            if (!a.Enabled) { _hint = a.Reason; return false; }
            string dir = SaveIsolation.TestCampaignDataDir(_settings.DataDir), why;
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { _hint = "Testordner konnte nicht angelegt werden: " + ex.Message; return false; }
            bool wasArmed = SaveGuard.Armed;
            if (!SaveGuard.PrepareTestHostSave(dir, out why))
            {
                _log.Error("TEST_CAMPAIGN save isolation could not be verified: " + why);
                _hint = "Testkampagne konnte nicht sicher vorbereitet werden (siehe Log).";
                if (SaveGuard.Armed || wasArmed) _runner.RequestQuit("SAVE_GUARD_FAILED after arming the test campaign: quitting to protect the real saves (fail closed).");
                return false;
            }
            _runner.EnableTestCampaign(dir, "developer menu host");
            _lc.TestCampaignPrepared(); _hint = null;
            RefreshContinueText();
            _log.Info("TEST_CAMPAIGN prepared in the main menu (developer menu): the original Continue / New Game now loads the isolated copy; the host session is prepared next");
            return true;
        }

        // The main menu's save info was read from the real folder; the original LoadContinueTxt re-reads it (now redirected).
        void RefreshContinueText()
        {
            try
            {
                var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once
                var m = _menuType != null ? AccessTools.Method(_menuType, "LoadContinueTxt") : null;
                if (mm != null && m != null && m.GetParameters().Length == 0) m.Invoke(mm, null);
            }
            catch (Exception ex) { _log.Warn("TEST_CAMPAIGN main-menu save info not refreshed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- story step in the isolated copy
        static Func<string, int> _spGet; static Action<string, int> _spSet; static Action _fbppSave; static bool _spResolved;
        static bool ResolvePrefs()
        {
            if (_spResolved) return _spGet != null && _spSet != null && _fbppSave != null;
            _spResolved = true;
            var sp = AccessTools.TypeByName("SPrefs"); var fbpp = AccessTools.TypeByName("FBPP");
            var g = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
            var s = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
            var sv = fbpp != null ? AccessTools.Method(fbpp, "Save", Type.EmptyTypes) : null;
            if (g != null) _spGet = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), g);
            if (s != null) _spSet = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), s);
            if (sv != null) _fbppSave = (Action)Delegate.CreateDelegate(typeof(Action), sv);
            return _spGet != null && _spSet != null && _fbppSave != null;
        }

        bool ApplyTestStory()
        {
            _appliedStory = -1;
            if (_devStory < 0) { _log.Info("TEST_STORY keeping the copied save's own story step"); return true; }
            bool hasSave = false;
            try { hasSave = _fbppExists != null && (bool)_fbppExists.Invoke(null, null); } catch { }
            if (!ResolvePrefs()) { _hint = "Speicher-API des Spiels nicht gefunden (siehe Log) – Schritt nicht gesetzt."; _log.Error("TEST_STORY SPrefs.GetInt / SetInt / FBPP.Save not found"); return false; }
            string r = MenuRouting.DevStoryRefusal(_devStory, hasSave, hasSave && _spGet("FreeMode") == 1);
            if (r != null) { _hint = r; _log.Warn("TEST_STORY refused: " + r); return false; }
            if (!SaveGuard.Armed || !SaveGuard.DevHost || !SaveGuard.RuntimeCheck())
            {
                _hint = "Speicherschutz der Testkopie nicht bestätigt – Schritt nicht gesetzt (siehe Log).";
                _log.Error("TEST_STORY refused: the active save target is not verified inside the isolated test copy");
                return false;
            }
            var step = StoryCatalog.Get(_devStory);
            int before = _spGet("Story");
            _spSet("Story", _devStory); _spSet("StoryBackup", _devStory); _fbppSave();
            _appliedStory = _devStory;
            RefreshContinueText();
            _log.Info("TEST_STORY isolated copy: Story " + before + " -> " + _devStory + " '" + step.Key + "' (" + step.Support + (step.Reason.Length > 0 ? ": " + step.Reason : "")
                + "); the original Continue loads it through StoryManager.BeginStory -> InitiateStory (this step's start effects once, earlier steps not replayed)");
            return true;
        }

        // ---------------------------------------------------------------- reset
        void ResetScenario()
        {
            var a = MenuRouting.DevReset(Context(), _lc.State, _legacy, SaveGuard.Armed, _worldLoadedOnce);
            if (!a.Enabled) { _hint = a.Reason; return; }
            string dir = SaveIsolation.TestCampaignDataDir(_settings.DataDir), why;
            if (!Directory.Exists(dir)) { _hint = "Kein Testszenario vorhanden – der nächste Test-Host kopiert deinen Spielstand frisch."; return; }
            string dst = SaveIsolation.ResetDestination(dir, SaveGuard.RealDir, SaveGuard.GameDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"), out why);
            if (dst == null) { _hint = "Zurücksetzen verweigert: " + why; _log.Error("TEST_RESET refused: " + why); return; }
            try
            {
                Directory.Move(dir, dst);
                _hint = "Testszenario zurückgesetzt. Der nächste Test-Host kopiert deinen echten Spielstand neu (alter Stand: " + Path.GetFileName(dst) + ").";
                _log.Info("TEST_RESET test campaign folder moved aside (not deleted): " + dir + " -> " + dst + "; the next developer host seeds a fresh copy of the real save");
            }
            catch (Exception ex) { _hint = "Zurücksetzen fehlgeschlagen: " + ex.Message; _log.Error("TEST_RESET move failed", ex); }
        }

        // ---------------------------------------------------------------- window (function first)
        void DrawDeveloper()
        {
            if (_devRect.width < 1) _devRect = new Rect(Mathf.Max(10, (Screen.width - 700) / 2f), Mathf.Max(10, (Screen.height - 760) / 2f), 700, Mathf.Min(760, Screen.height - 20));
            _devRect = GUI.Window(0x7533, _devRect, DevWindow, "TS2MP Entwicklermenü (" + _settings.DevMenuKey + ")");
        }

        void DevWindow(int id)
        {
            var ctx = Context();
            _devScroll = GUILayout.BeginScrollView(_devScroll);
            GUILayout.Label(DevInfo());
            if (_lc.State == MpState.Error && _lc.Error != null) GUILayout.Label("Fehler: " + _lc.Error);
            if (_hint != null) GUILayout.Label(">> " + _hint);
            GUILayout.Space(6);

            if (_legacy) GUILayout.Label(MenuRules.LegacyText);
            else if (_hostCode != null && (_lc.IsHostState || _lc.State == MpState.Connected))
            {
                GUILayout.Label("HOST aktiv auf UDP " + _port + (_runner.TestCampaignActive ? " – TESTKAMPAGNE" : " – normale Kampagne") + (_appliedStory >= 0 ? ", Schritt " + _appliedStory : ""));
                GUILayout.Label("Beitrittscode: " + _hostCode);
                GUILayout.Label("Bereitschaft: " + (_hostGateReason ?? "bereit"));
                if (GUILayout.Button("Code kopieren")) { GUIUtility.systemCopyBuffer = _hostCode; _hint = "Code kopiert."; }
                if (GUILayout.Button("Hosten abbrechen")) CancelHost("cancelled in the developer menu");
            }
            else if (_lc.Busy() && !_lc.IsHostState)
            {
                GUILayout.Label("CLIENT: " + _lc.Status);
                if (GUILayout.Button(_lc.State == MpState.Connected ? "Verbindung trennen" : "Abbrechen")) StopSession("disconnected in the developer menu");
            }
            else
            {
                DevHostSection(ctx);
                GUILayout.Space(10);
                DevJoinSection(ctx);
                GUILayout.Space(10);
                var ra = MenuRouting.DevReset(ctx, _lc.State, _legacy, SaveGuard.Armed, _worldLoadedOnce);
                GUILayout.Label("— Testszenario zurücksetzen —");
                GUILayout.Label("Verschiebt <TS2MP>\\testcampaign (Host-Kopie, Test-Datensätze, Test-Sandbox) nach testcampaign.reset-<Zeit>. Nichts wird gelöscht; echte Spielstände bleiben unberührt.");
                GUI.enabled = ra.Enabled;
                if (GUILayout.Button("Testszenario zurücksetzen")) ResetScenario();
                GUI.enabled = true;
                if (!ra.Enabled) GUILayout.Label(ra.Reason);
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Schließen (" + _settings.DevMenuKey + ")")) CloseMenu("button");
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        void DevHostSection(GameContext ctx)
        {
            GUILayout.Label("— Test-Host (isolierte Kopie deines Spielstands, alle Fähigkeiten, Testgeld) —");
            var a = MenuRules.PrepareTest(ctx, _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _lc.TestPrepared, _worldLoadedOnce);
            GUILayout.BeginHorizontal();
            GUILayout.Label("UDP-Port:", GUILayout.Width(80));
            _portText = Digits(GUILayout.TextField(_portText ?? "", 5, GUILayout.Width(80)));
            GUILayout.EndHorizontal();
            DevStorySelector();
            GUI.enabled = a.Enabled || _lc.TestPrepared;
            if (GUILayout.Button(_devStory < 0 ? "Test-Host vorbereiten (Story-Schritt der Kopie behalten)" : "Test-Host vorbereiten mit Schritt " + _devStory)) StartHost(true);
            GUI.enabled = true;
            if (!a.Enabled && !_lc.TestPrepared) GUILayout.Label(a.Reason);
            GUILayout.Label("Danach im Hauptmenü „Fortsetzen“. Spieler 2 tritt über sein Entwicklermenü bei.");
        }

        void DevStorySelector()
        {
            if (_stepLabels == null)
            {
                _stepLabels = new string[StoryCatalog.Count];
                foreach (var s in StoryCatalog.Steps) _stepLabels[s.Index] = SupportMark[(int)s.Support] + s.Index.ToString("D2") + "  " + s.Key + "  –  " + s.Text;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Story-Schritt:", GUILayout.Width(90));
            if (GUILayout.Button("Kopie behalten", GUILayout.Width(110))) _devStory = -1;
            if (GUILayout.Button("−10", GUILayout.Width(40))) _devStory = Mathf.Clamp(_devStory - 10, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("−1", GUILayout.Width(34))) _devStory = Mathf.Clamp(_devStory - 1, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("+1", GUILayout.Width(34))) _devStory = Mathf.Clamp(_devStory + 1, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("+10", GUILayout.Width(40))) _devStory = Mathf.Clamp(_devStory + 10, 0, StoryCatalog.Count - 1);
            GUILayout.EndHorizontal();
            var sel = StoryCatalog.Get(_devStory);
            if (sel == null) GUILayout.Label("Gewählt: Story-Schritt der Kopie (unverändert).");
            else
            {
                GUILayout.Label("Gewählt: " + _stepLabels[sel.Index]);
                GUILayout.Label(sel.Support == StepSupport.Supported ? "Unterstützt." : (sel.Support == StepSupport.Limited ? "Eingeschränkt: " : "NICHT unterstützt: ") + sel.Reason);
                GUILayout.Label("Frühere Schritte werden nicht nachgespielt: Gegenstände, erledigte Aufgaben, Schulden und Zähler stammen aus der Kopie.");
            }
            GUILayout.Label("Legende: OK unterstützt · ~ eingeschränkt · X nicht unterstützt");
            _stepScroll = GUILayout.BeginScrollView(_stepScroll, GUILayout.Height(190));
            for (int i = 0; i < _stepLabels.Length; i++)
                if (GUILayout.Toggle(_devStory == i, _stepLabels[i])) { if (_devStory != i) _devStory = i; }
            GUILayout.EndScrollView();
        }

        void DevJoinSection(GameContext ctx)
        {
            GUILayout.Label("— Testkampagne beitreten (zweiter PC) —");
            var a = MenuRules.Join(ctx, _lc.State, _legacy, _isolationAvailable, _lc.TestPrepared);
            if (!a.Enabled) { GUILayout.Label(a.Reason); return; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host:", GUILayout.Width(40)); _joinHost = GUILayout.TextField(_joinHost ?? "", 253, GUILayout.Width(220));
            GUILayout.Label("Port:", GUILayout.Width(36)); _joinPort = Digits(GUILayout.TextField(_joinPort ?? "", 5, GUILayout.Width(60)));
            GUILayout.Label("Code:", GUILayout.Width(40)); _joinCode = GUILayout.TextField(_joinCode ?? "", 40, GUILayout.Width(150));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Testkampagne beitreten")) StartJoin(JoinKind.Developer);
        }

        // Technical status, rebuilt at most twice per second (no per-frame string building).
        string DevInfo()
        {
            float now = Time.realtimeSinceStartup;
            if (_devInfo != null && now < _devInfoAt) return _devInfo;
            _devInfoAt = now + 0.5f;
            var n = _runner.Node;
            _devInfo = "Mod " + ProtocolInfo.ModVersion + " / Protokoll " + ProtocolInfo.ProtocolVersion + "   Zustand: " + _lc.State + (n != null ? " (" + n.Role + " " + n.State + ")" : "")
                + "\nSpeicherschutz: " + (SaveGuard.Armed ? "AKTIV" + (SaveGuard.DevHost ? " (Test-Host-Kopie)" : SaveGuard.TestSandbox ? " (Test-Sandbox)" : " (Spieler-2-Sandbox)") : "aus") + (SaveGuard.Armed ? "  " + SaveGuard.SandboxDir : "")
                + "\nTestkampagne: " + (_runner.TestCampaignActive ? "aktiv" : "aus") + (_lc.TestPrepared ? ", vorbereitet" : "") + "   Welt in diesem Prozess geladen: " + (_worldLoadedOnce ? "ja" : "nein")
                + "\nStory-Schritt gesetzt: " + (_appliedStory >= 0 ? _appliedStory + " " + StoryCatalog.Get(_appliedStory).Key : "–") + "   Datenordner: " + _settings.DataDir;
            return _devInfo;
        }
    }
}
