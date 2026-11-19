// W6A: shared story progression foundation (pure, Unity-free). Unity side: src/Plugin/StorySync.cs.
// Investigation: docs/research/STORY_SYSTEM.md (mission-system map).
//
// Original (Cecil IL 1.27c, verified 2026-11-17):
//   StoryManager.AdvanceStory IL_0000..IL_001d: if (isFreeMode) return; story++; InitiateStory().
//   StoryManager.InitiateStory: GM.CheckStory (HUD animation); currentStoryScript = story_scripts[story] (free mode: 80);
//     advance_if_home (CurrentLocation == 0) / advance_if_jail (== 93) -> story++ in place (IL_0066 / IL_00a6, no AdvanceStory);
//     RefreshText, buttons, tutorials, police_chase_on_start (PlayerScript.StartPoliceChase), setPrefsTo1, first_mission
//     (player + main car teleport), music, blockDrop, deduct_debt once (<key>_deduct, PlayerScript.debtAm), phone call
//     (<key>_Call once), accept_job_on_start (<house>_mission_<job> = 1 + side info), unlock_quickBuy, rain / thunder,
//     StartCoroutine(InitiateOff) (calledInitiate -> StoryObject.Update re-evaluates every StoryObject gate).
//   StoryManager.BeginStory (scene load): story = SPrefs "Story" (clamped), InitiateStory(). SaveStory: SPrefs "Story" = story.
//   ReadNote.BeginRead IL_015a..IL_0195: advanceStory && currentStoryScript.story_description == storyDesc -> AdvanceStory()
//     (advanceStory is never cleared: the note can advance again only while its own step is current, i.e. once).
//   ReadNote.Start IL_0062..IL_007f: addParcelCode && SPrefs(parcel_pref) >= 2 -> gameObject.SetActive(false).
//   StoryObject.UpdateObject: story_desc_to_activate == current story_description -> obj_to_activate active, else inactive.
//
// Multiplayer model:
//   - ONE story index: the host's StoryManager.story. The host publishes "story" (index + stage key) whenever it changes.
//   - Player 2 never runs AdvanceStory on its own copy (StorySync prefix). An original trigger on Player 2's PC that the
//     original code itself let through (its own condition evaluated on Player 2's copy, which equals the host's step) and
//     that is listed in StoryTriggers is proposed ("storyAdvance": from index + stage key + trigger). The host applies it
//     with ITS original AdvanceStory exactly once: only when its live index == from index, its live stage key == the
//     request's key and the trigger is listed for that key. A second / replayed / concurrent request finds a newer index
//     -> Stale; a retransmission is answered from the proposal cache.
//   - Player 2 follows the host index like the original scene load (BeginStory): story = host index, InitiateStory() for
//     the presentation (HUD text, StoryObject gates, tutorials, phone call, job acceptance markers), SaveStory() into its
//     sandbox. Actor / money effects of a stage start stay with the host: Player 2's InitiateStory runs with
//     ClientStrippedFlags cleared for that one call (no teleport, no chase, no debt deduction, no location skip).
//   - Unlisted triggers on Player 2's PC do nothing (logged once per stage), like a refused interaction.
using System;
using System.Text;

namespace TS2MP.Shared
{
    public enum StoryTriggerKind : byte { ReadNote = 1, BlackbaySale = 2 }   // BlackbaySale: W6B.3

    // One verified external story trigger: the stage key it advances, the original class, and the object path suffix
    // (scene hierarchy below the scene root, matched as a suffix of the client's transform path).
    public sealed class StoryTrigger
    {
        public readonly string StageKey; public readonly StoryTriggerKind Kind; public readonly string Path; public readonly string Evidence;
        public StoryTrigger(string stageKey, StoryTriggerKind kind, string path, string evidence) { StageKey = stageKey; Kind = kind; Path = path; Evidence = evidence; }
    }

    public static class StoryTriggers
    {
        // Supported Player-2 story actions (W6A slice). Each entry is VERIFIED_ASSET + VERIFIED_IL (docs/research/STORY_SYSTEM.md §4).
        public static readonly StoryTrigger[] All =
        {
            new StoryTrigger("story14", StoryTriggerKind.ReadNote, "Interior_103/story object 103 notka/pPlane3",
                "stage 18 story14; MadisonSt DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3 ReadNote advanceStory, storyDesc story14 (note_103, parcel1_madison); ReadNote.BeginRead IL_015a..IL_0195"),
            // W6B.3: Player 2 may hold the shared mission-box contents (91 black_package) -> its original Blackbay sale advances story16.
            new StoryTrigger("story16", StoryTriggerKind.BlackbaySale, "OTHERS/Viewport/Content/PACKAGE",
                "stage 20 story16; MainScene .../blackbay.xyz/blackbay/BG/WINDOW RIGHT/OTHERS/Viewport/Content/PACKAGE BlackBay_Object on_sell_advance_story, story_name story16, value 250, xp 50; BlackBay_Object.SellIt: sold latch, money (wallet Count), items removed, on_sell_advance_story && current story == story_name -> AdvanceStory"),
        };

        // The listed trigger for (stage key, kind, client path), or null. Path: the client's transform path (any depth),
        // must end with "/" + entry path or equal it.
        public static StoryTrigger Find(string stageKey, StoryTriggerKind kind, string path)
        {
            if (stageKey == null || path == null) return null;
            for (int i = 0; i < All.Length; i++)
            {
                var t = All[i];
                if (t.Kind != kind || !string.Equals(t.StageKey, stageKey, StringComparison.Ordinal)) continue;
                if (string.Equals(path, t.Path, StringComparison.Ordinal)) return t;
                if (path.Length > t.Path.Length && path.EndsWith(t.Path, StringComparison.Ordinal) && path[path.Length - t.Path.Length - 1] == '/') return t;
            }
            return null;
        }

        // Path sent on the wire: the listed suffix (fixed, short) instead of the full client path.
        public static string Canonical(StoryTrigger t) { return t != null ? t.Path : null; }
    }

    // Client proposal "storyAdvance" (Session scope, arg 0): version, from index, trigger kind, stage key, trigger path.
    public struct StoryAdvanceRequest
    {
        public const byte Version = 1;
        public const int MaxKeyChars = 32, MaxPathChars = 96;
        public int FromIndex; public string StageKey; public StoryTriggerKind Kind; public string Path;

        public byte[] ToBytes()
        {
            var k = Encoding.ASCII.GetBytes(StageKey ?? ""); var p = Encoding.ASCII.GetBytes(Path ?? "");
            var b = new byte[6 + k.Length + p.Length];
            b[0] = Version; b[1] = (byte)(FromIndex & 0xFF); b[2] = (byte)((FromIndex >> 8) & 0xFF); b[3] = (byte)Kind;
            b[4] = (byte)k.Length; Buffer.BlockCopy(k, 0, b, 5, k.Length);
            b[5 + k.Length] = (byte)p.Length; Buffer.BlockCopy(p, 0, b, 6 + k.Length, p.Length);
            return b;
        }

        public static bool TryParse(byte[] b, out StoryAdvanceRequest r)
        {
            r = default(StoryAdvanceRequest);
            if (b == null || b.Length < 8 || b[0] != Version || (b[3] != (byte)StoryTriggerKind.ReadNote && b[3] != (byte)StoryTriggerKind.BlackbaySale)) return false;
            int kl = b[4]; if (kl < 1 || kl > MaxKeyChars || 5 + kl >= b.Length) return false;
            int pl = b[5 + kl]; if (pl < 1 || pl > MaxPathChars || 6 + kl + pl != b.Length) return false;
            string k, p;
            if (!StoryState.TryText(b, 5, kl, false, out k) || !StoryState.TryText(b, 6 + kl, pl, true, out p)) return false;
            r = new StoryAdvanceRequest { FromIndex = b[1] | (b[2] << 8), Kind = (StoryTriggerKind)b[3], StageKey = k, Path = p };
            return true;
        }
        public override string ToString() { return "story " + FromIndex + " '" + StageKey + "' via " + Kind + " '" + Path + "'"; }
    }

    // Shared record "story" (Session scope, arg 0): version, index, last advance by (0 = host campaign / load, 1, 2), key.
    public sealed class StoryState
    {
        public const byte Version = 1;
        public int Index; public byte By; public string Key;

        public byte[] ToBytes()
        {
            var k = Encoding.ASCII.GetBytes(Key ?? "");
            var b = new byte[5 + k.Length];
            b[0] = Version; b[1] = (byte)(Index & 0xFF); b[2] = (byte)((Index >> 8) & 0xFF); b[3] = By; b[4] = (byte)k.Length;
            Buffer.BlockCopy(k, 0, b, 5, k.Length);
            return b;
        }

        public static bool TryParse(byte[] b, out StoryState s)
        {
            s = null;
            if (b == null || b.Length < 6 || b[0] != Version || b[3] > 2) return false;
            int kl = b[4]; string k;
            if (kl < 1 || kl > StoryAdvanceRequest.MaxKeyChars || 5 + kl != b.Length || !TryText(b, 5, kl, false, out k)) return false;
            s = new StoryState { Index = b[1] | (b[2] << 8), By = b[3], Key = k };
            return true;
        }

        // Printable ASCII; spaces only inside paths (scene object names contain spaces), never leading / trailing.
        internal static bool TryText(byte[] b, int o, int len, bool spaces, out string s)
        {
            s = null; var c = new char[len];
            for (int i = 0; i < len; i++)
            {
                byte x = b[o + i];
                if (x == 0x20 ? !spaces || i == 0 || i == len - 1 : x < 0x21 || x > 0x7E) return false;
                c[i] = (char)x;
            }
            s = new string(c); return true;
        }

        public bool SameAs(StoryState o) { return o != null && o.Index == Index && string.Equals(o.Key, Key, StringComparison.Ordinal); }
        public override string ToString() { return "story " + Index + " '" + Key + "'" + (By != 0 ? " (last advance by P" + By + ")" : ""); }
    }

    // Host plugin side: the host's own StoryManager.
    public interface IStoryHost
    {
        bool TryCurrent(out int index, out string key, out bool freeMode);   // false while the host scene loads / no story begun
        bool Advance(int expectedIndex);                                     // original StoryManager.AdvanceStory() on the host, once
    }

    public static class StoryRules
    {
        // StoryScript flags cleared on Player 2's PC for its presentation-only InitiateStory (restored right after).
        // advance_if_home / advance_if_jail: InitiateStory IL_0053 / IL_0091 story++ from Player 2's location would run ahead of
        // the host (whose location decides, original); police_chase_on_start IL_01d9 and first_mission IL_023f are actor effects
        // of the host's campaign step (chase / teleport of player + main car); deduct_debt IL_0330 is money (host campaign).
        public static readonly string[] ClientStrippedFlags = { "advance_if_home", "advance_if_jail", "police_chase_on_start", "first_mission", "deduct_debt" };

        // Host decision for one Player-2 story trigger. Exactly once: only from the host's live index and stage key.
        public static ProposalCode Decide(bool hostReady, bool freeMode, int hostIndex, string hostKey, StoryAdvanceRequest r, out string why)
        {
            why = null;
            if (!hostReady) { why = "host story not ready (scene loading)"; return ProposalCode.Busy; }
            var t = StoryTriggers.Find(r.StageKey, r.Kind, r.Path);
            if (t == null) { why = "trigger not listed for stage '" + r.StageKey + "'"; return ProposalCode.Invalid; }
            if (freeMode) { why = "free mode: AdvanceStory does nothing (original)"; return ProposalCode.Unchanged; }
            if (r.FromIndex != hostIndex) { why = "stale: host story is " + hostIndex + " '" + hostKey + "', request from " + r.FromIndex; return ProposalCode.Stale; }
            if (!string.Equals(hostKey, r.StageKey, StringComparison.Ordinal)) { why = "host stage '" + hostKey + "' is not '" + r.StageKey + "' (original condition false)"; return ProposalCode.Stale; }
            return ProposalCode.Accepted;
        }

        // Client: which host index to follow. Returns false when nothing is to do.
        public static bool ClientShouldFollow(int localIndex, bool localBegun, StoryState shared) { return localBegun && shared != null && shared.Index != localIndex; }
    }

    // W6B cooperative story phone call. Original (IL 1.27c, docs/research/STORY_SYSTEM.md section 6):
    //   StoryManager.InitiateStory: call_sequence.PhoneCall() (tryToCall = true) unless call_only_once and <key>_Call >= 1.
    //   StorySequence.Update: tryToCall -> (player free) SPrefs <key>_Call = 1; bypassCall ? (pickUp.Play, Beginn) : PhoneCall1
    //     (caller id, ringtone, vibrate, isCalling, PlayerScript.StartPhoneCall). isCalling + accept_call -> isCalling = false,
    //     pickUp.Play, Beginn (dialogue: speakSource clip, subtitles, nextSequence chain, end: GM.CheckStory), vibrate stop,
    //     PlayerScript.StopPhoneCall. cancel_phone -> ForceStop (personal: not shared).
    //   No campaign effect lives in the answer: <key>_Call is written when the ring starts (World key, host campaign); the
    //   dialogue and CheckStory are presentation on each PC. So the shared part is only "this stage's call was answered".
    // Identity: stage key + index of the sequence in GameManager.StorySequences (the same MainScene data on both PCs).
    public sealed class StoryCallState
    {
        public const byte Version = 1;
        public string Key; public byte Seq; public byte By;   // By: 1 host, 2 Player 2

        public byte[] ToBytes()
        {
            var k = Encoding.ASCII.GetBytes(Key ?? "");
            var b = new byte[4 + k.Length];
            b[0] = Version; b[1] = Seq; b[2] = By; b[3] = (byte)k.Length; Buffer.BlockCopy(k, 0, b, 4, k.Length);
            return b;
        }

        public static bool TryParse(byte[] b, out StoryCallState s)
        {
            s = null;
            if (b == null || b.Length < 5 || b[0] != Version || b[2] < 1 || b[2] > 2) return false;
            int kl = b[3]; string k;
            if (kl < 1 || kl > StoryAdvanceRequest.MaxKeyChars || 4 + kl != b.Length || !StoryState.TryText(b, 4, kl, false, out k)) return false;
            s = new StoryCallState { Seq = b[1], By = b[2], Key = k };
            return true;
        }

        public bool SameCall(StoryCallState o) { return o != null && o.Seq == Seq && string.Equals(o.Key, Key, StringComparison.Ordinal); }
        public override string ToString() { return "call '" + Key + "' sequence " + Seq + " answered by P" + By; }
    }

    public enum CallMirror : byte { None, AnswerRinging, AnswerWhenRung }

    public static class StoryCallRules
    {
        // Host decision for "this stage's call was answered" (by either player). Exactly one committed answer per call.
        public static ProposalCode Decide(bool hostReady, bool freeMode, string hostKey, StoryCallState committed, StoryCallState req, out string why)
        {
            why = null;
            if (!hostReady) { why = "host story not ready"; return ProposalCode.Busy; }
            if (req == null || string.IsNullOrEmpty(req.Key)) { why = "malformed"; return ProposalCode.Invalid; }
            if (freeMode) { why = "free mode: no story calls"; return ProposalCode.Unchanged; }
            if (!string.Equals(hostKey, req.Key, StringComparison.Ordinal)) { why = "stale: host stage '" + hostKey + "', call of '" + req.Key + "'"; return ProposalCode.Stale; }
            if (committed != null && committed.SameCall(req)) { why = "already answered by P" + committed.By; return ProposalCode.Unchanged; }
            return ProposalCode.Accepted;
        }

        // What this PC does with a committed answer of the OTHER player. Only for its current stage and the same sequence,
        // never after this PC answered / is talking itself: ringing -> the original answer branch; ring pending (tryToCall,
        // player busy) -> the original bypassCall branch when the ring would start (no ring, dialogue at once).
        public static CallMirror Decide(StoryCallState committed, byte localSlot, string localKey, int seqIndex, bool isCalling, bool tryToCall, bool inConversation)
        {
            if (committed == null || committed.By == localSlot || seqIndex < 0 || seqIndex != committed.Seq) return CallMirror.None;
            if (!string.Equals(committed.Key, localKey, StringComparison.Ordinal) || inConversation) return CallMirror.None;
            if (isCalling) return CallMirror.AnswerRinging;
            if (tryToCall) return CallMirror.AnswerWhenRung;
            return CallMirror.None;
        }
    }
}
