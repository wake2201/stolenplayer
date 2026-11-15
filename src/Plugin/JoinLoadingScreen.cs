// v0.16.1: the ORIGINAL main-menu loading presentation while Player 2 joins (connect, authenticate, arm the save isolation,
// receive + verify the host campaign) - before the original MainMenu.Continue() can run.
//
// Verified original (IL, Assembly-CSharp 1.27c):
//   MainMenu.Continue      IL_0000-000b FadeLoad = 0 + SPrefs.Save; IL_0010-002c every MainMenu.Deactivate[i].SetActive(false);
//                          IL_002e-0035 MainMenu.ActivateLoad.SetActive(true); IL_003a volumeDown = true; IL_0041-004d
//                          StartCoroutine(LoadNewSceneAsync("MainScene")).
//   MainMenu.StartNewGame  the same Deactivate / ActivateLoad / volumeDown / LoadNewSceneAsync tail (IL_0104-0146).
//   <LoadNewSceneAsync>d__70 IL_0021-002d funFactTimer = 6, showFunFacts = true; then SceneManager.LoadSceneAsync and per frame
//                          loadText = GetText("loading") + " (" + progress + "%)" until isDone.
//   MainMenu.Update        IL_019d-01d6: Esc while optionsObject is active -> options off, bgObject on.
// So the original "loading" state of the main menu = Deactivate[] hidden + ActivateLoad shown (+ music fade + fun facts).
//
// This helper applies exactly the visible part of that state (Deactivate[] off, ActivateLoad on, fun facts on) while the join
// handshake runs - the same original objects, no new UI, no invented progress (loadText keeps its scene text until the real
// coroutine writes the real percentage). The music fade (volumeDown) is left to the original Continue: it is not reversible
// without re-implementing MainMenu.Update. Menu input is blocked by disabling the current uGUI EventSystem (the F8 window is
// IMGUI and stays usable for "Abbrechen"). Hand-off: right before the original Continue the EventSystem is re-enabled and the
// state is left to the original (vanilla shows the same screen with its EventSystem enabled). Cancel / failure / timeout in
// the main menu: every object is restored to its recorded state.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class JoinLoadingScreen
    {
        readonly InstanceLog _log;
        readonly Type _menuType, _esType;
        readonly Func<object, GameObject[]> _deactivate; readonly Func<object, GameObject> _activateLoad, _options;
        readonly Func<object, bool> _showFacts; readonly Action<object, bool> _setShowFacts;
        readonly Func<object, float> _factTimer; readonly Action<object, float> _setFactTimer;
        readonly Func<Behaviour> _currentEs;

        // recorded original state while shown
        UnityEngine.Object _menu; readonly List<GameObject> _hidden = new List<GameObject>(); GameObject _load; bool _loadWasActive;
        GameObject _optionsHidden; bool _factsWas; float _timerWas; Behaviour _es; bool _esWasEnabled;
        public bool Shown { get { return _menu != null; } }
        public bool Available { get; private set; }
        long _shows, _restores, _handoffs;

        public JoinLoadingScreen(InstanceLog log)
        {
            _log = log;
            _menuType = AccessTools.TypeByName("MainMenu");
            _esType = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
            _deactivate = Fast.Getter<GameObject[]>(_menuType, "Deactivate");
            _activateLoad = Fast.Getter<GameObject>(_menuType, "ActivateLoad");
            _options = Fast.Getter<GameObject>(_menuType, "optionsObject");
            _showFacts = Fast.Getter<bool>(_menuType, "showFunFacts"); _setShowFacts = Fast.Setter<bool>(_menuType, "showFunFacts");
            _factTimer = Fast.Getter<float>(_menuType, "funFactTimer"); _setFactTimer = Fast.Setter<float>(_menuType, "funFactTimer");
            var cur = _esType != null ? AccessTools.Property(_esType, "current") : null;
            if (cur != null && cur.GetGetMethod() != null) { var g = cur.GetGetMethod(); _currentEs = () => g.Invoke(null, null) as Behaviour; }
            Available = _menuType != null && _deactivate != null && _activateLoad != null;
            if (!Available) log.Warn("JOIN_LOADING original MainMenu.Deactivate / ActivateLoad not found: the join keeps the main menu visible (input is still blocked when the EventSystem is available)");
        }

        // Called every frame by MultiplayerMenu with the Lifecycle rule (MenuRules.JoinLoadingScreen).
        public void Sync(bool show)
        {
            if (show && !Shown) Show();
            else if (!show && Shown) Restore("join ended in the main menu");
            else if (Shown && !Alive(_menu)) Forget("main menu unloaded");
        }

        float _nextTry;
        void Show()
        {
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 1f;   // at most one scene search per second until found (normally once per join)
            var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;
            _es = _currentEs != null ? _currentEs() : null;
            if (mm == null && _es == null) return;
            _menu = mm ?? (UnityEngine.Object)_es;
            _hidden.Clear(); _load = null; _optionsHidden = null;
            if (mm != null && Available)
            {
                var arr = _deactivate(mm);
                if (arr != null) foreach (var go in arr) if (go != null && go.activeSelf) { go.SetActive(false); _hidden.Add(go); }
                var opt = _options != null ? _options(mm) : null;
                if (opt != null && opt.activeSelf) { opt.SetActive(false); _optionsHidden = opt; }   // the original never shows the loading screen with options open
                _load = _activateLoad(mm);
                if (_load != null) { _loadWasActive = _load.activeSelf; _load.SetActive(true); }
                if (_showFacts != null && _setShowFacts != null && _factTimer != null && _setFactTimer != null)
                { _factsWas = _showFacts(mm); _timerWas = _factTimer(mm); _setFactTimer(mm, 6f); _setShowFacts(mm, true); }   // <LoadNewSceneAsync> IL_0021-002d
            }
            if (_es != null) { _esWasEnabled = _es.enabled; _es.enabled = false; }
            _shows++;
            _log.Info("JOIN_LOADING original loading screen shown while Player 2 joins (MainMenu.Deactivate " + _hidden.Count + " hidden, ActivateLoad " + (_load != null ? "on" : "missing")
                + ", menu input " + (_es != null ? "blocked" : "NOT blocked: no EventSystem") + "); cancel with F8");
        }

        // The original MainMenu.Continue takes over (it applies the same state and loads the world).
        public void HandOff()
        {
            if (!Shown) return;
            if (_es != null && Alive(_es)) _es.enabled = _esWasEnabled;
            _handoffs++;
            _log.Info("JOIN_LOADING handed over to the original MainMenu.Continue() (menu input restored to its original state)");
            Clear();
        }

        void Restore(string why)
        {
            if (Alive(_menu))
            {
                foreach (var go in _hidden) if (go != null) go.SetActive(true);
                if (_optionsHidden != null) _optionsHidden.SetActive(true);
                if (_load != null) _load.SetActive(_loadWasActive);
                var mm = _menu as Component;
                if (mm != null && _menuType != null && _menuType.IsInstanceOfType(mm) && _setShowFacts != null && _setFactTimer != null) { _setShowFacts(mm, _factsWas); _setFactTimer(mm, _timerWas); }
            }
            if (_es != null && Alive(_es)) _es.enabled = _esWasEnabled;
            _restores++;
            _log.Info("JOIN_LOADING main menu restored (" + why + "): " + _hidden.Count + " original menu object(s) shown again, menu input enabled");
            Clear();
        }

        void Forget(string why) { _log.Info("JOIN_LOADING released (" + why + ")"); Clear(); }

        void Clear() { _menu = null; _hidden.Clear(); _load = null; _optionsHidden = null; _es = null; }

        static bool Alive(UnityEngine.Object o) { return o != null && o; }

        public string Stats { get { return "shown " + _shows + " handedOff " + _handoffs + " restored " + _restores; } }
    }
}
