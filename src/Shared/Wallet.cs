// W4.1B shared money: ONE balance for both players, owned by the host (pure logic, offline-tested; Unity side:
// src/Plugin/WalletSync.cs + the Player-2 record path in src/Plugin/Player2Sync.cs).
//
// Verified original model (Cecil IL, Assembly-CSharp 1.27c, W4.1B audit):
//  - Money = PlayerScript.moneyAm (int field, no setter). 55 stfld sites in 40 methods write it directly (table: WalletPolicy).
//    Persistence: SaveGame.LoadGame IL_0122 moneyAm = SPrefs "Money" (+ PlayerPrefs "MoneyMinigame" IL_02e9); the only
//    writer of the "Money" key during play is SaveGame.SaveGameNewSystem (after moneyAm -= carDamagesValue, IL_035a).
//    SaveGame.Start IL_001c calls LoadGame; GameManager.loadedMap is set later (<ContinueButtonActivate> IL_0094).
//  - Most purchase methods deduct WITHOUT checking the balance (Shop_Manager.Buy_Item IL_027c, Inventory.BuyGarage IL_001d,
//    Whiplist.Upgrade* IL_0032): affordability is decided by the shop / UI before the call.
//
// Ownership: the host's PlayerScript.moneyAm IS the shared balance (persisted by the host's original saves, rolled back by an
// original checkpoint reload together with Player 1's world). The client's moneyAm is a mirror:
//      display = Balance + (Local - P2Wallet)
// Balance / P2Wallet come from ONE host-committed registry entry "wallet" (so they are always a consistent pair), Local is the
// client's cumulative counter of its own original money changes ("Wallet" in the Player-2 record, reported with the backpack
// in the same atomic p2report). The host applies rep.Wallet - rec.Wallet to its moneyAm in the same durable commit that
// stores the report (idempotent: a duplicate / retransmitted report has delta 0). Each applied Player-2 delta is journaled
// with a wallet sequence in the p2inv file; the host save memory gets the watermark "TS2MP_WalletTxn" exactly when the
// original writes "Money" (SPrefs.SetInt prefix), so after a load the deltas the loaded save does not contain are replayed -
// Player 2's durable inventory and its money effect never diverge (a host checkpoint reload rolls back only Player 1).
// Purchases: the client's Shop_Manager.Buy_Item also counts the price in "Spend". A report whose Spend grew is accepted only
// if the shared balance covers its net delta; otherwise it is refused (Stale) and the client restores the host record - the
// purchased item and the money return together (the report is one snapshot). The host's own purchases are never refused
// (it is the authority), so simultaneous purchases can never overspend: the later client purchase is rolled back.
// Migration: Player 2's previous personal balance (record field Money, protocol <= 26) is never added to the shared balance;
// it stays in the record untouched (dormant, never restored, never spendable). The shared balance starts at the host's
// campaign money. Player 2's legacy bank balance is frozen the same way (client Darkbank refused).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    // Registry "wallet" (Session scope, arg 0), host-committed only: the shared balance and the Player-2 record counter it contains.
    public struct WalletState
    {
        public const int WireSize = 16;
        public int RecId;      // Player-2 record id (P2Inventory.Id) the P2Wallet value belongs to
        public int Balance;    // host PlayerScript.moneyAm = the shared balance
        public int P2Wallet;   // record Wallet contained in Balance
        public uint Seq;       // host publish counter (log correlation)
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(RecId); w.Put(Balance); w.Put(P2Wallet); w.Put(Seq); return w.CopyData(); }
        public static bool TryParse(byte[] v, out WalletState s)
        {
            s = default(WalletState);
            if (v == null || v.Length != WireSize) return false;
            s.RecId = BitConverter.ToInt32(v, 0); s.Balance = BitConverter.ToInt32(v, 4); s.P2Wallet = BitConverter.ToInt32(v, 8); s.Seq = BitConverter.ToUInt32(v, 12);
            return s.RecId != 0;
        }
        public override string ToString() { return "balance " + Balance + " (P2 counter " + P2Wallet + ", record " + RecId + ", #" + Seq + ")"; }
    }

    // One applied Player-2 money delta (p2inv v5). Seq = wallet sequence (watermark "TS2MP_WalletTxn" in the host save).
    public struct WalletEntry { public uint Seq; public int Delta; }

    public static class WalletRules
    {
        public const int MaxDelta = 50000000;        // one report / one pickup never moves more than this (sanity, not an economy rule)
        public const int MaxLootMoney = 1000000;     // Pickupable.add_money sanity bound
        public const string MarkKey = "TS2MP_WalletTxn";

        static int Clamp(long v) { return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v; }

        // Client mirror value of the shared balance.
        public static int Display(WalletState s, int local) { return Clamp((long)s.Balance + local - s.P2Wallet); }

        // Host: the money side of a Player-2 report, decided BEFORE the record is replaced. balance = the host's current shared
        // balance (balanceKnown false while the host scene is loading). Purchases (Spend grew) must be covered by the balance;
        // every other change (earnings, the original penalties) is applied as the original applied it, even below zero.
        public static ProposalCode DecideReport(P2Inventory rec, P2Inventory rep, long balance, bool balanceKnown, out int delta, out string why)
        {
            delta = 0; why = null;
            long d = (long)rep.Wallet - rec.Wallet, spend = (long)rep.Spend - rec.Spend;
            if (d > MaxDelta || d < -MaxDelta) { why = "money change " + d + " out of range"; return ProposalCode.Invalid; }
            if (spend < 0) { why = "purchase counter went backwards"; return ProposalCode.Invalid; }
            if (spend > 0 && -d > spend + MaxDelta) { why = "inconsistent purchase counters"; return ProposalCode.Invalid; }
            delta = (int)d;
            if (spend > 0)
            {
                if (!balanceKnown) { why = "host balance not available (scene loading)"; return ProposalCode.Busy; }
                if (balance + d < 0) { why = "insufficient shared funds: balance " + balance + ", purchase net " + d; return ProposalCode.Stale; }
            }
            return ProposalCode.Accepted;
        }

        // Host balance after applying a delta (int range, like the original field).
        public static int Apply(int balance, int delta) { return Clamp((long)balance + delta); }
    }

    // Client-side mirror state (pure; the plugin reads/writes PlayerScript.moneyAm). Inactive until the scene's Player-2 record
    // was restored; a new scene deactivates it (the original LoadGame writes the sandbox value, which is never a delta).
    public sealed class ClientWallet
    {
        public bool Active { get; private set; }
        public int RecId { get; private set; }
        public int Local { get; private set; }        // cumulative counter reported as P2Inventory.Wallet
        public int Spend { get; private set; }        // cumulative Buy_Item prices reported as P2Inventory.Spend
        public int Seen { get; private set; }         // last moneyAm value this mirror knows (own writes included)
        int _armed; double _armedUntil;               // a pending local credit that must not count (HiddenItem +100)
        public long Counted, Reverted, MirrorWrites, Purchases;

        public void Restore(int recId, int recWallet, int recSpend, int moneyNow)
        {
            RecId = recId; Local = recWallet; Spend = recSpend; Seen = moneyNow; Active = true; _armed = 0;
        }
        public void Deactivate() { Active = false; _armed = 0; }

        // A positive local change of up to 'amount' within 'seconds' is not Player 2's to keep (original credit from a world
        // object that is not shared): it is reverted locally instead of counted.
        public void ArmExclusion(int amount, double now, double seconds) { if (Active && amount > 0) { _armed += amount; _armedUntil = now + seconds; } }
        public void Disarm() { _armed = 0; }
        public int Armed { get { return _armed; } }

        // One observation of the live field. Returns the value the field must have afterwards (= moneyNow unless an armed
        // exclusion reverts part of the change). Every other difference since the last observation is Player 2's own change.
        public int Observe(int moneyNow, double now)
        {
            if (!Active) return moneyNow;
            if (_armed > 0 && now > _armedUntil) _armed = 0;
            long d = (long)moneyNow - Seen;
            if (d == 0) return moneyNow;
            int target = moneyNow;
            if (d > 0 && _armed > 0)
            {
                int take = (int)Math.Min(d, _armed);
                _armed -= take; d -= take; target = moneyNow - take; Reverted += take;
            }
            if (d != 0) { Local = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)Local + d)); Counted++; }
            Seen = target;
            return target;
        }

        // Shop_Manager.Buy_Item finished with this price (> 0); the money change itself is counted by Observe first.
        public void Purchase(int price) { if (Active && price > 0) { Spend = (int)Math.Min(int.MaxValue, (long)Spend + price); Purchases++; } }

        // The mirror value for the current host entry, or false (inactive / entry of another record).
        public bool Target(WalletState s, out int display)
        {
            display = 0;
            if (!Active || s.RecId == 0 || s.RecId != RecId) return false;
            display = WalletRules.Display(s, Local);
            return true;
        }
        public void Wrote(int value) { Seen = value; MirrorWrites++; }
    }

    // What the client does with an original money writer during a multiplayer session (the host always runs the original).
    public enum MoneyPolicy : byte
    {
        Count,     // Player 2's own action: the change goes into the shared balance
        Purchase,  // counted + purchase counter (host checks the balance; refused -> record restored, item and money return)
        Exclude,   // the original runs, its money change is reverted locally and never credited (system not shared yet)
        Refuse,    // the original method does nothing on the client (it buys host / world-owned state that Player 2 cannot own)
        HostTxn,   // V1.2: a host transaction of another subsystem (TravelHooks: Tutorial.BuyVan -> "vbuy"); the host charges the
                   // shared balance once, the client never changes money itself. Refused like Refuse when that hook is missing.
        Load       // original load/save plumbing: replaced by the mirror (never a delta)
    }

    public struct MoneyWriter
    {
        public string Type, Method; public MoneyPolicy Policy; public string Evidence, Reason;
        public MoneyWriter(string t, string m, MoneyPolicy p, string evidence, string reason) { Type = t; Method = m; Policy = p; Evidence = evidence; Reason = reason; }
    }

    // Every original stfld PlayerScript::moneyAm (Cecil scan of Assembly-CSharp 1.27c: 55 sites / 40 methods), classified.
    public static class WalletPolicy
    {
        public static readonly MoneyWriter[] Writers =
        {
            // ---- earnings / costs of Player 2's own actions (counted) ----
            new MoneyWriter("Pickupable/<PickingItem>d__72", "MoveNext", MoneyPolicy.Count, "IL_034d += add_money", "world money: host transaction first (WorldLootSync); unsupported world money is refused before the pickup"),
            new MoneyWriter("Inventory", "Sell_Item_NewSystem", MoneyPolicy.Count, "IL_014d", "sale of Player 2's own item (record snapshot: item and money together)"),
            new MoneyWriter("BlackBay_Object", "SellIt", MoneyPolicy.Count, "IL_0031", "sale of Player 2's own items (existing backpack guard)"),
            new MoneyWriter("Shop_Manager", "Buy_Item", MoneyPolicy.Purchase, "IL_027c -= Shop_Button.value (no balance check inside)", "item goes into Player 2's record"),
            new MoneyWriter("GameManager", "GotCaught", MoneyPolicy.Count, "IL_00a6", "original penalty of Player 2"),
            new MoneyWriter("CarController", "OnCollisionEnter", MoneyPolicy.Count, "IL_0177 (is_sitting_inside)", "repair cost of the car Player 2 drives"),
            new MoneyWriter("CarCrash", "OnCollisionEnter", MoneyPolicy.Count, "IL_010a", "crash cost of Player 2"),
            new MoneyWriter("SaveGame", "SaveGameNewSystem", MoneyPolicy.Count, "IL_035a -= carDamagesValue", "travel damage cost (captured again after the original)"),
            new MoneyWriter("SaveGame", "Update", MoneyPolicy.Count, "IL_01bd fuel refill 70/200", "fuel refill of Player 2; the rent branch IL_0112/012b is excluded (host pays rent)"),
            new MoneyWriter("PlayerScript", "Update", MoneyPolicy.Count, "IL_4c87 drink -5, IL_4d7c food -25, IL_5466 cost, IL_57db refuel", "consumables / services of Player 2; IL_73de HiddenItem +100 excluded"),
            new MoneyWriter("PlayerScript", "CrowbarHit", MoneyPolicy.Count, "IL_0595 -200", "original penalty of Player 2"),
            new MoneyWriter("PlayerScript", "SpawnAtCar", MoneyPolicy.Count, "IL_0013 -carFastTravelPrice", "phone fast travel of Player 2 (TravelHooks guard unchanged)"),
            new MoneyWriter("PlayerScript", "InviteThief", MoneyPolicy.Count, "IL_0157 -inviteAssociateCost", "service ordered by Player 2"),
            new MoneyWriter("ResetRating", "Update", MoneyPolicy.Count, "IL_00bb -500", "service ordered by Player 2"),
            new MoneyWriter("PhoneButton", "UseButton", MoneyPolicy.Count, "IL_00e3..054b (checks moneyAm before most)", "phone services of Player 2 (method also does non-money work: never refused)"),
            // ---- not shared yet: the original runs, the money change is reverted on the client ----
            new MoneyWriter("SaveGame", "Update", MoneyPolicy.Exclude, "IL_0112 rent 600 / IL_012b rent 150, timeTillRentPayment = 168", "rent is a household cost charged once by the host"),
            new MoneyWriter("SaveGame", "SaveGameNewSystem", MoneyPolicy.Exclude, "IL_036b-0374 if (moneyAm < 0) moneyAm = 0", "the negative-money clamp belongs to the shared balance: the host's own saves apply it"),
            new MoneyWriter("Mission_Item", "MissionComplete1", MoneyPolicy.Exclude, "IL_0087", "missions / story are host-owned, not synchronized (would be repeatable)"),
            new MoneyWriter("JobOffer", "ClaimRewards", MoneyPolicy.Exclude, "IL_0013 += pay", "jobs are host-owned, not synchronized"),
            new MoneyWriter("ContractUIObj", "ClaimReward", MoneyPolicy.Exclude, "IL_008f / IL_013f", "contracts are host-owned, not synchronized"),
            new MoneyWriter("Payphone", "Success", MoneyPolicy.Exclude, "IL_0092 += PayphoneMission.pay", "payphone missions are host-owned, not synchronized"),
            new MoneyWriter("Pickpocket_Manager", "Success", MoneyPolicy.Exclude, "IL_00b8 += moneyAmount", "NPCs are simulated per instance (both players could rob the same NPC)"),
            new MoneyWriter("PlayerScript", "PickUpMoney", MoneyPolicy.Exclude, "IL_0014 (no caller in Assembly-CSharp)", "unknown world source"),
            new MoneyWriter("HiddenItem", "PickIt", MoneyPolicy.Exclude, "PlayerScript.Update IL_73b0 PickIt, IL_73de += 100", "hidden items are world objects that are not shared"),
            // ---- host / world-owned purchases: refused on the client ----
            new MoneyWriter("Inventory", "BuyGarage", MoneyPolicy.Refuse, "IL_001d -8000", "garage = host world"),
            new MoneyWriter("Whiplist", "DeductColorPrice", MoneyPolicy.Refuse, "IL_001c", "car upgrades are not synchronized"),
            new MoneyWriter("Whiplist", "UpgradeEngine", MoneyPolicy.Refuse, "IL_0032", "car upgrades are not synchronized"),
            new MoneyWriter("Whiplist", "UpgradeBrakes", MoneyPolicy.Refuse, "IL_0032", "car upgrades are not synchronized"),
            new MoneyWriter("Whiplist", "UpgradeTraction", MoneyPolicy.Refuse, "IL_0032", "car upgrades are not synchronized"),
            new MoneyWriter("HouseRenovator", "SetWallpaperBedroom", MoneyPolicy.Refuse, "IL_001e", "hideout = host world"),
            new MoneyWriter("HouseRenovator", "BuyAddition", MoneyPolicy.Refuse, "IL_003e", "hideout = host world"),
            new MoneyWriter("HouseRenovator", "BuyRepairs", MoneyPolicy.Refuse, "IL_0024", "hideout = host world"),
            new MoneyWriter("DroneUpgradePanel", "BuyUpgrade", MoneyPolicy.Refuse, "IL_003d", "drones are special items, not synchronized"),
            new MoneyWriter("DroneTech", "RestoreDrone", MoneyPolicy.Refuse, "IL_00d8", "drones are special items, not synchronized"),
            new MoneyWriter("UpgradeIt", "BuyUpgrade", MoneyPolicy.Refuse, "IL_0045", "upgrade target not owned by the Player-2 record"),
            new MoneyWriter("StockManager", "BuyShares", MoneyPolicy.Refuse, "IL_0042", "shares would be a second, separate holding"),
            new MoneyWriter("StockManager", "SellShares", MoneyPolicy.Refuse, "IL_004e", "shares would be a second, separate holding"),
            new MoneyWriter("Darkbank", "DepositMoney", MoneyPolicy.Refuse, "IL_002d + SPrefs BankBalance", "bank would be a second, separate balance"),
            new MoneyWriter("Darkbank", "WithdrawMoney", MoneyPolicy.Refuse, "IL_002d + SPrefs BankBalance", "bank would be a second, separate balance"),
            new MoneyWriter("Tutorial", "BuyVan", MoneyPolicy.HostTxn, "IL_0063", "fleet unlock = host: V1.2 vbuy host transaction (VehicleSync)"),
            new MoneyWriter("BuyTip", "BuyTipp", MoneyPolicy.Refuse, "IL_0046 (TipPref, habits)", "tips are host campaign knowledge"),
            new MoneyWriter("Inventory", "EndCheatEdit", MoneyPolicy.Refuse, "IL_021b", "cheat edit"),
            // ---- load / save plumbing ----
            new MoneyWriter("SaveGame", "LoadGame", MoneyPolicy.Load, "IL_0122 = SPrefs Money, IL_02e9 += MoneyMinigame", "the client restore sets the mirror (sandbox value / client minigame money are not deltas)"),
        };

        public static IEnumerable<MoneyWriter> With(MoneyPolicy p) { foreach (var w in Writers) if (w.Policy == p) yield return w; }

        // Rent branch of SaveGame.Update (IL_00e2-0136): survival 600, otherwise 150, then timeTillRentPayment = 168.
        public static int RentAmount(bool survival) { return survival ? 600 : 150; }
        public const int RentResetHours = 168;
        public static bool RentCharged(int timerBefore, int timerAfter) { return timerBefore <= 0 && timerAfter == RentResetHours; }
        public const int HiddenItemReward = 100;
        // SaveGameNewSystem on the client: keep the travel damage cost (IL_035a), undo the negative clamp (IL_0374). Returns the
        // value moneyAm must have after the original (before = moneyAm at the prefix, damages = GameManager.carDamagesValue).
        public static int ClientAfterSave(int before, int damages, int after)
        {
            long expected = (long)before - damages;
            if (expected < int.MinValue) expected = int.MinValue;
            return after == 0 && expected < 0 ? (int)expected : after;
        }
    }
}
