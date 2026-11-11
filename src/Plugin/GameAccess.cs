// M3.1: allocation-free access to game fields/methods without referencing Assembly-CSharp at compile time.
// Compiled expression delegates (one-time cost at startup) replace per-frame FieldInfo.GetValue (boxing, reflection).
// SleepHooks: Harmony postfixes on the game's own sleep (PassTime.PassStart / StopIt) + passingTime edge poll while a
// sleep runs (a completed sleep never calls StopIt) -> sleep start/end detection.
using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace TS2MP.Plugin
{
    internal static class Fast
    {
        public static Func<object, T> Getter<T>(Type owner, string field)
        {
            var f = owner == null ? null : AccessTools.Field(owner, field);
            if (f == null) return null;
            var p = Expression.Parameter(typeof(object), "o");
            Expression body = Expression.Field(Expression.Convert(p, owner), f);
            if (body.Type != typeof(T)) body = Expression.Convert(body, typeof(T));   // enums -> int, components -> Object
            return Expression.Lambda<Func<object, T>>(body, p).Compile();
        }

        public static Action<object, T> Setter<T>(Type owner, string field)
        {
            var f = owner == null ? null : AccessTools.Field(owner, field);
            if (f == null || f.FieldType != typeof(T)) return null;
            var p = Expression.Parameter(typeof(object), "o");
            var v = Expression.Parameter(typeof(T), "v");
            var assign = Expression.Assign(Expression.Field(Expression.Convert(p, owner), f), v);
            return Expression.Lambda<Action<object, T>>(assign, p, v).Compile();
        }

        public static Action<object, T> Call<T>(Type owner, string method)
        {
            var m = owner == null ? null : AccessTools.Method(owner, method, new[] { typeof(T) });
            if (m == null) return null;
            var p = Expression.Parameter(typeof(object), "o");
            var a = Expression.Parameter(typeof(T), "a");
            return Expression.Lambda<Action<object, T>>(Expression.Call(Expression.Convert(p, owner), m, a), p, a).Compile();
        }

        public static Action<object> Call0(Type owner, string method)
        {
            var m = owner == null ? null : AccessTools.Method(owner, method, Type.EmptyTypes);
            if (m == null) return null;
            var p = Expression.Parameter(typeof(object), "o");
            return Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(p, owner), m), p).Compile();
        }
    }

    internal static class SleepHooks
    {
        // Ended(true): the sleep ran to its target (passingTime cleared inside PassTime.Update, no StopIt call);
        // Ended(false): StopIt (Esc / survival abort). Fired at most once per started sleep.
        public static event Action Started;
        public static event Action<bool> Ended;
        static Func<object, bool> _passing;
        static Func<object, int> _targetHr;   // PassTime.currentHr = slider target hour (SetSleepValue)
        static Harmony _h;
        static object _active;   // PassTime instance of the running sleep; polled only while set

        public static bool Active { get { return _active != null; } }
        public static bool Observer;   // M4.3.3: the mod presents the OTHER player's sleep with PassTime - not a local sleep
        public static int TargetHour { get { try { return _active != null && _targetHr != null ? _targetHr(_active) : -1; } catch { return -1; } } }

        // Per frame from WorldSync.Update; one compiled field read while a sleep runs, nothing otherwise.
        public static void Poll()
        {
            if (_active == null) return;
            var o = _active as UnityEngine.Object;
            bool passing;
            try { passing = o != null && _passing(_active); } catch { passing = false; }
            if (!passing) Finish(true);
        }

        static void Finish(bool completed)
        {
            _active = null;
            try { if (Ended != null) Ended(completed); } catch { }
        }

        public static string Install(string id)
        {
            var t = AccessTools.TypeByName("PassTime");
            if (t == null) return "PassTime type not found";
            _passing = Fast.Getter<bool>(t, "passingTime");
            _targetHr = Fast.Getter<int>(t, "currentHr");
            var start = AccessTools.Method(t, "PassStart");
            var stop = AccessTools.Method(t, "StopIt");
            if (_passing == null || start == null || stop == null) return "PassTime.passingTime/PassStart/StopIt not found";
            _h = new Harmony(id);
            _h.Patch(start, null, new HarmonyMethod(AccessTools.Method(typeof(SleepHooks), "AfterPassStart")));
            _h.Patch(stop, null, new HarmonyMethod(AccessTools.Method(typeof(SleepHooks), "AfterStopIt")));
            return null;
        }

        public static void Uninstall() { if (_h != null) { _h.UnpatchSelf(); _h = null; } Started = null; Ended = null; _active = null; }

        // PassStart may refuse (CheckIfCanSleepAnymore); only a started fast-forward counts. A repeated PassStart while
        // a sleep is running is not a new sleep (the original start time stays authoritative).
        static void AfterPassStart(object __instance)
        {
            try
            {
                if (Observer || _active != null || !_passing(__instance)) return;
                _active = __instance;
                if (Started != null) Started();
            }
            catch { }
        }

        static void AfterStopIt()
        {
            if (_active != null) Finish(false);
        }
    }
}
