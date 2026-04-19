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

    /// <summary>Idle amplitude scale when in the Relaxed state (default = 1.0).</summary>
    public float IdleRelaxedScale { get; set; } = 1.0f;

    /// <summary>Idle amplitude scale when in the Alert state (combat-ready, smaller by default).</summary>
    public float IdleAlertScale { get; set; } = 0.55f;

    /// <summary>Idle amplitude scale when Tired (low health / stamina).</summary>
    public float IdleTiredScale { get; set; } = 0.60f;

    /// <summary>Event rate multiplier when in Relaxed state.</summary>
    public float IdleRelaxedEventRate { get; set; } = 1.0f;

    /// <summary>Event rate multiplier when in Alert state (more twitchy).</summary>
    public float IdleAlertEventRate { get; set; } = 1.6f;

    // ── Idle archetype signature ──────────────────────────────────────────────
    //
    // These fields encode the *character* of idle motion for each archetype —
    // which signals are prominent, how heavy/light the breathing is, how frequent
    // the tail wag, etc.  The idle motion generator in ModEntry.SimulateIdle /
    // SimulateMonsterIdle reads these values to choose which IdleEventKind to
    // schedule and with what amplitudes, avoiding hardcoded per-species switch
    // statements.
    //
    // All amplitudes are in spring-unit space (before PhysicsVisualScale).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Amplitude of the breathing-driven chest/belly impulse applied each breath cycle.
    /// 0 = no breathing impulse.  Humanoids and large animals use ~0.012; slimes ~0.020.
    /// </summary>
    public float BreathingAmplitude { get; set; } = 0.012f;

    /// <summary>
    /// Period in ticks between breathing impulse peaks.
    /// Default 90 (~1.5 s at 60 ticks/s).  Sleeping creatures use ~180; excited use ~60.
    /// </summary>
    public int BreathingPeriodTicks { get; set; } = 90;

    /// <summary>
    /// How strongly breathing is fed into the hair chain as a secondary driver.
    /// 0 = hair is unaffected by breathing; 0.15 = subtle sway; 0.4 = very expressive.
    /// </summary>
    public float BreathingHairFeedMult { get; set; } = 0.15f;

    /// <summary>
    /// How strongly breathing is fed into the belly bone.
    /// 0 = belly unaffected; 0.6 = strong belly heave.
    /// </summary>
    public float BreathingBellyFeedMult { get; set; } = 0.40f;

    /// <summary>
    /// How strongly the tail base is driven by hip sway during idle.
    /// 0 = tail self-driven only; 1 = tail fully follows hips.
    /// Default 0.35 for tailed creatures.
    /// </summary>
    public float IdleTailHipCoupling { get; set; } = 0.35f;

    /// <summary>
    /// Average ticks between tail wag bursts (idle schedule, non-emotional).
    /// 0 = no idle wag schedule.  PetDog = 50 (frequent), Dragon = 250 (rare).
    /// </summary>
    public int IdleTailWagIntervalTicks { get; set; } = 0;

    /// <summary>
    /// Number of wag-burst impulses fired per scheduled wag event.
    /// Default 1.  PetDog uses 3 (rapid wagging bursts).
    /// </summary>
    public int IdleTailWagBurstCount { get; set; } = 1;

    /// <summary>
    /// Average ticks between ear-twitch events.
    /// 0 = no ear twitches.  Pets and farm animals usually have one; slimes/skeletons don't.
    /// </summary>
    public int IdleEarTwitchIntervalTicks { get; set; } = 0;

    /// <summary>
    /// Average ticks between snout-bob / sniff events.
    /// 0 = no snout-bob.  Used by pigs, rabbits, wolves, dogs.
    /// </summary>
    public int IdleSnoutBobIntervalTicks { get; set; } = 0;

    /// <summary>
    /// Average ticks between fur-ripple events.
    /// 0 = no fur ripple.  Fur ripple is driven by breathing / torso sway — this
    /// schedules an *additional* occasional surface ripple burst.
    /// </summary>
    public int IdleFurRippleIntervalTicks { get; set; } = 0;

    /// <summary>
    /// Average ticks between wing-rustle / fold-unfold events.
    /// 0 = no wing rustles.  Bats and dragons use low values (frequent membrane
    /// fidgeting); birds use higher values.
    /// </summary>
    public int IdleWingRustleIntervalTicks { get; set; } = 0;

    /// <summary>
    /// List of <see cref="IdleEventKind"/> values that are active for this archetype.
    /// Empty = use the global default set for humanoids.
    ///
    /// Stored as a comma-separated string for JSON compatibility.
    /// Example: "EarTwitch,TailFlick,SnoutSniff"
    /// </summary>
    public string IdleEventKinds { get; set; } = string.Empty;

    /// <summary>
    /// Parses <see cref="IdleEventKinds"/> into a list of <see cref="IdleEventKind"/> values.
    /// Returns an empty list if the field is empty (fall back to caller-default).
    /// </summary>
    public IReadOnlyList<IdleEventKind> GetIdleEventKinds()
    {
        if (string.IsNullOrWhiteSpace(IdleEventKinds)) return Array.Empty<IdleEventKind>();

        var result = new List<IdleEventKind>();
        foreach (var part in IdleEventKinds.Split(','))
        {
            var trimmed = part.Trim();
            if (Enum.TryParse<IdleEventKind>(trimmed, ignoreCase: true, out var kind))
                result.Add(kind);
        }
        return result;
    }

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
            BreathingAmplitude   = 0.010f,
            BreathingPeriodTicks = 90,
            BreathingHairFeedMult  = 0.10f,
            BreathingBellyFeedMult = 0.30f,
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
            // Slime: slow internal pulse + occasional blob lean
            BreathingAmplitude      = 0.020f,
            BreathingPeriodTicks    = 80,
            BreathingBellyFeedMult  = 0.70f,
            IdleEventKinds          = "FurRipple,BodyShimmy",
            IdleFurRippleIntervalTicks = 120,
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
            // Bat: perched body sway, wing membrane twitch, head turns
            BreathingAmplitude      = 0.009f,
            BreathingPeriodTicks    = 70,
            IdleEventKinds          = "WingRustle,HeadShake",
            IdleWingRustleIntervalTicks = 80,
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
            // Wolf/furry: chest breathing, ear twitch, tail low sway, sniff/bob
            BreathingAmplitude      = 0.012f,
            BreathingPeriodTicks    = 85,
            BreathingBellyFeedMult  = 0.45f,
            IdleTailHipCoupling     = 0.40f,
            IdleTailWagIntervalTicks = 200,
            IdleEarTwitchIntervalTicks = 150,
            IdleSnoutBobIntervalTicks  = 120,
            IdleFurRippleIntervalTicks = 90,
            IdleEventKinds = "EarTwitch,TailFlick,FurRipple,SnoutSniff",
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
            // Dragon idle: slow chest expansion, tail swish every few seconds, wing membrane settle
            BreathingAmplitude        = 0.018f,
            BreathingPeriodTicks      = 110,   // slow deep breaths
            BreathingBellyFeedMult    = 0.60f,
            BreathingHairFeedMult     = 0.05f,
            IdleTailHipCoupling       = 0.50f,
            IdleTailWagIntervalTicks  = 250,   // occasional tail swish
            IdleWingRustleIntervalTicks = 180, // periodic membrane settle
            IdleEventKinds = "TailFlick,WingRustle,BodyShimmy",
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
            // Heavy farm animal: heavy breathing, slow snout bob, tail swish, ear flick
            BreathingAmplitude        = 0.016f,
            BreathingPeriodTicks      = 100,  // slower breaths, big animal
            BreathingBellyFeedMult    = 0.65f,
            IdleTailHipCoupling       = 0.35f,
            IdleTailWagIntervalTicks  = 180,
            IdleEarTwitchIntervalTicks = 130,
            IdleSnoutBobIntervalTicks  = 160,
            IdleFurRippleIntervalTicks = 200,
            IdleEventKinds = "EarTwitch,TailFlick,SnoutSniff,FurRipple,HeadDip",
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
            // Light farm animal: head bob (chicken), snout bob (pig/rabbit), ear flicks
            BreathingAmplitude        = 0.010f,
            BreathingPeriodTicks      = 75,
            BreathingBellyFeedMult    = 0.50f,
            IdleTailWagIntervalTicks  = 100,
            IdleEarTwitchIntervalTicks = 90,
            IdleSnoutBobIntervalTicks  = 100,
            IdleEventKinds = "EarTwitch,HeadDip,TailFlick,SnoutSniff",
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
            // Dog: tail wag bursts, ear twitches, chest breathing, head tilt
            BreathingAmplitude        = 0.013f,
            BreathingPeriodTicks      = 75,
            BreathingBellyFeedMult    = 0.45f,
            IdleTailHipCoupling       = 0.50f,
            IdleTailWagIntervalTicks  = 50,   // frequent happy wag
            IdleTailWagBurstCount     = 3,    // 3-burst wag
            IdleEarTwitchIntervalTicks = 100,
            IdleSnoutBobIntervalTicks  = 90,
            IdleEventKinds = "TailWag,EarTwitch,SnoutSniff,HeadDip,PawShift",
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
            // Cat: tail flick, ear twitch, loaf-breathing, head turn
            BreathingAmplitude        = 0.010f,
            BreathingPeriodTicks      = 95,    // calm loaf breathing
            BreathingBellyFeedMult    = 0.35f,
            IdleTailHipCoupling       = 0.30f,
            IdleTailWagIntervalTicks  = 90,   // slow independent tail flick
            IdleTailWagBurstCount     = 1,
            IdleEarTwitchIntervalTicks = 110,
            IdleEventKinds = "TailFlick,EarTwitch,HeadShake,BlinkDip",
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
            // Familiar: perched shimmer breathing, wing flap, tail sway
            BreathingAmplitude        = 0.011f,
            BreathingPeriodTicks      = 70,
            BreathingBellyFeedMult    = 0.40f,
            IdleTailHipCoupling       = 0.45f,
            IdleTailWagIntervalTicks  = 100,
            IdleWingRustleIntervalTicks = 90,
            IdleEventKinds = "WingRustle,TailFlick,BodyShimmy",
        };

        return d;
    }
}
