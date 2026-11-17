// W2B: shared parcel-locker codes (pure, Unity-free). Unity side: src/Plugin/ParcelSync.cs; locker doors: region-object kind
// ParcelLocker (src/Shared/RegionState.cs, src/Plugin/WorldObjectSync.cs). Investigation: docs/research/PARCEL_SYSTEM.md.
//
// Original (Cecil IL 1.27c, verified 2026-11-17):
//   ParcelStorage (MainScene "MANAGERS/PARCEL STORAGE", GameManager.ParcelStorag): List<ParcelCode> parcelCodes, 14 entries
//     {parcel_code_pref, parcel_code, location, lockerTname, lockerTransform, discoveredOnMap, wasOpened}.
//   ReadNote.BeginRead IL_0024..IL_00dd (addParcelCode): first ParcelCode with parcel_code_pref == parcel_pref, not
//     discoveredOnMap and SPrefs.GetInt(pref) == 0 -> discoveredOnMap = true; Inventory.AddNewParcelCode() (phone "new code"
//     animation flag only); GameManager.parcel_strings_to_set.Add(pref). IL_00f9.. achievement / XP + SPrefs(<note name>) /
//     story (advanceStory: StoryManager.AdvanceStory when the current story_description == storyDesc).
//   ItemID.HackedElectronic IL_0021..IL_005c: hacked_parcel_pref != "" and SPrefs.GetInt(pref) < 1 ->
//     parcel_strings_to_set.Add(pref) (no discoveredOnMap: usable after the next save + load, GameManager.LoadParcels).
//   SaveGame.SaveGameNewSystem IL_04c5..: parcel_strings_to_set -> SPrefs 1; mission_strings_to_set -> SPrefs 2 (parcel taken,
//     Pickupable lock_parcel_after_pickup). GameManager.LoadParcels / ParcelContainer.Start: SPrefs == 1 -> discoveredOnMap.
//   ParcelMinigame.PressNumberButton IL_00a1..IL_0187: 5 digits == parcel_code, its ParcelLocker not wasOpened and the code
//     discoveredOnMap -> Inventory.parcelsOpened++, ParcelCode.OpenLocker() (wasOpened on the code and its ParcelLocker),
//     ParcelContainer.openSound, QuitMinigame. Else "wrong_code".
//
// Multiplayer: the host's campaign is the only durable copy (its parcel_strings_to_set -> its save). The shared state
// "parcels" = the set of prefs whose ParcelCode is discoveredOnMap on the HOST (re-derived by the host, never merged from
// the client). Player 2's own original discovery runs locally and is proposed ("parcelCode": source + pref); the host
// applies exactly the original mutation of that source to its campaign (Note: ReadNote IL_00b6..IL_00d8 incl. the
// original "new code" animation; Hack: HackedElectronic IL_0041..IL_005c) and re-publishes. Every PC sets
// discoveredOnMap = true for each shared pref (never false), so the original minigame accepts the code on both PCs.
// Trust boundary (as for minigames): the host cannot verify that Player 2 really read the note / hacked the item.
using System;
using System.Collections.Generic;
using System.Text;

namespace TS2MP.Shared
{
    public enum ParcelSource : byte { Note = 1, Hack = 2 }

    public struct ParcelCodeRequest
    {
        public const int MaxPrefChars = 48;
        public ParcelSource Source; public string Pref;
        public byte[] ToBytes()
        {
            var p = Encoding.ASCII.GetBytes(Pref ?? "");
            var b = new byte[2 + p.Length]; b[0] = (byte)Source; b[1] = (byte)p.Length; Buffer.BlockCopy(p, 0, b, 2, p.Length);
            return b;
        }
        public static bool TryParse(byte[] b, out ParcelCodeRequest r)
        {
            r = default(ParcelCodeRequest);
            if (b == null || b.Length < 3 || (b[0] != (byte)ParcelSource.Note && b[0] != (byte)ParcelSource.Hack) || b[1] != b.Length - 2 || b[1] > MaxPrefChars) return false;
            string s;
            if (!ParcelState.TryAscii(b, 2, b[1], out s)) return false;
            r = new ParcelCodeRequest { Source = (ParcelSource)b[0], Pref = s };
            return true;
        }
        public override string ToString() { return Source + " '" + Pref + "'"; }
    }

    // Shared record "parcels" (Session scope, arg 0): version 1, count, then length-prefixed ASCII prefs (sorted, unique).
    public sealed class ParcelState
    {
        public const byte Version = 1;
        public const int MaxCodes = 20;   // 2 + 20 x (1 + 48) = 982 B <= StateEntry.MaxValueBytes (1.27c has 14)
        public readonly List<string> Discovered = new List<string>();

        public bool Has(string pref) { return Discovered.BinarySearch(pref, StringComparer.Ordinal) >= 0; }

        public static ParcelState Of(IEnumerable<string> prefs)
        {
            var s = new ParcelState();
            foreach (var p in prefs)
            {
                if (s.Discovered.Count >= MaxCodes) break;
                if (ParcelRules.ValidPref(p) && !s.Has(p)) { s.Discovered.Add(p); s.Discovered.Sort(StringComparer.Ordinal); }
            }
            return s;
        }

        public byte[] ToBytes()
        {
            int n = 2; foreach (var p in Discovered) n += 1 + p.Length;
            var b = new byte[n]; b[0] = Version; b[1] = (byte)Discovered.Count; int o = 2;
            foreach (var p in Discovered) { b[o++] = (byte)p.Length; for (int i = 0; i < p.Length; i++) b[o++] = (byte)p[i]; }
            return b;
        }

        public static bool TryParse(byte[] b, out ParcelState s)
        {
            s = null;
            if (b == null || b.Length < 2 || b[0] != Version || b[1] > MaxCodes) return false;
            var r = new ParcelState(); int o = 2;
            for (int i = 0; i < b[1]; i++)
            {
                if (o >= b.Length) return false;
                int len = b[o++]; string p;
                if (len < 1 || len > ParcelCodeRequest.MaxPrefChars || o + len > b.Length || !TryAscii(b, o, len, out p)) return false;
                o += len;
                if (r.Discovered.Count > 0 && string.CompareOrdinal(r.Discovered[r.Discovered.Count - 1], p) >= 0) return false;   // sorted, unique
                r.Discovered.Add(p);
            }
            if (o != b.Length) return false;
            s = r; return true;
        }

        internal static bool TryAscii(byte[] b, int o, int len, out string s)
        {
            s = null; var c = new char[len];
            for (int i = 0; i < len; i++) { byte x = b[o + i]; if (x < 0x21 || x > 0x7E) return false; c[i] = (char)x; }
            s = new string(c); return ParcelRules.ValidPref(s);
        }

        public bool SameAs(ParcelState o)
        {
            if (o == null || o.Discovered.Count != Discovered.Count) return false;
            for (int i = 0; i < Discovered.Count; i++) if (!string.Equals(Discovered[i], o.Discovered[i], StringComparison.Ordinal)) return false;
            return true;
        }
        public override string ToString() { return Discovered.Count + " code(s)" + (Discovered.Count > 0 ? " [" + string.Join(", ", Discovered.ToArray()) + "]" : ""); }
    }

    // Host plugin side of the shared codes (the host's own GameManager / ParcelStorage / SPrefs).
    public interface IParcelHost
    {
        bool TryDiscovered(out List<string> prefs);                 // host live discoveredOnMap set (false while the host scene loads)
        bool Known(string pref);                                    // pref is a ParcelCode of the host's ParcelStorage
        ParcelApply Apply(byte slot, ParcelSource source, string pref);   // original mutation of that source on the host campaign
    }
    public enum ParcelApply : byte { Applied, AlreadyKnown, NotAllowed }

    public static class ParcelRules
    {
        public static bool ValidPref(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length > ParcelCodeRequest.MaxPrefChars) return false;
            for (int i = 0; i < p.Length; i++) if (p[i] < 0x21 || p[i] > 0x7E) return false;
            return true;
        }

        // ReadNote.BeginRead IL_0081..IL_00a3: discovers only while not discoveredOnMap and SPrefs(pref) == 0.
        public static bool NoteDiscovers(bool discoveredOnMap, int pref) { return !discoveredOnMap && pref == 0; }
        // ItemID.HackedElectronic IL_0033..IL_003f: queues the pref while SPrefs(pref) < 1 (discoveredOnMap untouched).
        public static bool HackQueues(int pref) { return pref < 1; }

        // Host decision for one Player-2 discovery (the apply itself is IParcelHost.Apply).
        public static ProposalCode Decide(bool hostReady, bool known, out string why)
        {
            why = null;
            if (!hostReady) { why = "host campaign not ready"; return ProposalCode.Busy; }
            if (!known) { why = "no such parcel code in the host's ParcelStorage"; return ProposalCode.Invalid; }
            return ProposalCode.Accepted;
        }

        public static ProposalCode Code(ParcelApply a, out string why)
        {
            switch (a)
            {
                case ParcelApply.Applied: why = null; return ProposalCode.Accepted;
                case ParcelApply.AlreadyKnown: why = "already known to the host campaign (original condition false)"; return ProposalCode.Unchanged;
                default: why = "host apply failed"; return ProposalCode.Busy;
            }
        }
    }
}
