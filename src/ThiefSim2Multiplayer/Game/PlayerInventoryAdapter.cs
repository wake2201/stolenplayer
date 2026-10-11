using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    // Fixed local contracts. Applying quantities does not replay Item_Picked (rewards/save)
    // and never replaces the original Inventory, tools, hands or first-person controllers.
    internal sealed class PlayerInventoryAdapter
    {
        private readonly PlayerLocator locator = new PlayerLocator();
        internal Component Player { get; private set; }
        internal Component Inventory { get; private set; }
        internal readonly Dictionary<ushort, Component> Definitions = new Dictionary<ushort, Component>();
        internal bool Ready => Player && Inventory && Definitions.Count > 0;
        internal bool Refresh()
        {
            Component player = locator.Find(); if (!player) return false;
            if (player == Player && Ready) return true;
            Player = player; Inventory = GameAccess.Get(Player, "invScript") as Component; Definitions.Clear();
            var items = GameAccess.Get(Player, "item_IDs2") as Array;
            if (!Inventory || items == null || items.Length != InventoryLimits.MaxDefinitions) return false;
            foreach (Component definition in items)
            {
                int id = (int)GameAccess.Get(definition, "item_ID");
                if (id < 0 || id >= InventoryLimits.MaxDefinitions || !SupportedDefinition(definition)) continue;
                Definitions.Add((ushort)id, definition);
            }
            return Ready;
        }
        internal static bool SupportedDefinition(Component definition)
        {
            if (!definition) return false;
            // Native keys use the same Item_Picked unit-count contract and personal Keys tab.
            if (GameAccess.Bool(definition, "isKey")) return !GameAccess.Bool(definition,"is_heavy");
            foreach (string field in new[] { "is_heavy", "is_special", "isStoryItem", "isMissionItem", "is_cardboard", "is_hackable", "isKey", "isDrone", "isCar", "car_part" }) if (GameAccess.Bool(definition, field)) return false;
            // Ordinary native unit-count tools share the original ItemID quantity contract.
            int id = (int)GameAccess.Get(definition, "item_ID");

            return GameAccess.Bool(definition, "isLoot") || GameAccess.Bool(definition, "isTool") || id == 0 || id == 3;
        }
        internal IEnumerable<ItemDefinitionRule> Rules => Definitions.Select(p => new ItemDefinitionRule(p.Key, (float)GameAccess.Get(p.Value, "item_weight"), (int)GameAccess.Get(p.Value, "max_amount"), GameAccess.Get(p.Value, "drop_object") is GameObject));
        internal float Capacity => (float)GameAccess.Get(Player, "maxWeight");
        internal float Weight => (float)GameAccess.Get(Player, "currentWeight");
        internal int Amount(ushort definition) => (int)GameAccess.Get(Definitions[definition], "item_amount");
        internal InventoryState CaptureSeed(PersistentCoopPlayerId owner)
        {
            var items = new List<InventoryItemState>();
            foreach (var pair in Definitions.OrderBy(p => p.Key))
            {
                int count = Amount(pair.Key); if (count < 0 || count > InventoryLimits.MaxItems - items.Count) throw new InvalidOperationException("Native inventory exceeds cooperative bounds.");
                for (int i = 0; i < count; i++) items.Add(new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), pair.Key));
            }
            return new InventoryState(owner, 1, Capacity, items);
        }
        internal bool Eligible(ushort id, bool drop = false)
        {
            if (!Ready || !Definitions.ContainsKey(id)) return false;
            if (drop) return Amount(id) > 0 && GameAccess.Get(Definitions[id], "drop_object") is GameObject && !GameAccess.Bool(Inventory, "isShop") && !GameAccess.Bool(Inventory, "isStorage") && !GameAccess.Bool(Player, "is_in_vehicle") && (id != 0 || (int)GameAccess.Get(Player, "crowbarLife") == 100);
            int maximum = (int)GameAccess.Get(Definitions[id], "max_amount");
            return !GameAccess.Bool(Player, "isCarrying") && !GameAccess.Bool(Player, "is_in_vehicle") && GameAccess.Bool(Player, "canInteract") && (maximum <= 0 || Amount(id) < maximum) && Weight + (float)GameAccess.Get(Definitions[id], "item_weight") <= Capacity + 5f + .0001f;
        }
        internal ushort? SelectedDefinition
        { get { var selected = Ready ? GameAccess.Get(Inventory, "current_item_script") as Component : null; if (!selected) return null; int id = (int)GameAccess.Get(selected, "item_ID"); return id >= 0 && id < InventoryLimits.MaxDefinitions ? (ushort?)id : null; } }
        internal Vector3 DropPosition => ((Transform)GameAccess.Get(Inventory, "dropPos")).position;
        internal void Apply(InventoryState state, Action<string> log)
        {
            if (!Ready) throw new InvalidOperationException("Native inventory unavailable.");
            using (new NetworkApplyScope(true))
            {
                foreach (var pair in Definitions)
                {
                    int count = state.Count(pair.Key), before = Amount(pair.Key); if (count == before) continue;
                    if (pair.Key == 0 && count == 0) GameAccess.Call(Player, "HideWeapons");
                    GameAccess.Set(pair.Value, "item_amount", count);
                    log("[TS2MP] Inventory reconcile definition=" + pair.Key + " native=" + before + " authoritative=" + count + " revision=" + state.Revision);
                    var buttons = GameAccess.Get(Inventory, "buttons") as IList;
                    if (buttons == null) continue;
                    bool found = false;
                    foreach (Component button in buttons.Cast<Component>().ToArray())
                    {
                        if (!button || (int)GameAccess.Get(button, "item_ID") != pair.Key) continue;
                        found = true;
                        if (count == 0) { buttons.Remove(button); button.gameObject.SetActive(false); ((IList)GameAccess.Get(Inventory, "disabledButtonsDestroy")).Add(button.gameObject); }
                        else { GameAccess.Set(button, "item_amount", count); GameAccess.Call(button, "LoadAmountText"); }
                    }
                    if (count > 0 && !found && Inventory.gameObject.activeInHierarchy && !GameAccess.Bool(Inventory, "isShop") && !GameAccess.Bool(Inventory, "isStorage"))
                    {
                        GameAccess.Call(pair.Value, "SetTags");
                        Inventory.GetType().GetMethod("InstantiateNewItemX").Invoke(Inventory, new[] { GameAccess.Get(pair.Value, "inventory_button"), (object)(int)pair.Key, count, GameAccess.Get(pair.Value, "tagString"), false });
                    }
                }
                // Preserve quantities of unsupported definitions; recompute weight from actual ItemID units.
                float weight = 0;
                foreach (Component definition in (Array)GameAccess.Get(Player, "item_IDs2")) if (definition && !GameAccess.Bool(definition, "is_heavy")) weight += (int)GameAccess.Get(definition, "item_amount") * (float)GameAccess.Get(definition, "item_weight");
                GameAccess.Set(Player, "currentWeight", weight);
                ReconcileFlashlightOwnership();
                // Restore only the runtime-verified crowbar slot through its original functional path.
                int slot = (int)GameAccess.Get(Player, "currentItem");
                var equipped = state.Equipped.HasValue ? state.Find(state.Equipped.Value) : null;
                if (equipped != null && equipped.Definition == 0 && slot != 1) GameAccess.Call(Player, "SelectCrowbar");
                else if (equipped == null && slot == 1) GameAccess.Call(Player, "HideWeapons");
            }
        }
        internal void ReconcileFlashlightOwnership()
        {
            if (!Ready || !Definitions.ContainsKey(3)) return;
            bool owns = Amount(3) > 0;
            GameAccess.Set(Inventory, "hasFlashlight", owns);
            // PlayerScript.Update already uses this flag to extinguish an unowned torch.
            // Do the same immediately on authoritative application; no slot mapping is inferred.
            if (!owns && GameAccess.Bool(Player, "torch"))
            {
                GameAccess.Set(Player, "torch", false);
                var light = GameAccess.Get(Player, "LightObj") as GameObject;
                if (light) light.SetActive(false);
            }
        }
        internal void Reset() { Player = Inventory = null; Definitions.Clear(); }
    }
}
