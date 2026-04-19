using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── Equipment detach / drop / belongings system ───────────────────────────────
//
// Architecture follows a three-state lifecycle:
//
//   Equipped   →  Loose    →  Detached  →  world DroppedBelonging
//
//   Equipped:  Item is attached to a body slot.  Optionally has bone/cloth
//              follow-through via the spring physics engine.
//   Loose:     Item has been partially dislodged (hat is crooked, shoe is half
//              off).  The detach chance is already resolved — a second check
//              determines whether it fully detaches next tick.  Gives better
//              visual storytelling than instant pop-off.
//   Detached:  Item has been unequipped.  A DroppedBelonging is spawned in the
//              world and simulated by ItemPhysicsWorld until it sleeps or the
//              owner picks it up.
//
// BelongingsManager tracks all live DroppedBelonging instances, handles owner
// auto-pickup within AutoPickupRadius, despawn timers, and re-equip logic.
//
// Design principles:
//   • Each equipment slot has its own DetachProfile — hat behaves nothing like
//     shirt.
//   • An InventoryDropProfile governs whether loose-inventory items can spill.
//   • Items are split into Safe / Loose / Equipped categories.  Safe items
//     (quest items, named protected items) never drop.
//   • A per-event spill budget (MaxInventorySpillPerEvent) prevents one ragdoll
//     from emptying an entire backpack.
//   • All public types are JSON-serialisable for future data-driven overrides.
// ─────────────────────────────────────────────────────────────────────────────

// ── Equipment slot enum ───────────────────────────────────────────────────────

/// <summary>
/// Equipment slots that can participate in the gear knock-off system.
/// </summary>
public enum EquipSlot
{
    /// <summary>Head-mounted item.  High detach chance on backward fall or head hit.</summary>
    Hat,
    /// <summary>Torso clothing.  Low detach chance — mostly torso-pull events.</summary>
    Shirt,
    /// <summary>Leg/hip clothing.  Very low detach chance by default.</summary>
    Pants,
    /// <summary>Footwear.  Medium detach chance on leg/foot impact or tumble.</summary>
    Shoes,
    /// <summary>Actively held weapon or tool.  High chance on stun, disarm, or ragdoll.</summary>
    HeldItem,
    /// <summary>Back-mounted item (backpack, cape).  Medium chance — swings loose first.</summary>
    Back,
    /// <summary>Accessory slot (necklace, scarf, glasses, earrings, charm).</summary>
    Accessory,
    /// <summary>Generic fallback for any unrecognised slot.</summary>
    Other,
}

// ── Item drop category ────────────────────────────────────────────────────────

/// <summary>
/// Categorises inventory items by their vulnerability to spilling.
/// </summary>
public enum ItemDropCategory
{
    /// <summary>
    /// Never drops under any conditions.
    /// Covers quest items, key items, bound items, and user-marked favourites.
    /// </summary>
    Safe,

    /// <summary>
    /// Can spill from inventory during severe ragdoll or explosion events.
    /// Covers consumables, crafting materials, toys, ammo, food, potions.
    /// </summary>
    Loose,

    /// <summary>
    /// Higher drop chance — item is actively worn or held.
    /// Covers equipped weapon, hat, off-hand item, carried objects.
    /// </summary>
    Equipped,
}

// ── Item drop rule ────────────────────────────────────────────────────────────

/// <summary>
/// Named rule set describing under which conditions an item may be dropped.
/// Applied per item category and per equipment slot.
/// </summary>
public enum ItemDropRule
{
    /// <summary>Never drops, regardless of impact force or ragdoll.</summary>
    NeverDrop,

    /// <summary>Drops only on strong ragdoll, explosion, or stun.</summary>
    OnHeavyImpact,

    /// <summary>Drops when the matching body region or held slot receives a direct hit.</summary>
    OnDirectHit,

    /// <summary>Can spill out of inventory during severe knockdown.</summary>
    LooseSpill,
}

// ── Per-slot loosen / detach states ──────────────────────────────────────────

/// <summary>
/// Lifecycle state of a single equipment slot during a potential detach event.
/// Implements "loosen first, detach second" for better visual storytelling.
/// </summary>
public enum ItemEquipState
{
    /// <summary>Item is firmly attached to the slot.  Normal state.</summary>
    Secure,

    /// <summary>
    /// Item has been partially dislodged (hat crooked, shoe half-off, strap slipping).
    /// One more failed check will fully detach it.
    /// </summary>
    Loosened,

    /// <summary>Item has fully detached.  A DroppedBelonging is being spawned.</summary>
    Detached,
}

// ── DetachProfile ─────────────────────────────────────────────────────────────

/// <summary>
/// Per-slot detach parameters.  Controls if and how an item flies off on impact.
/// </summary>
public sealed class DetachProfile
{
    // ── Slot identity ─────────────────────────────────────────────────────────

    /// <summary>The slot this profile applies to.</summary>
    public EquipSlot Slot { get; set; } = EquipSlot.Other;

    // ── Detach condition ──────────────────────────────────────────────────────

    /// <summary>
    /// Normalised probability [0–1] that a qualifying impact produces a Loosened state.
    /// Multiplied by ComedyMode factor (×4) when that setting is on.
    /// </summary>
    public float DetachChance { get; set; } = 0.10f;

    /// <summary>
    /// Minimum normalised impact force [0–1] for a detach check to occur.
    /// Slots with higher thresholds only fly off on very strong hits.
    /// </summary>
    public float ImpactThreshold { get; set; } = 0.45f;

    /// <summary>Item may detach during a full ragdoll event.</summary>
    public bool TriggersOnRagdoll { get; set; } = true;

    /// <summary>
    /// Item may detach on a direct hit to the relevant body region
    /// (e.g. head hit for hat, leg hit for shoe) even without full ragdoll.
    /// </summary>
    public bool TriggersOnDirectHit { get; set; } = false;

    /// <summary>
    /// Bias direction for the launch impulse on detach (normalised).
    /// Zero = inherit the impact direction.
    /// </summary>
    public Vector2 LaunchBias { get; set; } = Vector2.Zero;

    /// <summary>
    /// Multiplier applied to the owner's velocity when computing the detach launch impulse.
    /// Higher values = item inherits more motion from the source bone/body part.
    /// </summary>
    public float LaunchImpulseMult { get; set; } = 1.0f;

    /// <summary>Auto re-equip the item when the owner walks near it.</summary>
    public bool AutoReequipAllowed { get; set; } = true;

    // ── Loosen-first parameters ───────────────────────────────────────────────

    /// <summary>
    /// Probability [0–1] that a Loosened item fully detaches on the next qualifying
    /// impact check.  A Loosened item returns to Secure if not struck again within
    /// <see cref="LoosenResetTicks"/> ticks.
    /// </summary>
    public float LoosenDetachChance { get; set; } = 0.60f;

    /// <summary>
    /// Ticks after loosening without a follow-up impact before the item re-secures
    /// itself.  Default 120 (~2 seconds).
    /// </summary>
    public int LoosenResetTicks { get; set; } = 120;

    // ── Built-in slot defaults ────────────────────────────────────────────────

    /// <summary>
    /// Returns the built-in default <see cref="DetachProfile"/> for a given slot.
    /// </summary>
    public static DetachProfile DefaultFor(EquipSlot slot) => slot switch
    {
        EquipSlot.Hat       => new() { Slot = EquipSlot.Hat,       DetachChance = 0.35f, ImpactThreshold = 0.40f, TriggersOnDirectHit = true,  LaunchImpulseMult = 1.3f },
        EquipSlot.Shoes     => new() { Slot = EquipSlot.Shoes,     DetachChance = 0.20f, ImpactThreshold = 0.50f, TriggersOnDirectHit = true,  LaunchImpulseMult = 1.1f },
        EquipSlot.Shirt     => new() { Slot = EquipSlot.Shirt,     DetachChance = 0.05f, ImpactThreshold = 0.70f, TriggersOnDirectHit = false, LaunchImpulseMult = 0.8f, AutoReequipAllowed = false },
        EquipSlot.Pants     => new() { Slot = EquipSlot.Pants,     DetachChance = 0.02f, ImpactThreshold = 0.80f, TriggersOnDirectHit = false, LaunchImpulseMult = 0.7f, AutoReequipAllowed = false },
        EquipSlot.HeldItem  => new() { Slot = EquipSlot.HeldItem,  DetachChance = 0.40f, ImpactThreshold = 0.45f, TriggersOnDirectHit = true,  LaunchImpulseMult = 1.5f },
        EquipSlot.Back      => new() { Slot = EquipSlot.Back,      DetachChance = 0.12f, ImpactThreshold = 0.55f, TriggersOnDirectHit = false, LaunchImpulseMult = 1.0f },
        EquipSlot.Accessory => new() { Slot = EquipSlot.Accessory, DetachChance = 0.25f, ImpactThreshold = 0.40f, TriggersOnDirectHit = true,  LaunchImpulseMult = 1.2f },
        _                   => new() { Slot = EquipSlot.Other,     DetachChance = 0.10f, ImpactThreshold = 0.60f },
    };
}

// ── InventoryDropProfile ──────────────────────────────────────────────────────

/// <summary>
/// Governs whether and how loose inventory items can spill from a character's
/// backpack during a severe knockdown or explosion.
/// </summary>
public sealed class InventoryDropProfile
{
    /// <summary>Item category this profile applies to.</summary>
    public ItemDropCategory Category { get; set; } = ItemDropCategory.Loose;

    /// <summary>The drop rule that determines when this item can spill.</summary>
    public ItemDropRule DropRule { get; set; } = ItemDropRule.OnHeavyImpact;

    /// <summary>
    /// Probability [0–1] that an item of this category spills per qualifying event.
    /// Capped by <see cref="ModConfig.MaxInventorySpillPerEvent"/> across all items.
    /// </summary>
    public float SpillChance { get; set; } = 0.25f;

    /// <summary>
    /// <see cref="ItemPhysicsMaterial"/> to use when spawning this item as a physics
    /// world object after it has been dropped.
    /// </summary>
    public ItemPhysicsMaterial WorldMaterial { get; set; } = ItemPhysicsMaterial.Wood;

    // ── Built-in category defaults ────────────────────────────────────────────

    /// <summary>Returns the built-in default profile for an item category.</summary>
    public static InventoryDropProfile DefaultFor(ItemDropCategory cat) => cat switch
    {
        ItemDropCategory.Safe     => new() { Category = ItemDropCategory.Safe,     DropRule = ItemDropRule.NeverDrop,    SpillChance = 0f },
        ItemDropCategory.Equipped => new() { Category = ItemDropCategory.Equipped, DropRule = ItemDropRule.OnDirectHit,  SpillChance = 0.40f, WorldMaterial = ItemPhysicsMaterial.Metal },
        _                         => new() { Category = ItemDropCategory.Loose,    DropRule = ItemDropRule.LooseSpill,   SpillChance = 0.25f, WorldMaterial = ItemPhysicsMaterial.Wood },
    };
}

// ── DroppedBelonging ──────────────────────────────────────────────────────────

/// <summary>
/// Tracks a single item that has been knocked off or spilled from a character
/// and now lives as a physics object in the world.
/// Managed exclusively by <see cref="BelongingsManager"/>.
/// </summary>
public sealed class DroppedBelonging
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>SMAPI / Stardew item unique string ID or display name (for re-equip matching).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Human-readable display name (used for log messages).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The character key (<see cref="ModEntry.GetCharacterKey"/>) of the original owner.</summary>
    public int OwnerKey { get; set; } = -1;

    /// <summary>Equipment slot the item came from (null for loose inventory items).</summary>
    public EquipSlot? SourceSlot { get; set; }

    /// <summary>Whether this item is eligible to auto-re-equip when retrieved.</summary>
    public bool AutoReequipEligible { get; set; } = true;

    // ── World physics ─────────────────────────────────────────────────────────

    /// <summary>World position (in world-pixels) where the item landed.</summary>
    public Vector2 WorldPosition { get; set; }

    /// <summary>
    /// The <see cref="ItemPhysicsState"/> object that is actively simulating
    /// this item in <see cref="ItemPhysicsWorld"/>.
    /// Null when the item has been picked up or has despawned.
    /// </summary>
    public ItemPhysicsState? PhysicsState { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Game ticks elapsed since this belonging was dropped.</summary>
    public int AgeTicks { get; set; }

    /// <summary>Maximum ticks before despawn.  0 = use <see cref="ModConfig.DroppedBelongingsDespawnTicks"/>.</summary>
    public int MaxAgeTicks { get; set; }

    /// <summary>Ticks remaining during which only the original owner can pick this up.</summary>
    public int OwnerGraceTicks { get; set; } = 120; // ~2 seconds

    /// <summary>Whether this belonging has been picked up or despawned.</summary>
    public bool IsAlive { get; set; } = true;
}

// ── BelongingsManager ─────────────────────────────────────────────────────────

/// <summary>
/// Central manager for all items that have been knocked off or spilled from
/// characters.  Handles:
/// <list type="bullet">
///   <item>Registering newly dropped belongings.</item>
///   <item>Per-tick despawn countdown and age tracking.</item>
///   <item>Owner auto-pickup within <see cref="ModConfig.AutoPickupRadius"/> tiles.</item>
///   <item>Clearing state on day-start or save-load.</item>
/// </list>
///
/// Does NOT directly manipulate the Stardew inventory (that requires Harmony
/// patches against <c>Farmer</c> or event callbacks) — callers are responsible
/// for actually removing / re-adding items from inventory before and after
/// registering / retrieving a <see cref="DroppedBelonging"/>.
///
/// Not thread-safe — update from the main game thread only.
/// </summary>
public sealed class BelongingsManager
{
    private readonly List<DroppedBelonging> _active = new();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Count of currently live dropped belongings.</summary>
    public int Count => _active.Count;

    /// <summary>Read-only view of all live dropped belongings.</summary>
    public IReadOnlyList<DroppedBelonging> All => _active;

    /// <summary>
    /// Register a newly dropped item.
    /// The caller must have already removed the item from the character's
    /// inventory/equipment before calling this method.
    /// </summary>
    public void Register(DroppedBelonging belonging)
    {
        if (belonging is null) throw new ArgumentNullException(nameof(belonging));
        _active.Add(belonging);
    }

    /// <summary>
    /// Advance all live belongings by one tick.
    /// <paramref name="cfg"/> provides despawn timing and auto-pickup parameters.
    /// <paramref name="playerPos"/> is used for auto-pickup radius checks.
    /// <paramref name="playerKey"/> is the character key of the local farmer.
    /// <paramref name="onAutoPickup"/> is invoked for each belonging the player walks over
    /// so the caller can restore the item to the inventory/equipment.
    /// </summary>
    public void Tick(
        ModConfig        cfg,
        Vector2          playerPos,
        int              playerKey,
        Action<DroppedBelonging> onAutoPickup)
    {
        if (_active.Count == 0) return;

        var despawnMax = cfg.DroppedBelongingsDespawnTicks > 0
            ? cfg.DroppedBelongingsDespawnTicks
            : int.MaxValue;

        var pickupRadiusSq = (cfg.AutoPickupRadius * 64f) * (cfg.AutoPickupRadius * 64f);

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var b = _active[i];
            if (!b.IsAlive)
            {
                _active.RemoveAt(i);
                continue;
            }

            b.AgeTicks++;
            if (b.OwnerGraceTicks > 0) b.OwnerGraceTicks--;

            // Update world position from physics state if still simulating
            if (b.PhysicsState is not null && b.PhysicsState.IsAlive)
            {
                b.WorldPosition = b.PhysicsState.Position;
            }

            // Despawn check
            var maxAge = b.MaxAgeTicks > 0 ? b.MaxAgeTicks : despawnMax;
            if (b.AgeTicks >= maxAge)
            {
                b.IsAlive = false;
                _active.RemoveAt(i);
                continue;
            }

            // Auto-pickup: only the owner or anyone (after grace ticks expire)
            if (cfg.AutoPickupRadius > 0f)
            {
                var distSq = Vector2.DistanceSquared(playerPos, b.WorldPosition);
                var canPickup = distSq <= pickupRadiusSq
                    && (b.OwnerKey == playerKey || b.OwnerGraceTicks <= 0);

                if (canPickup)
                {
                    b.IsAlive = false;
                    onAutoPickup(b);
                    _active.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Manually mark a belonging as picked up (e.g. via interact button).
    /// </summary>
    public void MarkPickedUp(DroppedBelonging belonging)
    {
        belonging.IsAlive = false;
        _active.Remove(belonging);
    }

    /// <summary>
    /// Evaluate the normalised detach chance for a given slot, accounting for
    /// the global severity threshold, per-slot override, comedy mode, and the
    /// loosen-first state machine.
    /// Returns <c>true</c> if the item should become Loosened (or fully Detached
    /// if already Loosened), <c>false</c> otherwise.
    /// <paramref name="currentState"/> is updated in-place.
    /// </summary>
    public static bool EvaluateDetach(
        DetachProfile   profile,
        ItemEquipState  currentState,
        float           impactForce,     // normalised [0–1]
        ModConfig       cfg,
        ref ItemEquipState newState)
    {
        // Bail if impact is below slot threshold or global threshold
        var effectiveThreshold = Math.Max(profile.ImpactThreshold, cfg.GearKnockOffSeverityThreshold);
        if (impactForce < effectiveThreshold)
        {
            newState = currentState;
            return false;
        }

        var chanceMultiplier = cfg.ComedyMode ? 4f : 1f;

        if (currentState == ItemEquipState.Secure)
        {
            // Roll for loosening
            var roll = Random.Shared.NextSingle();
            if (roll < profile.DetachChance * chanceMultiplier)
            {
                newState = ItemEquipState.Loosened;
                return true; // loosened — caller should show cosmetic tilt
            }

            newState = ItemEquipState.Secure;
            return false;
        }

        if (currentState == ItemEquipState.Loosened)
        {
            // Already loose — roll for full detach
            var roll = Random.Shared.NextSingle();
            if (roll < profile.LoosenDetachChance * chanceMultiplier)
            {
                newState = ItemEquipState.Detached;
                return true; // fully detached — spawn DroppedBelonging
            }

            newState = ItemEquipState.Loosened;
            return false;
        }

        // Already detached — nothing to do
        newState = currentState;
        return false;
    }

    /// <summary>
    /// Classify an item by its display name into a <see cref="ItemDropCategory"/>,
    /// taking the protected-item names list from config into account.
    /// </summary>
    public static ItemDropCategory ClassifyItem(string displayName, ModConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return ItemDropCategory.Safe;

        // Check user-defined protected items
        if (!string.IsNullOrWhiteSpace(cfg.ProtectedItemNames))
        {
            foreach (var part in cfg.ProtectedItemNames.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0
                    && displayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return ItemDropCategory.Safe;
                }
            }
        }

        // Always-safe item name patterns (vanilla quest / key items)
        if (IsAlwaysSafe(displayName)) return ItemDropCategory.Safe;

        // Consumables / crafting mats → Loose
        if (IsLooseItem(displayName)) return ItemDropCategory.Loose;

        // Default to Loose for anything else
        return ItemDropCategory.Loose;
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Remove all tracked belongings.
    /// Called on day-start and save-load to avoid stale state across transitions.
    /// </summary>
    public void Clear() => _active.Clear();

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsAlwaysSafe(string name)
    {
        // These name patterns cover vanilla quest/key items that should NEVER drop.
        ReadOnlySpan<string> safePatterns = new[]
        {
            "Prismatic Shard",
            "Dwarvish Translation Guide",
            "Mayor's Shorts",
            "Club Card",
            "Skull Key",
            "Rusty Key",
            "Library Key",
            "Mermaid Pendant",
            "Rabbit's Foot",
            "Golden Pumpkin",
            "Void Ghost Pendant",
            "Pearl",
        };
        foreach (var p in safePatterns)
        {
            if (name.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsLooseItem(string name)
    {
        // Consumable / crafting material / ammo type keywords → Loose
        ReadOnlySpan<string> loosePatterns = new[]
        {
            "Juice",
            "Wine",
            "Pickle",
            "Jelly",
            "Honey",
            "Ale",
            "Coffee",
            "Tea",
            "Potion",
            "Bomb",
            "Cherry",
            "Berry",
            "Apple",
            "Salad",
            "Soup",
            "Bread",
            "Cake",
            "Cookie",
            "Stew",
            "Sashimi",
            "Pizza",
            "Rice",
            "Tortilla",
            "Egg",
            "Cheese",
            "Truffle",
            "Milk",
            "Fiber",
            "Stone",
            "Wood",
            "Coal",
            "Geode",
            "Ore",
        };
        foreach (var p in loosePatterns)
        {
            if (name.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
