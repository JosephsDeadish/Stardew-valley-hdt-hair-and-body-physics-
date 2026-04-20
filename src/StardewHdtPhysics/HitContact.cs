using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── HitContact system ─────────────────────────────────────────────────────────
//
// Separates game-collision from impact *feedback* following the pipeline:
//
//   A. Detect contact     → build HitContact
//   B. Resolve gameplay   → damage / stagger / break (handled by ModEntry / game)
//   C. Resolve feedback   → HitContactResolver.Apply(hitContact) returns
//                           HitstopProfile which drives hitstop, recoil, VFX,
//                           bone impulse and flash
//
// Key design goals:
//  • HitMaterial, not just object type — stone vs wood vs flesh all feel different
//  • Per-target-kind tuning — wall / breakable / enemy / NPC / armored / slime
//  • BoneImpulseRouter keeps collision decisions OUT of bone code
//  • Hitstop is output, not hardcoded constant — attacker recoil = first-class value
// ─────────────────────────────────────────────────────────────────────────────

// ── Material ──────────────────────────────────────────────────────────────────

/// <summary>
/// Physical material of the contact surface.
/// Drives stop length, recoil, sound and VFX preset.
/// </summary>
public enum HitMaterial
{
    Unknown = 0,
    Stone,
    Metal,
    Wood,
    Cloth,
    Glass,
    Gem,
    Ore,
    /// <summary>Organic soft tissue — humanoids, animals.</summary>
    Flesh,
    /// <summary>Slime/goop — soft, slightly sticky, very little attacker recoil.</summary>
    Slime,
    /// <summary>Armour plate on top of a flesh target.</summary>
    Armor,
    /// <summary>Blocking shield (player or NPC).</summary>
    Shield,
    /// <summary>Foliage — leaves, grass, thin cloth banners, almost no stop.</summary>
    Foliage,
}

// ── Hit zone (body region) ────────────────────────────────────────────────────

/// <summary>
/// Body region that was struck.  Used by <see cref="BoneImpulseRouter"/> to
/// dispatch impulses to the correct bones with correct directions and falloff.
/// </summary>
public enum HitZone
{
    None = 0,
    // --- Shared ---
    Head,
    Torso,
    Belly,
    Groin,
    HipL,
    HipR,
    ThighL,
    ThighR,
    // --- Feminine-specific ---
    BreastL,
    BreastR,
    ButtL,
    ButtR,
    // --- Appendages ---
    ArmL,
    ArmR,
    Tail,
    WingL,
    WingR,
    EarL,
    EarR,
    Snout,
    // --- Non-body ---
    Shield,
    Armor,
    WeaponTip,
}

// ── Target kind ───────────────────────────────────────────────────────────────

/// <summary>
/// High-level classification of the thing that was hit.
/// Used to pick the base <see cref="HitstopProfile"/> before material layering.
/// </summary>
public enum HitTargetKind
{
    Unknown = 0,
    /// <summary>Immovable terrain — wall, stone, cliff.</summary>
    Wall,
    /// <summary>World object that breaks when hit hard enough — crate, bottle, ore node.</summary>
    BreakableItem,
    /// <summary>Loose item on the ground — coin, tool, log.</summary>
    LooseItem,
    /// <summary>Hostile monster.</summary>
    Enemy,
    /// <summary>Friendly or neutral NPC.</summary>
    Npc,
    /// <summary>Farm or wild animal.</summary>
    Animal,
    /// <summary>Enemy with armour plating.</summary>
    ArmoredEnemy,
    /// <summary>Large soft-body creature (slime colony, goopy enemies).</summary>
    SoftBodyCreature,
}

// ── Hit contact record ────────────────────────────────────────────────────────

/// <summary>
/// Immutable description of a single contact event.
/// Built by ModEntry contact-detection code and consumed by
/// <see cref="HitContactResolver.Resolve"/>.
/// </summary>
public readonly record struct HitContact(
    HitTargetKind TargetKind,
    HitMaterial   Material,
    HitZone       Zone,
    /// <summary>Normalized direction FROM attacker TO target (world-space).</summary>
    Vector2        HitNormal,
    /// <summary>0 = lightest tap, 1 = full-strength attack.</summary>
    float          Strength,
    bool           WasBreak,
    bool           WasBlock,
    bool           WasKill);

// ── Per-material physical properties ─────────────────────────────────────────

/// <summary>
/// Multipliers applied on top of the base <see cref="HitstopProfile"/> selected by
/// <see cref="HitTargetKind"/>.  Material layering lets "armored enemy" still feel
/// metallic while enemy flesh feels soft.
/// </summary>
public readonly struct HitMaterialProps
{
    /// <summary>Multiplies base freeze-tick count.  Stone=1.3, Flesh=0.9, Slime=0.7.</summary>
    public readonly float FreezeMult;
    /// <summary>Multiplies attacker recoil magnitude.</summary>
    public readonly float AttackerRecoilMult;
    /// <summary>Multiplies target flinch impulse magnitude.</summary>
    public readonly float TargetRecoilMult;
    /// <summary>Multiplies flash alpha.</summary>
    public readonly float FlashMult;
    /// <summary>The typed particle kind for impact debris.  Null = no material-specific particle override.</summary>
    public readonly PhysicsParticleKind? DebrisKind;
    /// <summary>Particle count override (0 = use profile default).</summary>
    public readonly int DebrisCountOverride;

    public HitMaterialProps(
        float freezeMult, float attackerRecoilMult, float targetRecoilMult,
        float flashMult, PhysicsParticleKind? debrisKind = null, int debrisCountOverride = 0)
    {
        FreezeMult           = freezeMult;
        AttackerRecoilMult   = attackerRecoilMult;
        TargetRecoilMult     = targetRecoilMult;
        FlashMult            = flashMult;
        DebrisKind           = debrisKind;
        DebrisCountOverride  = debrisCountOverride;
    }

    /// <summary>Returns per-material multipliers.</summary>
    public static HitMaterialProps For(HitMaterial mat) => mat switch
    {
        //                                    frz  aRecoil tRecoil flash  debris              count
        HitMaterial.Stone   => new(1.40f, 1.30f,  0.40f, 0.35f, PhysicsParticleKind.StoneChunk,   4),
        HitMaterial.Metal   => new(1.30f, 1.20f,  0.35f, 0.40f, PhysicsParticleKind.StoneChunk,   2),
        HitMaterial.Wood    => new(1.00f, 0.90f,  0.50f, 0.25f, PhysicsParticleKind.WoodSplinter, 3),
        HitMaterial.Cloth   => new(0.40f, 0.30f,  0.70f, 0.10f, null,                             0),
        HitMaterial.Glass   => new(0.70f, 0.50f,  0.80f, 0.30f, PhysicsParticleKind.GemChunk,     5),
        HitMaterial.Gem     => new(1.10f, 0.80f,  0.60f, 0.45f, PhysicsParticleKind.GemChunk,     3),
        HitMaterial.Ore     => new(1.20f, 1.10f,  0.40f, 0.30f, PhysicsParticleKind.OreChunk,     3),
        HitMaterial.Flesh   => new(0.90f, 0.50f,  1.00f, 0.25f, null,                             0),
        HitMaterial.Slime   => new(0.70f, 0.30f,  1.20f, 0.15f, null,                             0),
        HitMaterial.Armor   => new(1.50f, 1.40f,  0.20f, 0.50f, PhysicsParticleKind.StoneChunk,   2),
        HitMaterial.Shield  => new(1.30f, 1.20f,  0.10f, 0.35f, null,                             0),
        HitMaterial.Foliage => new(0.15f, 0.10f,  0.80f, 0.05f, null,                             0),
        _                   => new(1.00f, 1.00f,  1.00f, 0.20f, null,                             0),
    };
}

// ── Hitstop profile ───────────────────────────────────────────────────────────

/// <summary>
/// Resolved feedback parameters for a single contact event.
/// All values are 0-based; 0 = suppress that feedback channel entirely.
/// Consumed by <c>ModEntry</c> to drive hitstop, flash, recoil and VFX.
/// </summary>
public sealed class HitstopProfile
{
    /// <summary>Ticks to freeze physics (0 = no freeze).</summary>
    public int FreezeTicks;

    /// <summary>Screen flash alpha [0, 1].</summary>
    public float FlashAlpha;

    /// <summary>
    /// Impulse to apply to the attacker (backward — opposite to <c>HitNormal</c>).
    /// Magnitude encodes strength; direction is pre-computed.
    /// </summary>
    public Vector2 AttackerRecoil;

    /// <summary>
    /// Impulse to apply to the target (in the direction of <c>HitNormal</c>).
    /// </summary>
    public Vector2 TargetRecoil;

    /// <summary>
    /// Typed particle kind to spawn at the impact point.  Null = no typed debris.
    /// </summary>
    public PhysicsParticleKind? DebrisKind;

    /// <summary>Number of typed debris particles to spawn.</summary>
    public int DebrisCount;

    /// <summary>Speed multiplier for spawned debris particles.</summary>
    public float DebrisSpeed;

    /// <summary>Body bone impulse for the target (maps to BoneImpulseRouter).</summary>
    public Vector2 TargetBoneImpulse;

    /// <summary>Hair chain impulse for the target.</summary>
    public Vector2 TargetHairImpulse;

    /// <summary>
    /// Whether the physical simulation should continue this tick
    /// (false = hitstop in effect — skip physics update for target).
    /// </summary>
    public bool AllowContinue;

    /// <summary>A readable name for this profile (debug / logging only).</summary>
    public string Label = string.Empty;
}

// ── Resolver ──────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless resolver: maps a <see cref="HitContact"/> into a fully-populated
/// <see cref="HitstopProfile"/> by:
/// <list type="number">
///   <item>Selecting the base profile for the <see cref="HitTargetKind"/></item>
///   <item>Scaling by <see cref="HitContact.Strength"/></item>
///   <item>Layering <see cref="HitMaterialProps"/> multipliers</item>
///   <item>Clamping outputs to safe ranges</item>
/// </list>
/// Does NOT write to any game state — callers are responsible for applying the
/// returned profile.
/// </summary>
public static class HitContactResolver
{
    // ── Base profiles per target kind (at strength = 1.0) ────────────────────
    // (freezeTicks, flashAlpha, attackerRecoilMag, targetRecoilMag, debrisCount, debrisSpeed)
    private static (int freeze, float flash, float aRecoil, float tRecoil, int debris, float debrisSpd)
        BaseFor(HitTargetKind kind) => kind switch
    {
        HitTargetKind.Wall             => (6,  0.25f, 0.45f, 0.00f, 5, 1.8f),
        HitTargetKind.BreakableItem    => (4,  0.20f, 0.30f, 0.50f, 6, 1.5f),
        HitTargetKind.LooseItem        => (1,  0.05f, 0.10f, 0.60f, 0, 0.0f),
        HitTargetKind.Enemy            => (4,  0.25f, 0.15f, 0.55f, 0, 0.0f),
        HitTargetKind.Npc              => (2,  0.10f, 0.08f, 0.40f, 0, 0.0f),
        HitTargetKind.Animal           => (3,  0.15f, 0.10f, 0.45f, 0, 0.0f),
        HitTargetKind.ArmoredEnemy     => (7,  0.40f, 0.45f, 0.15f, 2, 1.2f),
        HitTargetKind.SoftBodyCreature => (3,  0.10f, 0.05f, 0.80f, 0, 0.0f),
        _                              => (3,  0.15f, 0.20f, 0.40f, 2, 1.2f),
    };

    /// <summary>
    /// Resolve a full <see cref="HitstopProfile"/> from a <see cref="HitContact"/>.
    /// </summary>
    public static HitstopProfile Resolve(in HitContact c)
    {
        var s    = Math.Clamp(c.Strength, 0f, 1.5f);
        var base_ = BaseFor(c.TargetKind);
        var mat  = HitMaterialProps.For(c.Material);

        // Kill / break bonuses — breaking something feels weighty
        float breakBonus = c.WasBreak ? 1.20f : 1.0f;
        // Block reduces feedback (weapon bounced off a shield)
        float blockMult  = c.WasBlock ? 0.60f : 1.0f;

        // ── Freeze ticks ──────────────────────────────────────────────────────
        var freezeTicks = (int)MathF.Round(base_.freeze * s * mat.FreezeMult * breakBonus * blockMult);
        freezeTicks = Math.Clamp(freezeTicks, 0, 8);

        // ── Flash ─────────────────────────────────────────────────────────────
        var flash = base_.flash * s * mat.FlashMult;
        flash = Math.Clamp(flash, 0f, 0.65f);

        // ── Recoil directions ─────────────────────────────────────────────────
        // Attacker recoil: backward (opposite to hit normal), slightly upward
        var attackerDir = -c.HitNormal + new Vector2(0f, -0.15f);
        if (attackerDir.LengthSquared() > 0f)
            attackerDir = Vector2.Normalize(attackerDir);

        var attackerRecoilMag = base_.aRecoil * s * mat.AttackerRecoilMult * blockMult;
        attackerRecoilMag = Math.Clamp(attackerRecoilMag, 0f, 0.55f);

        // Target recoil: in the direction of the hit normal
        var targetDir = c.HitNormal;
        if (targetDir.LengthSquared() < 0.001f)
            targetDir = new Vector2(0f, -1f);

        var targetRecoilMag = base_.tRecoil * s * mat.TargetRecoilMult * breakBonus;
        targetRecoilMag = Math.Clamp(targetRecoilMag, 0f, 0.70f);

        // ── Bone / hair impulse to target ─────────────────────────────────────
        // Body bone: in hit direction, scaled by target recoil
        var boneImpulse = targetDir * targetRecoilMag * 0.80f;
        // Hair: same direction but 1.2× (hair lags and whips further)
        var hairImpulse = targetDir * targetRecoilMag * 1.20f;

        // ── Debris ────────────────────────────────────────────────────────────
        var debrisKind  = mat.DebrisKind;
        var debrisCount = mat.DebrisCountOverride > 0 ? mat.DebrisCountOverride : base_.debris;
        // Scale debris count by strength (weak hit = fewer chips)
        debrisCount = (int)MathF.Round(debrisCount * Math.Clamp(s, 0.5f, 1.5f));

        return new HitstopProfile
        {
            FreezeTicks      = freezeTicks,
            FlashAlpha       = flash,
            AttackerRecoil   = attackerDir * attackerRecoilMag,
            TargetRecoil     = targetDir   * targetRecoilMag,
            DebrisKind       = debrisKind,
            DebrisCount      = debrisCount,
            DebrisSpeed      = base_.debrisSpd,
            TargetBoneImpulse = boneImpulse,
            TargetHairImpulse = hairImpulse,
            AllowContinue    = freezeTicks == 0,
            Label            = $"{c.TargetKind}/{c.Material}/{c.Zone} s={s:F2}",
        };
    }

    // ── Convenience factory helpers ───────────────────────────────────────────

    /// <summary>Build a HitContact for a player hitting a hard surface (stone/metal wall).</summary>
    public static HitContact ForWall(HitMaterial mat, Vector2 hitNormal, float strength)
        => new(HitTargetKind.Wall, mat, HitZone.None, hitNormal, strength, false, false, false);

    /// <summary>Build a HitContact for a player hitting wood.</summary>
    public static HitContact ForWood(Vector2 hitNormal, float strength, bool breaks)
        => new(HitTargetKind.BreakableItem, HitMaterial.Wood, HitZone.None, hitNormal, strength, breaks, false, false);

    /// <summary>Build a HitContact for hitting an enemy body zone.</summary>
    public static HitContact ForEnemy(HitMaterial mat, HitZone zone, Vector2 hitNormal, float strength, bool kills)
        => new(HitTargetKind.Enemy, mat, zone, hitNormal, strength, false, false, kills);

    /// <summary>Build a HitContact for hitting an armored enemy.</summary>
    public static HitContact ForArmoredEnemy(Vector2 hitNormal, float strength)
        => new(HitTargetKind.ArmoredEnemy, HitMaterial.Armor, HitZone.Armor, hitNormal, strength, false, false, false);

    /// <summary>Build a HitContact for hitting an NPC (friendly/neutral).</summary>
    public static HitContact ForNpc(HitZone zone, Vector2 hitNormal, float strength)
        => new(HitTargetKind.Npc, HitMaterial.Flesh, zone, hitNormal, strength, false, false, false);

    /// <summary>Build a HitContact for hitting a slime-type soft-body creature.</summary>
    public static HitContact ForSlime(Vector2 hitNormal, float strength)
        => new(HitTargetKind.SoftBodyCreature, HitMaterial.Slime, HitZone.Torso, hitNormal, strength, false, false, false);

    /// <summary>Build a HitContact when the player takes a hit (attacker direction reversed).</summary>
    public static HitContact ForPlayerHurt(Vector2 attackerDir, float damageFraction)
        => new(HitTargetKind.Enemy, HitMaterial.Flesh, HitZone.Torso,
               Vector2.Normalize(attackerDir), Math.Clamp(damageFraction, 0f, 1.5f), false, false, false);
}
