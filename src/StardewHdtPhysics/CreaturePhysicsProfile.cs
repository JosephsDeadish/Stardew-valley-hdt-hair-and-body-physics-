using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── CreaturePhysicsProfile ────────────────────────────────────────────────────
//
// Per-species physics tuning that drives SimulateMonsterBody and
// BoneImpulseRouter.  All magic constants that used to be buried inside the
// SimulateMonsterBody switch-statement now live here, one profile per species.
//
// Design goals:
//  • Single source of truth for each species' feel — no per-case magic numbers
//  • Mod-extensible via creatureProfiles.json so any content pack (Buggy Bugs,
//    monster packs, farm animal mods, …) can register new species or override
//    built-in ones without code changes
//  • BoneImpulseRouter receives species-specific follow-through multipliers so
//    the right appendages react to body hits (dragon tail/wings, wolf fur, etc.)
//  • Public registration API lets SMAPI mods call RegisterByName() at GameLaunched
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Full physics tuning profile for one creature species/archetype.
///
/// Every float field is either an absolute value (e.g. DecayRate) or a
/// per-step multiplier.  JSON-serialisable — matches creatureProfiles.json schema.
/// </summary>
public sealed class CreaturePhysicsProfile
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>The archetype this profile represents (used as the dictionary key).</summary>
    public MonsterPhysicsArchetype Archetype { get; set; } = MonsterPhysicsArchetype.Generic;

    /// <summary>Human-readable name for logging/debug.</summary>
    public string Label { get; set; } = string.Empty;

    // ── Core simulation parameters ────────────────────────────────────────────

    /// <summary>
    /// Fraction of accumulated impulse retained each tick.
    /// 0.72 = snappy skeleton clatter;  0.95 = slow-lingering dragon motion.
    /// </summary>
    public float DecayRate { get; set; } = 0.86f;

    /// <summary>Fraction of the creature's X velocity fed back as impulse each tick.</summary>
    public float VelocityImpulseX { get; set; } = 0.04f;

    /// <summary>Fraction of the creature's Y velocity fed back as impulse each tick.</summary>
    public float VelocityImpulseY { get; set; } = 0.04f;

    // ── Per-tick random micro-noise ────────────────────────────────────────────

    /// <summary>Half-amplitude of per-tick random noise on X (0 = none).</summary>
    public float RandomNoiseX { get; set; } = 0f;

    /// <summary>Half-amplitude of per-tick random noise on Y (0 = none).</summary>
    public float RandomNoiseY { get; set; } = 0f;

    // ── Periodic burst impulse (e.g. dragon wingbeat, bird flap) ─────────────

    /// <summary>Ticks between periodic impulse bursts.  0 = disabled.</summary>
    public int PeriodicPeriod { get; set; } = 0;

    /// <summary>Random X half-amplitude of the periodic burst impulse.</summary>
    public float PeriodicImpulseX { get; set; } = 0f;

    /// <summary>Y impulse applied during periodic burst (positive = upward in screen-space).</summary>
    public float PeriodicImpulseY { get; set; } = 0f;

    // ── Sinusoidal impulse (elemental fluctuation, worm oscillation) ──────────

    /// <summary>Angular frequency (radians/tick) of the sinusoidal X component.  0 = off.</summary>
    public float SinusoidalXFreq { get; set; } = 0f;

    /// <summary>Amplitude of the sinusoidal X component.</summary>
    public float SinusoidalXAmp  { get; set; } = 0f;

    /// <summary>Angular frequency (radians/tick) of the sinusoidal Y component.  0 = off.</summary>
    public float SinusoidalYFreq { get; set; } = 0f;

    /// <summary>Amplitude of the sinusoidal Y component.</summary>
    public float SinusoidalYAmp  { get; set; } = 0f;

    // ── High-speed extra noise (ground rumble, charge stomp) ─────────────────

    /// <summary>
    /// Squared velocity threshold above which extra random noise is added.
    /// 0 = disabled.  Dragon uses 2f for ground-rumble when sprinting.
    /// </summary>
    public float VelocityThresholdSq { get; set; } = 0f;

    /// <summary>Half-amplitude of the extra noise added when above the velocity threshold (X).</summary>
    public float ThresholdNoiseX { get; set; } = 0f;

    /// <summary>Half-amplitude of the extra noise added when above the velocity threshold (Y).</summary>
    public float ThresholdNoiseY { get; set; } = 0f;

    // ── Chain presence flags ──────────────────────────────────────────────────

    /// <summary>True if this species uses a tail chain (wolf, dragon, cat, etc.).</summary>
    public bool HasTailChain { get; set; } = false;

    /// <summary>True if this species uses wing chains (dragon, bat, bird, familiar).</summary>
    public bool HasWings { get; set; } = false;

    /// <summary>True if this species uses a fur-surface ripple chain (wolf, cow, fluffy animals).</summary>
    public bool HasFurChain { get; set; } = false;

    /// <summary>True if this species uses ear/snout animal bones (farm animals, pets).</summary>
    public bool HasEarBones { get; set; } = false;

    // ── Chain segment counts ──────────────────────────────────────────────────

    /// <summary>Number of segments for the tail chain (2–6).</summary>
    public int TailSegments { get; set; } = 4;

    /// <summary>Number of segments for the fur chain (2–6).</summary>
    public int FurSegments { get; set; } = 3;

    // ── Hit reaction multipliers (used by BoneImpulseRouter.RouteWithProfile) ─

    /// <summary>Scales the bone-impulse delivered to this species' body bones on hit.</summary>
    public float HitBoneImpulseMult { get; set; } = 1.0f;

    /// <summary>Tail follow-through fraction on a non-tail body hit (0.04 = default).</summary>
    public float HitTailFollowMult { get; set; } = 0.04f;

    /// <summary>Wing follow-through fraction on a non-wing body hit (0.03 = default).</summary>
    public float HitWingFollowMult { get; set; } = 0.03f;

    /// <summary>Fur ripple fraction on body hit (0.03 = default).</summary>
    public float HitFurFollowMult { get; set; } = 0.03f;

    /// <summary>Hair chain follow-through fraction on head/torso hit (0.06 = default).</summary>
    public float HitHairFollowMult { get; set; } = 0.06f;

    // ── Idle micro-motion ─────────────────────────────────────────────────────

    /// <summary>Per-tick amplitude of the idle body micro-motion (0 = no idle).</summary>
    public float IdleBodyAmplitude { get; set; } = 0.008f;

    /// <summary>Per-tick amplitude of idle ear/snout/appendage micro-motion.</summary>
    public float IdleAppendageAmplitude { get; set; } = 0.006f;

    // ── Spring tuning ─────────────────────────────────────────────────────────

    /// <summary>Multiplies the configured stiffness for all chains on this species.</summary>
    public float StiffnessMult { get; set; } = 1.0f;

    /// <summary>Multiplies the configured damping for all chains on this species.</summary>
    public float DampingMult { get; set; } = 1.0f;
}

// ── CreatureSpeciesRule ───────────────────────────────────────────────────────

/// <summary>
/// JSON-loadable rule from <c>assets/creatureProfiles.json</c>.
/// When the creature's display name contains <see cref="NameContains"/> (case-insensitive),
/// the attached <see cref="Profile"/> is used — overriding any built-in archetype profile.
///
/// This is the primary mod-extensibility hook: add your creature names here and
/// provide a fully-tuned <see cref="CreaturePhysicsProfile"/>.
/// </summary>
public sealed class CreatureSpeciesRule
{
    /// <summary>Case-insensitive substring match against the creature's display name.</summary>
    public string NameContains { get; set; } = string.Empty;

    /// <summary>
    /// Optional archetype override (e.g. "Insect").
    /// Used if <see cref="Profile"/> is null — maps to a built-in profile.
    /// </summary>
    public string? Archetype { get; set; }

    /// <summary>
    /// Full profile override.  When populated, completely replaces any built-in
    /// profile for creatures whose name matches <see cref="NameContains"/>.
    /// </summary>
    public CreaturePhysicsProfile? Profile { get; set; }
}

// ── CreaturePhysicsProfileLibrary ─────────────────────────────────────────────

/// <summary>
/// Central registry for all creature physics profiles.
///
/// Built-in profiles cover every <see cref="MonsterPhysicsArchetype"/>.
/// Mod authors can extend or override profiles in two ways:
///
///   1. JSON: populate <c>assets/creatureProfiles.json</c> with
///      <see cref="CreatureSpeciesRule"/> entries.
///
///   2. Code: call <see cref="RegisterByName"/> from your SMAPI mod's
///      <c>GameLaunched</c> event (after all mods have loaded).
///
/// Resolution order (highest priority first):
///   1. Code-registered name overrides (most recent insertion wins)
///   2. JSON name-pattern overrides
///   3. Built-in archetype profile
///   4. Generic fallback
/// </summary>
public static class CreaturePhysicsProfileLibrary
{
    private static readonly Dictionary<MonsterPhysicsArchetype, CreaturePhysicsProfile> BuiltIn
        = BuildBuiltIns();

    // Name overrides: (pattern, profile) — inserted at index 0 so later calls win.
    private static readonly List<(string Pattern, CreaturePhysicsProfile Profile)> NameOverrides = new();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Get the built-in profile for an archetype.
    /// Falls back to <see cref="MonsterPhysicsArchetype.Generic"/> if not found.
    /// </summary>
    public static CreaturePhysicsProfile Get(MonsterPhysicsArchetype archetype)
        => BuiltIn.TryGetValue(archetype, out var p) ? p : BuiltIn[MonsterPhysicsArchetype.Generic];

    /// <summary>
    /// Find a profile by creature name, checking name overrides first.
    /// Returns <c>null</c> when no name pattern matches — caller should fall back
    /// to <see cref="Get(MonsterPhysicsArchetype)"/>.
    /// </summary>
    public static CreaturePhysicsProfile? FindByName(string creatureName)
    {
        foreach (var (pattern, profile) in NameOverrides)
        {
            if (creatureName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return null;
    }

    /// <summary>
    /// Register a custom profile by creature-name pattern.
    /// Inserted at the front of the override list so the most-recently registered
    /// pattern wins on conflict.
    /// Intended for SMAPI mods to call from <c>GameLaunched</c>.
    /// </summary>
    public static void RegisterByName(string nameContains, CreaturePhysicsProfile profile)
        => NameOverrides.Insert(0, (nameContains, profile));

    /// <summary>
    /// Bulk-register rules loaded from <c>assets/creatureProfiles.json</c>.
    /// Called from <c>ModEntry.LoadData</c> — appended after any code-registered overrides.
    /// </summary>
    internal static void RegisterFromJson(IEnumerable<CreatureSpeciesRule> rules)
    {
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.NameContains)) continue;

            CreaturePhysicsProfile? profile = rule.Profile;

            // If no full profile provided, look up built-in by archetype name
            if (profile is null && !string.IsNullOrWhiteSpace(rule.Archetype)
                && Enum.TryParse<MonsterPhysicsArchetype>(rule.Archetype, ignoreCase: true, out var arch))
            {
                profile = Get(arch);
            }

            if (profile is not null)
                NameOverrides.Add((rule.NameContains, profile));
        }
    }

    // ── Built-in profile factory ──────────────────────────────────────────────

    private static Dictionary<MonsterPhysicsArchetype, CreaturePhysicsProfile> BuildBuiltIns()
    {
        var d = new Dictionary<MonsterPhysicsArchetype, CreaturePhysicsProfile>();

        // ── Generic ────────────────────────────────────────────────────────────
        d[MonsterPhysicsArchetype.Generic] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Generic,
            Label                = "Generic",
            DecayRate            = 0.86f,
            VelocityImpulseX     = 0.04f,
            VelocityImpulseY     = 0.04f,
            HitBoneImpulseMult   = 1.0f,
            IdleBodyAmplitude    = 0.008f,
        };

        // ── Slime — bouncy jello, omnidirectional wobble ───────────────────────
        d[MonsterPhysicsArchetype.Slime] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Slime,
            Label                = "Slime",
            DecayRate            = 0.92f,
            VelocityImpulseX     = 0.06f,
            VelocityImpulseY     = 0.06f,
            RandomNoiseX         = 0.018f,
            RandomNoiseY         = 0.018f,
            HitBoneImpulseMult   = 1.3f,  // squishy: lots of body movement on hit
            HitFurFollowMult     = 0.00f, // no fur
            HitTailFollowMult    = 0.00f,
            HitWingFollowMult    = 0.00f,
            IdleBodyAmplitude    = 0.014f, // visibly wobbles at rest
        };

        // ── Bat — floppy wing flutter, fast lateral snap-back ─────────────────
        d[MonsterPhysicsArchetype.Bat] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Bat,
            Label                = "Bat",
            DecayRate            = 0.80f,
            VelocityImpulseX     = 0.08f,
            VelocityImpulseY     = 0.025f,
            RandomNoiseX         = 0.012f,
            HasWings             = true,
            HitWingFollowMult    = 0.35f, // wings react strongly to body hit
            IdleBodyAmplitude    = 0.010f,
        };

        // ── Worm — Y-axis compression/extension ────────────────────────────────
        d[MonsterPhysicsArchetype.Worm] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Worm,
            Label                = "Worm",
            DecayRate            = 0.84f,
            VelocityImpulseX     = 0.025f,
            VelocityImpulseY     = 0.07f,
            SinusoidalXFreq      = 0.12f,  // sinusoidal lateral wriggle
            SinusoidalXAmp       = 0.012f,
            HitBoneImpulseMult   = 0.9f,
            IdleBodyAmplitude    = 0.010f,
        };

        // ── FlyingBug — wing/thorax vibration, light rapid micro-oscillation ───
        d[MonsterPhysicsArchetype.FlyingBug] = new()
        {
            Archetype            = MonsterPhysicsArchetype.FlyingBug,
            Label                = "FlyingBug",
            DecayRate            = 0.78f,
            VelocityImpulseX     = 0.04f,
            VelocityImpulseY     = 0.04f,
            RandomNoiseX         = 0.015f,
            RandomNoiseY         = 0.010f,
            HasWings             = true,
            HitWingFollowMult    = 0.25f,
            IdleBodyAmplitude    = 0.008f,
        };

        // ── Furry — fur ripple, gentle surface wave ────────────────────────────
        d[MonsterPhysicsArchetype.Furry] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Furry,
            Label                = "Furry",
            DecayRate            = 0.90f,
            VelocityImpulseX     = 0.03f,
            VelocityImpulseY     = 0.03f,
            HasFurChain          = true,
            HasTailChain         = true,
            TailSegments         = 4,
            FurSegments          = 4,
            HitFurFollowMult     = 0.20f,  // fur ripples after body hit
            HitTailFollowMult    = 0.12f,
            IdleBodyAmplitude    = 0.008f,
            IdleAppendageAmplitude = 0.010f,
        };

        // ── Skeleton — sharp clatter, very fast decay ─────────────────────────
        d[MonsterPhysicsArchetype.Skeleton] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Skeleton,
            Label                = "Skeleton",
            DecayRate            = 0.72f,
            VelocityImpulseX     = 0.07f,
            VelocityImpulseY     = 0.07f,
            HitBoneImpulseMult   = 1.5f,  // bones clack on impact
            IdleBodyAmplitude    = 0.006f,
        };

        // ── Dragon — massive wing-beat + tail thrash + ground rumble ──────────
        d[MonsterPhysicsArchetype.Dragon] = new()
        {
            Archetype              = MonsterPhysicsArchetype.Dragon,
            Label                  = "Dragon",
            DecayRate              = 0.95f,
            VelocityImpulseX       = 0.12f,
            VelocityImpulseY       = 0.12f,
            PeriodicPeriod         = 25,      // wingbeat every 25 ticks
            PeriodicImpulseX       = 0.09f,   // random X flutter
            PeriodicImpulseY       = 0.12f,   // strong downward wing-thrust
            SinusoidalXFreq        = 0.15f,   // tail thrash lateral oscillation
            SinusoidalXAmp         = 0.03f,
            RandomNoiseY           = 0.01f,
            VelocityThresholdSq    = 2f,      // ground rumble when sprinting
            ThresholdNoiseX        = 0.05f,
            ThresholdNoiseY        = 0.05f,
            HasWings               = true,
            HasTailChain           = true,
            TailSegments           = 5,       // longer for big dragons
            HitWingFollowMult      = 0.45f,   // wings buckle on hit
            HitTailFollowMult      = 0.25f,
            HitBoneImpulseMult     = 0.8f,    // heavy mass absorbs some force
            IdleBodyAmplitude      = 0.016f,
            IdleAppendageAmplitude = 0.020f,
            StiffnessMult          = 0.80f,   // slightly looser springs = lumbering mass feel
            DampingMult            = 0.90f,
        };

        // ── Elemental — sinusoidal magical energy pulsing ─────────────────────
        d[MonsterPhysicsArchetype.Elemental] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Elemental,
            Label                = "Elemental",
            DecayRate            = 0.82f,
            VelocityImpulseX     = 0.05f,
            VelocityImpulseY     = 0.05f,
            SinusoidalXFreq      = 0.30f,
            SinusoidalXAmp       = 0.022f,
            SinusoidalYFreq      = 0.25f,
            SinusoidalYAmp       = 0.022f,
            HitBoneImpulseMult   = 1.1f,
            IdleBodyAmplitude    = 0.012f,
        };

        // ── Bird — flap + head-bob, wings react on hit ────────────────────────
        d[MonsterPhysicsArchetype.Bird] = new()
        {
            Archetype              = MonsterPhysicsArchetype.Bird,
            Label                  = "Bird",
            DecayRate              = 0.82f,
            VelocityImpulseX       = 0.05f,
            VelocityImpulseY       = 0.04f,
            RandomNoiseX           = 0.010f,
            PeriodicPeriod         = 18,     // wing-flap every 18 ticks
            PeriodicImpulseY       = 0.06f,
            HasWings               = true,
            HasEarBones            = true,   // comb/beak bone
            HitWingFollowMult      = 0.30f,
            IdleBodyAmplitude      = 0.009f,
            IdleAppendageAmplitude = 0.012f,
        };

        // ── HeavyBeast — large quadruped (troll, golem, bear) ─────────────────
        d[MonsterPhysicsArchetype.HeavyBeast] = new()
        {
            Archetype            = MonsterPhysicsArchetype.HeavyBeast,
            Label                = "HeavyBeast",
            DecayRate            = 0.88f,
            VelocityImpulseX     = 0.035f,
            VelocityImpulseY     = 0.060f,  // Y-dominant: heavy belly/body mass
            VelocityThresholdSq  = 3f,
            ThresholdNoiseX      = 0.025f,
            ThresholdNoiseY      = 0.025f,
            HasFurChain          = true,
            HasTailChain         = true,
            TailSegments         = 3,
            FurSegments          = 3,
            HitBoneImpulseMult   = 1.2f,
            HitFurFollowMult     = 0.15f,
            HitTailFollowMult    = 0.08f,
            IdleBodyAmplitude    = 0.012f,
            StiffnessMult        = 1.10f,   // stiffer: heavy mass
            DampingMult          = 1.10f,
        };

        // ── Insect — ground crawling (beetle, spider, ant) ────────────────────
        d[MonsterPhysicsArchetype.Insect] = new()
        {
            Archetype            = MonsterPhysicsArchetype.Insect,
            Label                = "Insect",
            DecayRate            = 0.76f,
            VelocityImpulseX     = 0.045f,
            VelocityImpulseY     = 0.035f,
            RandomNoiseX         = 0.016f,
            RandomNoiseY         = 0.010f,
            HitBoneImpulseMult   = 0.9f,
            IdleBodyAmplitude    = 0.006f,
        };

        // ── FarmAnimalHeavy — large farm animals: cow, goat, sheep, bull ───────
        d[MonsterPhysicsArchetype.FarmAnimalHeavy] = new()
        {
            Archetype              = MonsterPhysicsArchetype.FarmAnimalHeavy,
            Label                  = "FarmAnimalHeavy",
            DecayRate              = 0.88f,
            VelocityImpulseX       = 0.020f,
            VelocityImpulseY       = 0.040f,
            HasEarBones            = true,
            HasTailChain           = true,
            HasFurChain            = true,
            TailSegments           = 3,
            FurSegments            = 3,
            HitBoneImpulseMult     = 0.7f,  // large mass absorbs hits
            HitTailFollowMult      = 0.08f,
            HitFurFollowMult       = 0.12f,
            IdleBodyAmplitude      = 0.010f,
            IdleAppendageAmplitude = 0.015f,
            StiffnessMult          = 1.10f,
            DampingMult            = 1.15f,
        };

        // ── FarmAnimalLight — small farm animals: rabbit, chicken, duck, pig ──
        d[MonsterPhysicsArchetype.FarmAnimalLight] = new()
        {
            Archetype              = MonsterPhysicsArchetype.FarmAnimalLight,
            Label                  = "FarmAnimalLight",
            DecayRate              = 0.84f,
            VelocityImpulseX       = 0.040f,
            VelocityImpulseY       = 0.055f,  // hop bounce
            RandomNoiseY           = 0.006f,
            HasEarBones            = true,
            HasTailChain           = true,
            TailSegments           = 2,
            HitBoneImpulseMult     = 1.0f,
            HitTailFollowMult      = 0.10f,
            IdleBodyAmplitude      = 0.009f,
            IdleAppendageAmplitude = 0.016f,  // active ear flop
        };

        // ── PetDog — dog: floppy ears, tail wag, collar/charm chain ───────────
        d[MonsterPhysicsArchetype.PetDog] = new()
        {
            Archetype              = MonsterPhysicsArchetype.PetDog,
            Label                  = "PetDog",
            DecayRate              = 0.86f,
            VelocityImpulseX       = 0.040f,
            VelocityImpulseY       = 0.045f,
            HasEarBones            = true,
            HasTailChain           = true,
            TailSegments           = 4,
            HitBoneImpulseMult     = 0.9f,
            HitTailFollowMult      = 0.15f,
            IdleBodyAmplitude      = 0.010f,
            IdleAppendageAmplitude = 0.020f,  // wagging tail even at rest
        };

        // ── PetCat — cat: twitchy ears, S-curve tail, light recoil ───────────
        d[MonsterPhysicsArchetype.PetCat] = new()
        {
            Archetype              = MonsterPhysicsArchetype.PetCat,
            Label                  = "PetCat",
            DecayRate              = 0.84f,
            VelocityImpulseX       = 0.045f,
            VelocityImpulseY       = 0.040f,
            RandomNoiseX           = 0.008f,  // occasional twitch
            HasEarBones            = true,
            HasTailChain           = true,
            TailSegments           = 5,        // longer sinuous tail
            HitBoneImpulseMult     = 0.85f,
            HitTailFollowMult      = 0.20f,
            IdleBodyAmplitude      = 0.008f,
            IdleAppendageAmplitude = 0.018f,
        };

        // ── FantasyFamiliar — dragon familiar, fairy, magical companion ───────
        d[MonsterPhysicsArchetype.FantasyFamiliar] = new()
        {
            Archetype              = MonsterPhysicsArchetype.FantasyFamiliar,
            Label                  = "FantasyFamiliar",
            DecayRate              = 0.87f,
            VelocityImpulseX       = 0.045f,
            VelocityImpulseY       = 0.045f,
            SinusoidalXFreq        = 0.20f,   // magical shimmer oscillation
            SinusoidalXAmp         = 0.010f,
            PeriodicPeriod         = 20,       // wing-flap
            PeriodicImpulseY       = 0.05f,
            HasWings               = true,
            HasTailChain           = true,
            TailSegments           = 4,
            HitWingFollowMult      = 0.30f,
            HitTailFollowMult      = 0.15f,
            IdleBodyAmplitude      = 0.010f,
            IdleAppendageAmplitude = 0.014f,
        };

        return d;
    }
}
