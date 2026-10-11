using System;
using ThiefSim2Multiplayer.Game;

internal static class NativeBooleanMemberTests
{
    private sealed class PropertyMenu
    {
        public bool RadialMenuActive { get; private set; }
        internal PropertyMenu(bool active) { RadialMenuActive = active; }
    }

    private sealed class FieldMenu { public bool RadialMenuActive; }
    private sealed class MissingMenu { }

    internal static void Run(Action<bool, string> check)
    {
        check(NativeBooleanMember.Read(new PropertyMenu(false), "RadialMenuActive") == false, "native radial-menu Boolean property reads inactive state");
        check(NativeBooleanMember.Read(new PropertyMenu(true), "RadialMenuActive"), "native radial-menu Boolean property reads active state");
        check(NativeBooleanMember.Read(new FieldMenu { RadialMenuActive = true }, "RadialMenuActive"), "native Boolean field fallback remains available");
        bool missingRejected = false;
        try { NativeBooleanMember.Read(new MissingMenu(), "RadialMenuActive"); }
        catch (MissingMemberException) { missingRejected = true; }
        check(missingRejected, "unknown native Boolean member still fails closed");
    }
}
