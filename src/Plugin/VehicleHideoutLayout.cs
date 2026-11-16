// V1.4: hideout parking layout of the two original car objects (partial of VehicleSync). Decision: HideoutLayout.Plan
// (src/Shared/Travel.cs); this file gathers the scene facts, applies the result and logs VEHICLE_HIDEOUT_LAYOUT.
// Root cause (verified IL 1.27c VehiclesManager.InitiateMainCar + GameManager.Start, scene scan level1):
//   at the hideout the original activates BOTH cars when the Shadowhawk is owned (or CurrentCar == 1) and puts THIS save's
//   CurrentCar at GameManager.SpawnHideoutCar (-97.7, 76.4, -169.6; yaw CarController.rotY_hideout) and the other car at
//   VehiclesManager.secondCarHideoutPos (-109.8, 76.4, -168.8). Each PC uses its own CurrentCar (the car it last drove), so
//   after independent trips the PCs disagree: host CurrentCar 0 -> Ranger at the main spot, Player 2 CurrentCar 1 ->
//   Shadowhawk at the main spot. The host's Ranger (AwayProxy, car at the Auto Shop) kept the host's main spot; the
//   Shadowhawk (ParkedOther, simulated by Player 2) followed Player 2's pose onto it -> two cars on one spot on the host,
//   and Player 2 had the Ranger proxy at the second spot where Player 1 never saw it. W1's KeepClearOfOtherCars ran only
//   once, when the proxy appeared, and only moved the proxy away from cars already standing there.
// Applied (event-driven): after every reconcile in the hideout and at most once per second for settled poses; cheap
// (<= 2 cars, distance compares); scene objects are never searched. Never moves a driven / moving car, never writes a
// park pose or record for a proxy; a Real car is arranged once per scene (before it is driven).
using System;
using System.Collections.Generic;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class VehicleSync
    {
        bool _layoutDirty = true; string _layoutReason = "scene bind"; double _layoutNext; object _layoutGm; string _layoutSig;
        Func<object, float> _carRotYHideout; bool _layoutApiTried;
        long _layoutPasses, _layoutMoves;
        readonly List<LayoutCar> _lyCars = new List<LayoutCar>(2); readonly List<LocalCar> _lyLocal = new List<LocalCar>(2); readonly List<string> _lyKind = new List<string>(2);

        void MarkLayout(string why) { if (!_layoutDirty) _layoutReason = why; _layoutDirty = true; }

        void LayoutFrame(UnityEngine.Object gm, UnityEngine.Object ps, int region, double now)
        {
            if (region != TravelRules.Hideout || gm == null) return;
            bool newScene = !ReferenceEquals(gm, _layoutGm);
            if (!_layoutDirty && now < _layoutNext) return;   // the scene bind arrives through the first reconcile (MarkLayout)
            string reason = newScene ? "scene bind" : _layoutDirty ? _layoutReason : "settled poses";
            _layoutDirty = false; _layoutNext = now + 1.0;
            var vm = _gmVM(gm);
            var main = _gmHideoutCar != null ? _gmHideoutCar(gm) as Transform : null;
            var second = vm != null && _vmSecondPos != null ? _vmSecondPos(vm) as Transform : null;
            if (main == null || second == null) { if (_warned.Add(95001)) _log.Warn("VEHICLE_HIDEOUT_LAYOUT original spots missing (SpawnHideoutCar " + (main != null) + ", secondCarHideoutPos " + (second != null) + "): layout not applied"); return; }
            if (!_layoutApiTried && _local.Count > 0)
            {
                _layoutApiTried = true;
                _carRotYHideout = Fast.Getter<float>(_local[0].Car.GetType(), "rotY_hideout");
            }
            _layoutPasses++;
            var cur = ps != null ? _psVehicle(ps) as Component : null;
            var cars = _lyCars; var lcs = _lyLocal; var pres = _lyKind;
            cars.Clear(); lcs.Clear(); pres.Clear();
            bool anyMoving = false;
            foreach (var lc in _local)
            {
                if (lc.Go == null || !lc.Active) continue;
                LayoutRole role; string kind;
                ParkRecord pk; bool pose = _view.TryGetPark(lc.Id, out pk) && pk.IsValid && pk.HasPose && pk.Region == TravelRules.Hideout;
                if (lc.Mine && !lc.IsProxy)
                {
                    kind = "Real";
                    bool seated = ReferenceEquals(cur, lc.Car) && _sitting(lc.Car);
                    bool moving = lc.Rb != null && !lc.Rb.isKinematic && lc.Rb.velocity.sqrMagnitude > 0.25f;
                    if (seated || moving)
                    {
                        // V1.4 follow-up: a car someone sat in / drove has its place chosen by the driver -> never rearranged later
                        // (without this, a car first driven before its arrangement was moved to its identity spot after parking).
                        if (!lc.Arranged) { lc.Arranged = true; _log.Info("VEHICLE_HIDEOUT_LAYOUT " + CarName(lc.Id) + " Real (" + (moving ? "moving" : "driver seated") + ") at " + Fmt(lc.T.position) + ": driver's place, never repositioned"); }
                        if (moving) { anyMoving = true; continue; }   // moving: not an obstacle yet (the other PC's observer also waits until it stopped)
                    }
                    role = lc.Arranged || JointHolds(lc.Id) ? LayoutRole.Fixed : LayoutRole.Arrange;   // stopped with the driver seated = Fixed obstacle, like Remote.Stopped on the other PC
                }
                else if (lc.Observed && lc.Pres == CarPresentation.ParkedOther)
                {
                    kind = "ParkedOther";
                    if (lc.FollowRecv > 0 && !lc.Remote.Stopped(now)) { anyMoving = true; continue; }   // moving under its holder: not an obstacle yet, never moved here
                    role = lc.Arranged || lc.FollowRecv > 0 ? LayoutRole.Fixed : LayoutRole.Arrange;   // before its holder's snapshots: the same arrangement the holder runs (committed pose first)
                }
                else if (lc.IsProxy && !lc.Observed && lc.Pres == CarPresentation.AwayProxy) { kind = "AwayProxy"; role = LayoutRole.Proxy; }
                else continue;
                cars.Add(new LayoutCar { Id = lc.Id, Role = role, Pos = V(lc.T.position), HasPose = pose, Pose = pose ? pk.Pos : default(Vec3) });
                lcs.Add(lc); pres.Add(kind);
            }
            if (cars.Count == 0) return;   // before the first reconcile: nothing presented yet (the scene bind is logged with the first cars)
            if (newScene) { _layoutGm = gm; _layoutSig = null; }
            var plan = HideoutLayout.Plan(cars, V(main.position), V(second.position), anyMoving);   // V1.4.1: proxies hold while a car moves
            var psc = ps as Component;
            List<string> moved = null;
            for (int i = 0; i < plan.Length; i++)
            {
                var lc = lcs[i]; var c = cars[i]; var r = plan[i];
                if (c.Role == LayoutRole.Fixed) continue;
                if (!r.Moves(c.Pos)) { if (c.Role == LayoutRole.Arrange) lc.Arranged = true; continue; }
                var target = new Vector3(r.Target.X, r.Target.Y, r.Target.Z);
                if (c.Role == LayoutRole.Arrange && pres[i] == "Real" && psc != null && Flat(psc.transform.position, target) < 1.2f)
                {
                    if (_warned.Add(95100 + lc.Kind)) _log.Info("VEHICLE_HIDEOUT_LAYOUT " + CarName(lc.Id) + " arrangement waits: the local player stands on " + r.Choice);
                    continue;
                }
                Quaternion rot;
                if (r.Choice == LayoutChoice.MainSpot) rot = Quaternion.Euler(main.rotation.x, _carRotYHideout != null ? _carRotYHideout(lc.Car) : main.eulerAngles.y, main.rotation.z);   // GameManager.Start (hideout) IL
                else if (r.Choice == LayoutChoice.SecondSpot) rot = second.rotation;                                                                                                       // InitiateMainCar
                else { ParkRecord pk; _view.TryGetPark(lc.Id, out pk); rot = new Quaternion(pk.Rot.X, pk.Rot.Y, pk.Rot.Z, pk.Rot.W); }
                var from = lc.T.position;
                lc.T.SetPositionAndRotation(target, rot);
                if (lc.Rb != null)
                {
                    if (lc.Rb.isKinematic || lc.IsProxy) { lc.Rb.position = target; lc.Rb.rotation = rot; }
                    if (!lc.Rb.isKinematic) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
                }
                if (lc.IsProxy && !lc.Ghost) GhostIfAroundPlayer(lc, ps);
                if (c.Role == LayoutRole.Arrange) lc.Arranged = true;
                _layoutMoves++;
                if (moved == null) moved = new List<string>(2);
                moved.Add(CarName(lc.Id) + " " + pres[i] + " " + Fmt(from) + " -> " + r.Choice + " " + Fmt(target) + " (" + (r.Overlapped ? "overlapped another car" : c.Role == LayoutRole.Proxy ? "proxy spot follows the shared layout" : c.HasPose ? "committed hideout pose" : "arrival without a hideout pose: first free original spot (main first)") + ")");
            }
            string sig = "";
            for (int i = 0; i < plan.Length; i++) sig += cars[i].Id + ":" + pres[i] + ":" + cars[i].Role + ":" + plan[i].Choice + ";";
            if (moved == null && sig == _layoutSig) return;
            _layoutSig = sig;
            for (int i = 0; i < plan.Length; i++) _log.Info("VEHICLE_HIDEOUT_LAYOUT " + reason + ": " + Describe(lcs[i], pres[i], cars[i], plan[i]));
            if (moved != null) foreach (var m in moved) _log.Info("VEHICLE_HIDEOUT_LAYOUT corrected " + m);
        }

        string Describe(LocalCar lc, string kind, LayoutCar c, LayoutResult r)
        {
            VehicleRecord rec; string recS = Rec(lc.Id, out rec) ? Regions.Name(rec.Region) + " holder P" + rec.Holder : "no record";
            int on = 0, all = 0;
            foreach (var rd in lc.Go.GetComponentsInChildren<Renderer>(true)) { all++; if (rd.enabled && rd.gameObject.activeInHierarchy) on++; }
            ParkRecord pk; string park = _view.TryGetPark(lc.Id, out pk) ? (pk.HasPose ? "pose in " + Regions.Name(pk.Region) + (pk.Region == TravelRules.Hideout ? " (valid here)" : " (not a hideout pose)") : "no pose (" + Regions.Name(pk.Region) + ")") : "none";
            return CarName(lc.Id) + " region " + recS + " presentation " + kind + " role " + c.Role + " root " + (lc.Go.activeSelf ? "active" : "inactive") + " renderers " + on + "/" + all
                + " pos " + Fmt(lc.T.position) + " yaw " + lc.T.eulerAngles.y.ToString("0") + " spot " + r.Choice + " park " + park + " overlap " + (r.Overlapped ? "yes" : "no");
        }

        static Vec3 V(Vector3 p) { return new Vec3(p.x, p.y, p.z); }
    }
}
