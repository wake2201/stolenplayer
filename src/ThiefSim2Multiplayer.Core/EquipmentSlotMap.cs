namespace ThiefSim2Multiplayer.Core
{
    // Proven first-person slots only. This is an adapter mapping, never a wire slot ID.
    public static class EquipmentSlotMap
    {
        public static bool TryGetItem(int slot, out short item)
        {
            item = -1;
            if (slot == 1) { item = 0; return true; }
            return slot == 0 || slot == 99;
        }
    }
}
