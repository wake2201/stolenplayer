// W1: shared hideout ownership - garage, home additions, house repairs, wallpapers / floors. Unity-free (unit-testable);
// the Unity side is src/Plugin/HideoutSync.cs.
//
// Verified original (ilspycmd 1.27c, Assembly-CSharp; scene scan level1 MainScene / level4 PlayerHideout, read-only):
//   Garage: hideout trigger "GARAGE TO DISABLE/Buy garage trigger" (tag BuyGarage) -> Tutorial.OpenBuyGarage (button only
//     if moneyAm >= 8000) -> Inventory.BuyGarage: moneyAm -= 8000, PMB/PCB/camera off, FindObjectOfType<Garage>().BoughtSeq(),
//     BuySound, achievement BUY_GARAGE. Garage.BoughtSeq: buyAnimObj on, garageDisable off, garageEnable on,
//     GM.mission_strings_to_set.Add("garage") (SaveGameNewSystem writes it as 2), GM.boughtGarage = true,
//     DayNightTime.RefreshLightObjects, GarageAnim (4 s, controls back). Load: Garage.Start (garage >= 1 -> swap),
//     GameManager.Start (garage >= 1 -> boughtGarage). No reload.
//   HouseRenovator (MainScene "Computer Canvas/.../houserenovator", GameManager.houseRenovations; opened from the hideout
//     computer "SCENE OBJECTS/HIDEOUT/COMPUTER"):
//     BuyAddition: moneyAm >= additions[n].additionCost -> moneyAm -= cost, MoneyPickSound, SPrefs.SetInt(additionPref, 1),
//       bought = true, SelectAddition, GM.SelectHideout() (save + reload of the hideout). Scene data: AudioUpgrade 1000,
//       AntiTheft 1500, CatStuff 1500, LoreBooks 1500.
//     BuyRepairs: moneyAm >= 2500 -> moneyAm -= 2500, MoneyPickSound, SPrefs.SetInt("HouseRepairs", 1), CheckRepair,
//       GM.SelectHideout(), achievement UPGRADE_HOUSE.
//     SetWallpaperBedroom: moneyAm >= 50 -> moneyAm -= 50, sound, SPrefs.SetInt(prefs[setting], value) (prefs:
//       WallpaperBedroom, FloorBedroom, WallpaperLivingRoom, FloorLivingRoom, FloorStorage), CheckAll on every
//       CheckRenovations (live material change, no reload).
//   Readers (Start only): EnableDisablePref x4 in the hideout (AudioUpgrade radio, LoreBooks compendium, AntiTheft cams,
//     CatStuff), CheckRenovations x67 (HouseRepairs swaps / materials + wallpaper prefs), CatAI.Start (CatStuff XP),
//     HeavyItemsShed.LoadItems (AntiTheft). -> additions and repairs need the original hideout rebuild (SelectHideout),
//     wallpapers / the garage are applied live by the original.
// Multiplayer: the host's campaign is the only owner. One host transaction "hbuy" for every item; one committed state
// "hideout" (8 B) that both instances present; the host applies a client's purchase on its own original state (money,
// queued keys) so money and ownership persist together at the host's next checkpoint.
using System;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum HideoutItem : byte { Garage = 1, Repairs = 2, Addition = 3, Wallpaper = 4 }

    // Client -> host proposal value (key "hbuy", Session scope, arg 0). 3 bytes: item, index, value.
    public struct HideoutBuyRequest
    {
        public HideoutItem Item; public byte Index; public byte Value;   // Addition: Index 0..3; Wallpaper: Index = setting 0..4, Value = material
        public byte[] ToBytes() { return new[] { (byte)Item, Index, Value }; }
        public static bool TryParse(byte[] b, out HideoutBuyRequest r)
        {
            r = default(HideoutBuyRequest);
            if (b == null || b.Length != 3 || b[0] < 1 || b[0] > 4) return false;
            r = new HideoutBuyRequest { Item = (HideoutItem)b[0], Index = b[1], Value = b[2] };
            if (r.Item == HideoutItem.Addition && r.Index >= HideoutShop.AdditionPrefs.Length) return false;
            if (r.Item == HideoutItem.Wallpaper && r.Index >= HideoutShop.WallPrefs.Length) return false;
            if ((r.Item == HideoutItem.Garage || r.Item == HideoutItem.Repairs) && (r.Index != 0 || r.Value != 0)) return false;
            if (r.Item == HideoutItem.Addition && r.Value != 0) return false;
            return true;
        }
        public override string ToString() { return Item + (Item == HideoutItem.Addition ? " " + HideoutShop.AdditionPrefs[Index] : Item == HideoutItem.Wallpaper ? " " + HideoutShop.WallPrefs[Index] + "=" + Value : ""); }
    }

    // Committed shared hideout state (key "hideout", Session scope, arg 0), host-only writer. 8 bytes.
    public struct HideoutState
    {
        public const byte Version = 1;
        public bool Garage, Repairs; public byte Additions;   // bit i = AdditionPrefs[i] owned
        public byte W0, W1, W2, W3, W4;                       // WallPrefs values

        public bool Has(int addition) { return (Additions & (1 << addition)) != 0; }
        public byte Wall(int i) { switch (i) { case 0: return W0; case 1: return W1; case 2: return W2; case 3: return W3; default: return W4; } }
        public void SetWall(int i, byte v) { switch (i) { case 0: W0 = v; break; case 1: W1 = v; break; case 2: W2 = v; break; case 3: W3 = v; break; default: W4 = v; break; } }

        public byte[] ToBytes() { return new byte[] { Version, (byte)((Garage ? 1 : 0) | (Repairs ? 2 : 0)), Additions, W0, W1, W2, W3, W4 }; }
        public static bool TryParse(byte[] b, out HideoutState s)
        {
            s = default(HideoutState);
            if (b == null || b.Length != 8 || b[0] != Version || (b[1] & ~3) != 0 || (b[2] & ~0x0F) != 0) return false;
            s = new HideoutState { Garage = (b[1] & 1) != 0, Repairs = (b[1] & 2) != 0, Additions = b[2], W0 = b[3], W1 = b[4], W2 = b[5], W3 = b[6], W4 = b[7] };
            return true;
        }
        public bool Same(HideoutState o) { return Garage == o.Garage && Repairs == o.Repairs && Additions == o.Additions && W0 == o.W0 && W1 == o.W1 && W2 == o.W2 && W3 == o.W3 && W4 == o.W4; }
        // The original builds additions and repairs only in Start (EnableDisablePref / CheckRenovations / CatAI / HeavyItemsShed)
        // -> a scene built without them needs the original rebuild (SelectHideout). Garage and wallpapers are applied live.
        public bool NeedsRebuild(HideoutState built) { return (Repairs && !built.Repairs) || (Additions & ~built.Additions) != 0; }
        public bool WallsDiffer(HideoutState o) { return W0 != o.W0 || W1 != o.W1 || W2 != o.W2 || W3 != o.W3 || W4 != o.W4; }
        public override string ToString()
        {
            return "garage " + (Garage ? 1 : 0) + " repairs " + (Repairs ? 1 : 0) + " additions 0x" + Additions.ToString("X1") + " walls " + W0 + "/" + W1 + "/" + W2 + "/" + W3 + "/" + W4;
        }
    }

    // Host plugin view of its own original hideout state (MainScene HouseRenovator exists in every region; the Garage and
    // hideout objects only in the hideout - the host never needs them to decide or to persist).
    public interface IHideoutHost
    {
        bool TryBalance(out int balance);                  // shared balance (host moneyAm); false while the host scene loads
        bool TryState(out HideoutState state);             // derived from the host's keys + queued purchases
        int AdditionCost(int index);                       // HouseRenovator.additions[i].additionCost (<= 0 unknown)
        int WallpaperCount(int setting);                   // material array length of that setting (<= 0 unknown)
        bool Apply(HideoutBuyRequest r, int price);        // host: original money field + original key / queue
    }

    public static class HideoutShop
    {
        public const int Region = 0;                       // the garage trigger and the hideout computer exist only in the hideout
        public const int GaragePrice = 8000;               // Inventory.BuyGarage / Tutorial.OpenBuyGarage
        public const int RepairsPrice = 2500;              // HouseRenovator.BuyRepairs
        public const int WallpaperPrice = 50;              // HouseRenovator.SetWallpaperBedroom
        public static readonly string[] AdditionPrefs = { "AudioUpgrade", "AntiTheft", "CatStuff", "LoreBooks" };
        public static readonly int[] AdditionCosts = { 1000, 1500, 1500, 1500 };   // scene data (verification only; the host reads the live table)
        public static readonly string[] WallPrefs = { "WallpaperBedroom", "FloorBedroom", "WallpaperLivingRoom", "FloorLivingRoom", "FloorStorage" };
        public const string GarageKey = "garage", RepairsKey = "HouseRepairs";

        public static int AdditionIndex(string pref) { for (int i = 0; i < AdditionPrefs.Length; i++) if (AdditionPrefs[i] == pref) return i; return -1; }

        // Host decision; nothing changes here. Already owned / same wallpaper -> Stale (never Unchanged: a client treats
        // Unchanged as success and would present a purchase without a charge).
        public static ProposalCode Decide(HideoutBuyRequest r, int requesterRegion, IHideoutHost host, out int price, out string why)
        {
            price = 0; why = null;
            if (requesterRegion != Region) { why = "buyer not in the hideout (" + Regions.Name(requesterRegion) + ")"; return ProposalCode.NotAuthorized; }
            if (host == null) { why = "host hideout not ready"; return ProposalCode.Busy; }
            HideoutState s;
            if (!host.TryState(out s)) { why = "host state unavailable (scene loading)"; return ProposalCode.Busy; }
            switch (r.Item)
            {
                case HideoutItem.Garage:
                    if (s.Garage) { why = "garage already owned"; return ProposalCode.Stale; }
                    price = GaragePrice; break;
                case HideoutItem.Repairs:
                    if (s.Repairs) { why = "repairs already ordered"; return ProposalCode.Stale; }
                    price = RepairsPrice; break;
                case HideoutItem.Addition:
                    if (s.Has(r.Index)) { why = AdditionPrefs[r.Index] + " already owned"; return ProposalCode.Stale; }
                    price = host.AdditionCost(r.Index);
                    if (price <= 0) { why = "addition price unknown"; return ProposalCode.Busy; }
                    break;
                case HideoutItem.Wallpaper:
                    int n = host.WallpaperCount(r.Index);
                    if (n <= 0) { why = "material table unknown"; return ProposalCode.Busy; }
                    if (r.Value >= n) { why = "material " + r.Value + " out of range (" + n + ")"; return ProposalCode.Invalid; }
                    if (s.Wall(r.Index) == r.Value) { why = "already set"; return ProposalCode.Stale; }
                    price = WallpaperPrice; break;
                default: why = "unknown item"; return ProposalCode.Invalid;
            }
            int bal;
            if (!host.TryBalance(out bal)) { why = "host balance unavailable (scene loading)"; return ProposalCode.Busy; }
            if (bal < price) { why = "shared balance " + bal + " < " + price; return ProposalCode.Stale; }
            return ProposalCode.Accepted;
        }
    }

    public enum RebuildStep : byte { None = 0, Wait = 1, Rebuild = 2 }

    // W1.1: the coordinated original rebuild (GameManager.SelectHideout) on the instance that did NOT run the purchase.
    // Pure decision logic; HideoutSync feeds it the scene facts once per state change / retry and performs the call.
    //   "leaving" = GameManager.disablesound: set only by the original scene-leaving paths (LoadWait of every Select*,
    //   LoadCheckpoint, the story ending) and never cleared on that GameManager -> a native load is already under way (an own
    //   purchase, a travel, a caught reload or an earlier rebuild) and the next scene reads the keys itself.
    // Replaces the W1 "once per GameManager" marker, which was also set by purchases that never reload (wallpaper, garage,
    // a refused "too poor" click) and by a call the travel hook did not run, and whose 1 s retry could be consumed by an
    // unrelated registry change (the deferred rebuild then waited for the next commit - often a travel).
    public sealed class HideoutRebuild
    {
        public const double RetrySeconds = 1.0;
        public const int MaxNotStartedPerScene = 3;
        public bool HasBuilt; public HideoutState Built;   // what the current scene was built with (keys at loadedMap)
        public bool Waiting;                               // a needed rebuild is held back: re-evaluate at NextTry without a state change
        public double NextTry;
        bool _requested; HideoutState _target;             // our SelectHideout started the native load for this state
        bool _suppressed; HideoutState _suppress;          // a started rebuild did not bring this state: never repeated for it (no loop)
        int _notStarted;                                   // calls the original did not run in this scene
        public long Requests, Started, NotStarted, Applied, Missing, Waits;

        // A new scene finished its original Start methods (loadedMap). Returns the verdict of our last rebuild, else null.
        public string Bound(HideoutState built)
        {
            HasBuilt = true; Built = built; Waiting = false; NextTry = 0; _notStarted = 0;
            if (!_requested) return null;
            _requested = false;
            if (_target.NeedsRebuild(built)) { _suppressed = true; _suppress = _target; Missing++; return "MISSING"; }
            Applied++; return "APPLIED";
        }

        public RebuildStep Decide(HideoutState committed, bool inHideout, bool ownPending, bool leaving, string busy, double now, out string why)
        {
            why = null; Waiting = false;
            if (!inHideout || !HasBuilt || !committed.NeedsRebuild(Built)) return RebuildStep.None;
            if (leaving) { why = "native load already under way (the next scene reads the keys)"; return RebuildStep.None; }
            if (_suppressed && committed.Same(_suppress)) { why = "an earlier rebuild did not bring this state (not repeated)"; return RebuildStep.None; }
            if (_notStarted >= MaxNotStartedPerScene) { why = "the original did not start the load " + _notStarted + " times in this scene"; return RebuildStep.None; }
            if (ownPending) busy = "own purchase waiting for the host";
            if (busy != null) { why = busy; Waiting = true; NextTry = now + RetrySeconds; Waits++; return RebuildStep.Wait; }
            if (now < NextTry) { why = "retry pending"; Waiting = true; return RebuildStep.Wait; }
            Requests++; return RebuildStep.Rebuild;
        }

        // After the SelectHideout call: loadStarted = GameManager.disablesound became true (LoadWait ran: LoadPanel "ShowLoad").
        public void Invoked(HideoutState target, bool loadStarted, double now)
        {
            if (loadStarted) { Started++; _requested = true; _target = target; Waiting = false; return; }
            NotStarted++; _notStarted++; Waiting = _notStarted < MaxNotStartedPerScene; NextTry = now + RetrySeconds;
        }
    }
}
