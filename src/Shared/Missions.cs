// W6B.3 cooperative story missions (pure, Unity-free). Unity side: src/Plugin/StoryMissions.cs (StorySync partial).
// Investigation: docs/research/STORY_SYSTEM.md section 4d.
//
// Original (ILSpy 1.27c + byte-exact scene decode, verified 2026-11-19):
//   Mission_Item.Mission_Complete: if (SPrefs(<house>_mission_<i>) != 1) return; required_number_complete <= 0 -> MissionComplete1;
//     then Destroy(diamondMarker); if (!AI) enabled = false.
//   Mission_Item.MissionComplete1: RefreshInfo, mission_complete_image + GM.PlayMissionComplete (actor UI); PS.moneyAm += mission_pay;
//     invScript.AddMoneyMade(pay); SPrefs(<house>_mission_<i>) = 2; currentXPPoints += mission_add_XP (actor); display_as_complete = true;
//     on_complete_advance_story && currentStoryScript.story_description == story_desc -> StoryManager.AdvanceStory();
//     if (!AI && !Door) gameObject.SetActive(false).
//   Callers used here: PlantEvidence.Planted (PlayerScript.Update raycast 2.25 m, tag "PlantEvidence": GM.plantPackageItem
//     (ITEM IDs/347_item_plant).item_amount--, then Planted: disableObj off, enableObj on, missionToComplete.Mission_Complete());
//     Photograph.Update (PS.takingPhoto && camera raycast <= minDistance hits this object -> missionScript.Mission_Complete();
//     enabled = false).
//   Residence data (MainScene PAUSE MENU CANVAS/RESIDENCES SCRIPTS/<house>, SelectResidence.residence_missions):
//     110[0] plant_evidence_110 pay 250 XP 50, on_complete_advance_story story17;  105[0] photograph_kitchen pay 250 XP 50, story18.
//   Mission pref keys are written by StoryManager.InitiateStory accept_job_on_start (1) and MissionComplete1 (2); Mission_Item.LoadAll
//     (OnEnable) shows the marker / obj_to_enable only while the key is 1.
//
// Multiplayer model:
//   - ONE campaign: the host's SPrefs mission keys, money and StoryManager. A listed action performed with the ORIGINAL interaction on
//     Player 2's PC is proposed ("missionAct"); the original completion does not run there until the host committed it.
//   - Host: decides once (listed target, live stage + index, its key == 1, Player 2 in the target's region and - when both share the
//     scene - near the target), then applies the campaign part of MissionComplete1 (key = 2, display_as_complete, shared payout once,
//     AdvanceStory) and the world part on its own copy of the scene objects.
//   - Player 2 on acceptance runs its ORIGINAL completion (mission-complete UI, its own XP, local objects); its money change is
//     reverted by the wallet (MoneyPolicy.Exclude) because the host already paid the shared balance; its AdvanceStory is suppressed
//     (it follows the host index).
//   - "missions" (Session, arg 0): the host's key value per listed target. Player 2 writes it into its sandbox (a later local
//     completion of a done task is then a no-op like the original) and presents a completion done by Player 1 on loaded objects.
//     A host checkpoint reload that restores 1 is followed as well (legitimate rollback, never forced forward).
using System;
using System.Collections.Generic;
using System.Text;

namespace TS2MP.Shared
{
    public enum MissionActionKind : byte { Plant = 1, Photo = 2 }

    public sealed class MissionTarget
    {
        public readonly int House, Mission; public readonly MissionActionKind Kind; public readonly string StageKey, Path, Evidence;
        public readonly int Region; public readonly float MaxDistance;
        public MissionTarget(int house, int mission, MissionActionKind kind, string stageKey, string path, int region, float maxDistance, string evidence)
        { House = house; Mission = mission; Kind = kind; StageKey = stageKey; Path = path; Region = region; MaxDistance = maxDistance; Evidence = evidence; }
        public string PrefKey { get { return House + "_mission_" + Mission; } }   // SelectResidence.Start: houseNumber + "_mission_" + l
        public override string ToString() { return House + "[" + Mission + "] " + Kind + " (" + StageKey + ")"; }
    }

    public static class MissionTargets
    {
        public const int MadisonSt = 99;
        // Supported Player-2 mission actions. Each entry: VERIFIED_ASSET (MadisonSt level3 / MainScene level1 decode) + VERIFIED_IL.
        // Distance = original reach + 3.75 m tolerance for the interpolated remote position (feet, not camera).
        public static readonly MissionTarget[] All =
        {
            new MissionTarget(110, 0, MissionActionKind.Plant, "story17", "DOM 110/MISSION PLANT/Fake", MadisonSt, 6f,
                "MadisonSt DOMY/DOM 110/MISSION PLANT/Fake PlantEvidence (missionToComplete = DOM 110/MISSION PLANT/Trigger Mission_Item house 110 mission 0); 110[0] plant_evidence_110 pay 250, advance story17; PlayerScript.Update interact 2.25 m"),
            new MissionTarget(105, 0, MissionActionKind.Photo, "story18", "Soda Store/STORY PHOTOGRAPH/GameObject/PHOTOGRAPH MISSION", MadisonSt, 18.75f,
                "MadisonSt DOMY/Soda Store/STORY PHOTOGRAPH/GameObject/PHOTOGRAPH MISSION Photograph (minDistance 15, missionScript = .../GameObject Mission_Item house 105 mission 0); 105[0] photograph_kitchen pay 250, advance story18"),
        };

        public static MissionTarget Find(int house, int mission)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].House == house && All[i].Mission == mission) return All[i];
            return null;
        }

        // Client path (any depth) ends with "/" + entry path or equals it.
        public static bool PathMatches(MissionTarget t, string path)
        {
            if (t == null || path == null) return false;
            if (string.Equals(path, t.Path, StringComparison.Ordinal)) return true;
            return path.Length > t.Path.Length && path.EndsWith(t.Path, StringComparison.Ordinal) && path[path.Length - t.Path.Length - 1] == '/';
        }
    }

    // Client proposal "missionAct" (Session, arg 0): version, from index (u16), house (u16), mission, kind, key length, stage key.
    public struct MissionActionRequest
    {
        public const byte Version = 1;
        public int FromIndex, House, Mission; public MissionActionKind Kind; public string StageKey;

        public byte[] ToBytes()
        {
            var k = Encoding.ASCII.GetBytes(StageKey ?? "");
            var b = new byte[8 + k.Length];
            b[0] = Version; b[1] = (byte)(FromIndex & 0xFF); b[2] = (byte)((FromIndex >> 8) & 0xFF);
            b[3] = (byte)(House & 0xFF); b[4] = (byte)((House >> 8) & 0xFF); b[5] = (byte)Mission; b[6] = (byte)Kind;
            b[7] = (byte)k.Length; Buffer.BlockCopy(k, 0, b, 8, k.Length);
            return b;
        }

        public static bool TryParse(byte[] b, out MissionActionRequest r)
        {
            r = default(MissionActionRequest);
            if (b == null || b.Length < 9 || b[0] != Version || (b[6] != (byte)MissionActionKind.Plant && b[6] != (byte)MissionActionKind.Photo)) return false;
            int kl = b[7]; if (kl < 1 || kl > StoryAdvanceRequest.MaxKeyChars || 8 + kl != b.Length) return false;
            string k; if (!StoryState.TryText(b, 8, kl, false, out k)) return false;
            r = new MissionActionRequest { FromIndex = b[1] | (b[2] << 8), House = b[3] | (b[4] << 8), Mission = b[5], Kind = (MissionActionKind)b[6], StageKey = k };
            return true;
        }
        public override string ToString() { return "mission " + House + "[" + Mission + "] " + Kind + " at story " + FromIndex + " '" + StageKey + "'"; }
    }

    // Shared record "missions" (Session, arg 0): version, count, then per listed target house (u16), mission, key value (sbyte).
    public sealed class MissionState
    {
        public const byte Version = 1; public const int MaxEntries = 32;
        public readonly List<int> House = new List<int>(), Mission = new List<int>(), Pref = new List<int>();

        public void Set(int house, int mission, int pref)
        {
            for (int i = 0; i < House.Count; i++) if (House[i] == house && Mission[i] == mission) { Pref[i] = pref; return; }
            House.Add(house); Mission.Add(mission); Pref.Add(pref);
        }
        public bool TryGet(int house, int mission, out int pref)
        {
            for (int i = 0; i < House.Count; i++) if (House[i] == house && Mission[i] == mission) { pref = Pref[i]; return true; }
            pref = 0; return false;
        }
        public byte[] ToBytes()
        {
            var b = new byte[2 + 4 * House.Count];
            b[0] = Version; b[1] = (byte)House.Count;
            for (int i = 0; i < House.Count; i++)
            {
                int o = 2 + 4 * i; b[o] = (byte)(House[i] & 0xFF); b[o + 1] = (byte)((House[i] >> 8) & 0xFF); b[o + 2] = (byte)Mission[i];
                b[o + 3] = (byte)(sbyte)Math.Max(-128, Math.Min(127, Pref[i]));
            }
            return b;
        }
        public static bool TryParse(byte[] b, out MissionState s)
        {
            s = null;
            if (b == null || b.Length < 2 || b[0] != Version || b[1] > MaxEntries || b.Length != 2 + 4 * b[1]) return false;
            var r = new MissionState();
            for (int i = 0; i < b[1]; i++) { int o = 2 + 4 * i; r.Set(b[o] | (b[o + 1] << 8), b[o + 2], (sbyte)b[o + 3]); }
            s = r; return true;
        }
        public bool SameAs(MissionState o)
        {
            if (o == null || o.House.Count != House.Count) return false;
            for (int i = 0; i < House.Count; i++) { int p; if (!o.TryGet(House[i], Mission[i], out p) || p != Pref[i]) return false; }
            return true;
        }
        public override string ToString()
        {
            var sb = new StringBuilder("missions");
            for (int i = 0; i < House.Count; i++) sb.Append(' ').Append(House[i]).Append('[').Append(Mission[i]).Append("]=").Append(Pref[i]);
            return sb.ToString();
        }
    }

    // Host plugin side (src/Plugin/StoryMissions.cs).
    public interface IMissionHost
    {
        bool TryMissionPref(MissionTarget t, out int pref);                      // false: residence data not available (scene loading)
        bool PlayerNear(byte slot, MissionTarget t, out string why);             // region + (shared scene) distance of the actor
        bool Complete(MissionTarget t, byte slot, out string why);              // campaign + world part of MissionComplete1, once
        void Publish();                                                          // commit "missions" from the host keys (on change)
    }

    public static class MissionRules
    {
        // Host decision for one Player-2 mission action. Exactly once: the host key must still be 1 (accepted, not done) and the story
        // stage / index must be the request's; a second / replayed / concurrent request finds key 2 or a newer index -> Stale.
        public static ProposalCode Decide(bool hostReady, bool freeMode, int hostIndex, string hostKey, MissionActionRequest r, MissionTarget t,
                                          bool prefKnown, int pref, bool near, string nearWhy, out string why)
        {
            why = null;
            if (!hostReady) { why = "host story not ready (scene loading)"; return ProposalCode.Busy; }
            if (t == null || t.Kind != r.Kind || !string.Equals(t.StageKey, r.StageKey, StringComparison.Ordinal)) { why = "mission action not listed"; return ProposalCode.Invalid; }
            if (freeMode) { why = "free mode: story missions are pre-completed (original StoryManager.Start)"; return ProposalCode.Unchanged; }
            if (!prefKnown) { why = "host residence data not available"; return ProposalCode.Busy; }
            if (pref >= 2) { why = "task already completed in the host campaign"; return ProposalCode.Stale; }
            if (pref != 1) { why = "task not accepted in the host campaign (key " + pref + ")"; return ProposalCode.Stale; }
            if (r.FromIndex != hostIndex) { why = "stale: host story is " + hostIndex + " '" + hostKey + "', request from " + r.FromIndex; return ProposalCode.Stale; }
            if (!string.Equals(hostKey, r.StageKey, StringComparison.Ordinal)) { why = "host stage '" + hostKey + "' is not '" + r.StageKey + "'"; return ProposalCode.Stale; }
            if (!near) { why = nearWhy ?? "actor not at the target"; return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        }

        // Player 2: what to do with a host key value for a listed target (sandbox key write / presentation).
        public static bool ClientPresentDone(int localPref, int hostPref) { return hostPref >= 2 && localPref < 2; }
    }
}
