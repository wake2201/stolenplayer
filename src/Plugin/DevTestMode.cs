// W4.2B.1 developer test mode (manual two-instance QA only). Reachable ONLY from the developer launcher's "devtest"
// command (-ts2mp-devtest argument, launcher mode, loopback host/client); the F8 mode of the release never parses it and the
// release package contains no launcher. Never active otherwise.
//
// Isolation (Plugin.Awake, before anything touches persistence):
//   - every TS2MP data file (Player-2 record p2inv-*, p2spawn-*, fleet-*, client sandbox, logs) lives in <data>\devtest;
//   - the host plays a COPY of the real save folder in <data>\devtest\host-save (SaveGuard.InstallDevHost: redirect +
//     guards; the real folder is only read, once, while the copy has no save yet); Player 2 uses its normal client
//     sandbox inside <data>\devtest. Steam achievements and PlayerPrefs writes are blocked on both (SaveGuard guards).
// Test conveniences (only while the save target is verified to be inside the isolated folder):
//   - all skill-tree skills learned: Skill_Manager.Start postfix -> the original Skill_Manager.Learn_Skill(i) per skill
//     (SPrefs "Skill_<i>" = 1 + isLearned) and extra skills at their maximum upgrade (SPrefs <skill_name>, the value
//     SkillPage.LearnSkillExtra writes). No story, mission, XP or level is touched.
//   - money: PlayerScript.moneyAm topped up to 1,000,000 whenever it falls below 250,000 (checked every 2 s). Player 2's
//     money travels to the host's (isolated) Player-2 record through the normal p2report path.
//   - tools are bought by the tester in the original shops.
using System;
using System.Collections;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class DevTestMode
    {
        public const int MoneyFloor = 250000, MoneyTopUp = 1000000;
        internal static DevTestMode Instance;

        readonly InstanceLog _log;
        readonly Type _gmT, _psT, _smT;
        readonly Func<object, bool> _gmLoaded; readonly Func<object, object> _gmPs, _smSkills;
        readonly Func<object, int> _money; readonly Action<object, int> _setMoney;
        readonly Func<object, bool> _isExtra, _learned; readonly Func<object, int> _cur, _max; readonly Action<object, int> _setCur; readonly Func<object, string> _name, _pref;
        readonly System.Reflection.MethodInfo _learn, _spSetInt, _spGetInt;
        object _gm; float _nextCheck, _nextFind; bool _skillsDoneThisLoad, _skillsOk; int _topUps;

        // Data folder of a developer test: always a dedicated "devtest" sub-folder (production files can never be reached).
        public static string IsolatedDataDir(string dataDir) { return SaveIsolation.DevTestDataDir(dataDir); }

        public static bool LoopbackOnly(LaunchArgs a)
        {
            return a != null && NetConfig.IsLoopback(a.BindAddress) && NetConfig.IsLoopback(a.ConnectAddress) && a.JoinKey == null;
        }

        public DevTestMode(InstanceLog log)
        {
            _log = log;
            _gmT = AccessTools.TypeByName("GameManager"); _psT = AccessTools.TypeByName("PlayerScript"); _smT = AccessTools.TypeByName("Skill_Manager");
            var siT = AccessTools.TypeByName("Skill_Instance");
            _gmLoaded = Fast.Getter<bool>(_gmT, "loadedMap"); _gmPs = Fast.Getter<object>(_gmT, "PS");
            _money = Fast.Getter<int>(_psT, "moneyAm"); _setMoney = Fast.Setter<int>(_psT, "moneyAm");
            _smSkills = Fast.Getter<object>(_smT, "skills");
            _isExtra = Fast.Getter<bool>(siT, "is_extra_skill"); _learned = Fast.Getter<bool>(siT, "isLearned"); _cur = Fast.Getter<int>(siT, "currentUpgrade"); _max = Fast.Getter<int>(siT, "maxUpgrades");
            _setCur = Fast.Setter<int>(siT, "currentUpgrade"); _name = Fast.Getter<string>(siT, "skill_name"); _pref = Fast.Getter<string>(siT, "skill_pref");
            _learn = _smT != null ? AccessTools.Method(_smT, "Learn_Skill", new[] { typeof(int) }) : null;
            var sp = AccessTools.TypeByName("SPrefs");
            _spSetInt = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
            _spGetInt = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
            Instance = this;
            bool skillsOk = _smSkills != null && _learn != null && _isExtra != null && _cur != null && _max != null && _setCur != null && _name != null && _spSetInt != null;
            if (skillsOk)
            {
                try
                {
                    new Harmony("com.ts2mp.multiplayer.devtest").Patch(AccessTools.Method(_smT, "Start"), postfix: new HarmonyMethod(AccessTools.Method(typeof(DevTestMode), "SkillStartPost")));
                }
                catch (Exception ex) { skillsOk = false; _log.Error("DEVTEST Skill_Manager.Start hook failed", ex); }
            }
            _skillsOk = skillsOk;
            _log.Warn("DEVTEST MODE ACTIVE (developer launcher only): isolated test saves, all skills " + (skillsOk ? "unlocked on every world load" : "NOT available (original skill API missing)")
                + ", money kept >= " + MoneyFloor + (_money != null && _setMoney != null ? "" : " (NOT available: PlayerScript.moneyAm missing)") + ". Never use this for a real campaign.");
        }

        // Only while this process is isolated (dev host copy or client sandbox) and the active save target is verified.
        static bool Isolated() { return SaveGuard.Armed && SaveGuard.RuntimeCheck(); }

        static void SkillStartPost(object __instance)
        {
            var i = Instance; if (i == null) return;
            try { i.UnlockSkills(__instance, "Skill_Manager.Start"); } catch (Exception ex) { i._log.Error("DEVTEST skill unlock failed", ex); }
        }

        // Same rule as the original Skill_Manager.Check_If_Skill_Learned: SPrefs(skill_pref) >= 1 or isLearned.
        bool Learned(object s)
        {
            if (_learned != null && _learned(s)) return true;
            string p = _pref != null ? _pref(s) : null;
            return p != null && _spGetInt != null && (int)_spGetInt.Invoke(null, new object[] { p }) >= 1;
        }

        void UnlockSkills(object mgr, string why)
        {
            if (!Isolated()) { _log.Warn("DEVTEST skill unlock skipped: save target not verified inside the isolated test folder"); return; }
            var list = _smSkills(mgr) as IList;
            if (list == null) return;
            int learned = 0, upgraded = 0;
            for (int k = 0; k < list.Count; k++)
            {
                var s = list[k]; if (s == null) continue;
                // original Learn_Skill: SPrefs.SetInt(skill_pref, 1) + isLearned = true; only when not learned yet, so later
                // loads write nothing (FBPP may write the file on every Set when AutoSaveData is on)
                if (!Learned(s)) { _learn.Invoke(mgr, new object[] { k }); learned++; }
                if (_isExtra(s))
                {
                    int max = _max(s);
                    if (max > 0 && _cur(s) < max) { _setCur(s, max); _spSetInt.Invoke(null, new object[] { _name(s), max }); upgraded++; }
                }
            }
            _skillsDoneThisLoad = true;
            _log.Info("DEVTEST skills (" + why + "): " + learned + " skill(s) learned, " + upgraded + " extra skill(s) set to their maximum upgrade (isolated test save only)");
        }

        // Per frame from NetRunner: one cheap time compare; the work runs every 2 s inside a loaded world.
        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 2f;
            if (_gm == null || (_gm is UnityEngine.Object && !(UnityEngine.Object)_gm))
            {
                _gm = null; _skillsDoneThisLoad = false;
                if (now < _nextFind || _gmT == null) return;
                _nextFind = now + 2f;
                _gm = UnityEngine.Object.FindObjectOfType(_gmT);   // only while no world is loaded (<= once per 2 s)
                if (_gm == null) return;
            }
            if (_gmLoaded == null || !_gmLoaded(_gm)) return;
            var ps = _gmPs != null ? _gmPs(_gm) : null;
            var psu = ps as UnityEngine.Object;
            if (psu == null || !psu) return;
            if (!Isolated()) return;
            if (!_skillsDoneThisLoad && _skillsOk)
            {
                _skillsDoneThisLoad = true;   // fallback once per world load (the Start hook normally did it already)
                var mgr = UnityEngine.Object.FindObjectOfType(_smT);
                if (mgr != null) { try { UnlockSkills(mgr, "world load fallback"); } catch (Exception ex) { _log.Error("DEVTEST skill unlock failed", ex); } }
            }
            if (_money != null && _setMoney != null && _money(ps) < MoneyFloor)
            {
                int before = _money(ps);
                _setMoney(ps, MoneyTopUp); _topUps++;
                if (_topUps <= 5 || _topUps % 20 == 0) _log.Info("DEVTEST money " + before + " -> " + MoneyTopUp + " (top-up #" + _topUps + ", isolated test save only)");
            }
        }

        public void Dispose() { if (Instance == this) Instance = null; }
    }
}
