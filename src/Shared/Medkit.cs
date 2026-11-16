// V1.4.1: the original single-use car medkit as a host transaction (pure, Unity-free).
// Original (verified IL 1.27c, ilspycmd):
//   Medkit.Start            -> SPrefs "Medkit" == 1 -> Destroy(gameObject) (single use per save; one key for every car's medkit)
//   Medkit.Update (2 s)      -> tag "Medkit" only while myCar.car_door_R.isOpen || (myCar.is_sitting_inside && !myCar.engine_running),
//                               else "Untagged" (reachable through the open right door or by the seated driver with the engine off)
//   PlayerScript.Update IL_729c..IL_7342 (ray hit tag "Medkit", E): isBleeding -> IL_72f0 Heal(); healSound.Play();
//                               Object.Destroy(medkit); SPrefs.SetInt("Medkit", 1)  |  else warning "not_bleeding".
//   PlayerScript.Heal()      -> gotShotObject off, uiBloodObj off, isBleeding = false, bloodSprite.fillAmount = 1.
// Multiplayer: the host's key is the only durable copy. Player 2's use is a proposal ("medkitUse", arg = car id) decided by
// the host exactly once (ProposalGate caches a retransmission; a second use after the commit is Stale); the host commits the
// shared state "medkit" and both PCs remove every medkit object (the original Medkit.Start rule for key == 1). A medkit on a
// travel proxy (the car is in another region) is refused: the host checks that the car's fleet region is the requester's.
using System;

namespace TS2MP.Shared
{
    public struct MedkitState
    {
        public bool Used; public ushort Car; public byte By;   // By: slot that used it (0 = from the host save / unknown)
        public byte[] ToBytes() { return new byte[] { (byte)(Used ? 1 : 0), (byte)(Car & 0xFF), (byte)(Car >> 8), By }; }
        public static bool TryParse(byte[] b, out MedkitState s)
        {
            s = default(MedkitState);
            if (b == null || b.Length != 4 || b[0] > 1 || b[3] > 2) return false;
            s = new MedkitState { Used = b[0] == 1, Car = (ushort)(b[1] | (b[2] << 8)), By = b[3] };
            return true;
        }
        public override string ToString() { return Used ? "used (car#" + Car + " by P" + By + ")" : "available"; }
    }

    public interface IMedkitHost
    {
        bool TryUsed(out bool used);              // host save key "Medkit" == 1 (false while the host scene loads)
        bool Apply(byte slot, ushort car);        // host: original key write + remove the local medkit objects
    }

    public static class MedkitRules
    {
        public const string PrefKey = "Medkit";

        // Host decision for one Player-2 use. carKnown: the fleet has this car; carRegion: its fleet region.
        public static ProposalCode Decide(ushort car, bool carKnown, int carRegion, int requesterRegion, bool used, out string why)
        {
            why = null;
            if (!carKnown) { why = "unknown car"; return ProposalCode.Invalid; }
            if (used) { why = "medkit already used (single use per save)"; return ProposalCode.Stale; }
            if (requesterRegion == Regions.None || carRegion != requesterRegion) { why = "car not here (travel proxy / other region)"; return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        }

        // Seated passenger (first person, original 2.25 m ray on the medkit): reachable when the original rule tags it on this PC
        // (right door open) or, like the original seated driver, while the car's engine is off (host-committed fleet flag).
        public static bool SeatReachable(bool originalTag, bool engineOn) { return originalTag || !engineOn; }
    }
}
