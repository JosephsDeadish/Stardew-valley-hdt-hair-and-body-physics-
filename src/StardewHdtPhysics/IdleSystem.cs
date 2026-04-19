using StardewValley;

namespace StardewHdtPhysics;

// ── Procedural idle animation system ─────────────────────────────────────────
//
// Drives per-character idle animations as layered procedural forces on top of
// the spring-damper physics engine.  The system works in three tiers:
//
//  1. Continuous micro-motion: breathing, slow sway — always active at rest.
//  2. Per-state profile scaling: Relaxed/Alert/Tired/Sleeping each modulate
//     amplitude, frequency, and spring stiffness/damping.
//  3. Scheduled event impulses: occasional ear twitches, tail flicks, head
//     shakes, wing rustles, etc. — break up the loop and make entities feel
//     alive without looking robotic.
//
// All values are in spring-unit space (before PhysicsVisualScale).
// ─────────────────────────────────────────────────────────────────────────────

// ── Idle state ────────────────────────────────────────────────────────────────

/// <summary>
/// Emotional/behavioural state that modulates idle amplitude and frequency.
/// Detected automatically each update from game context — no manual setup needed.
/// </summary>
public enum IdleState
{
    /// <summary>Normal, calm standing idle.  Default when nothing else applies.</summary>
    Relaxed,

    /// <summary>
    /// Character recently took damage, swung a weapon, or received an impact impulse.
    /// Stiffer spring, smaller amplitude, faster micro-sway — ready to react.
    /// </summary>
    Alert,

    /// <summary>
    /// Character has low health or stamina.  Slower, heavier, more lethargic.
    /// Deeper breathing, minimal sway.
    /// </summary>
    Tired,

    /// <summary>
    /// Character is sleeping or has fainted.
    /// Very slow deep-breathing only, almost no lateral sway.
    /// </summary>
    Sleeping,

    /// <summary>
    /// Character is examining something or facing an interactable.
    /// Forward-lean micro-sway, higher head-dip event rate.
    /// </summary>
    Curious,

    /// <summary>
    /// Character is at full health, full stamina, outdoors during the day.
    /// Slightly more expressive amplitude and higher event frequency.
    /// </summary>
    Energetic,
}

// ── Per-state scaling profile ─────────────────────────────────────────────────

/// <summary>
/// Per-<see cref="IdleState"/> scaling parameters applied multiplicatively on
/// top of the global idle amplitude and spring constants.
/// </summary>
public sealed class IdleStateProfile
{
    /// <summary>Multiplies all idle impulse amplitudes while in this state.</summary>
    public float AmplitudeScale { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the idle interval check period.
    /// &gt;1 = less frequent bursts (Tired/Sleeping); &lt;1 = more frequent (Alert).
    /// </summary>
    public float FrequencyScale { get; set; } = 1.0f;

    /// <summary>Multiplies the rate of scheduled idle events (twitches, etc.).</summary>
    public float EventRate { get; set; } = 1.0f;

    /// <summary>Additive bias on spring stiffness while in this state.</summary>
    public float StiffnessBias { get; set; } = 0.0f;

    /// <summary>Additive bias on spring damping while in this state.</summary>
    public float DampingBias { get; set; } = 0.0f;
}

/// <summary>
/// Factory that returns the built-in <see cref="IdleStateProfile"/> for any
/// <see cref="IdleState"/>.
/// </summary>
public static class IdleStateProfiles
{
    public static readonly IdleStateProfile Relaxed  = new() { AmplitudeScale = 1.00f, FrequencyScale = 1.00f, EventRate = 1.0f };
    public static readonly IdleStateProfile Alert    = new() { AmplitudeScale = 0.55f, FrequencyScale = 0.80f, EventRate = 1.6f, StiffnessBias = 0.03f, DampingBias = 0.01f };
    public static readonly IdleStateProfile Tired    = new() { AmplitudeScale = 0.60f, FrequencyScale = 1.50f, EventRate = 0.4f, DampingBias  = 0.02f };
    public static readonly IdleStateProfile Sleeping = new() { AmplitudeScale = 0.20f, FrequencyScale = 3.00f, EventRate = 0.05f, DampingBias = 0.06f };
    public static readonly IdleStateProfile Curious  = new() { AmplitudeScale = 0.70f, FrequencyScale = 1.10f, EventRate = 1.3f };
    public static readonly IdleStateProfile Energetic= new() { AmplitudeScale = 1.30f, FrequencyScale = 0.85f, EventRate = 1.4f };

    public static IdleStateProfile Get(IdleState state) => state switch
    {
        IdleState.Alert     => Alert,
        IdleState.Tired     => Tired,
        IdleState.Sleeping  => Sleeping,
        IdleState.Curious   => Curious,
        IdleState.Energetic => Energetic,
        _                   => Relaxed,
    };
}

// ── Idle event kinds ──────────────────────────────────────────────────────────

/// <summary>
/// Scheduled one-shot impulse events that break up continuous idle loops and
/// make characters feel alive without looking robotic.
/// </summary>
public enum IdleEventKind
{
    /// <summary>Fast lateral micro-flick; ear / appendage chain reacts.</summary>
    EarTwitch,
    /// <summary>Tip-of-tail snap; tail chain reacts.</summary>
    TailFlick,
    /// <summary>Broad multi-segment wag burst (pets / happy animals).</summary>
    TailWag,
    /// <summary>Wing membrane micro-flap; wing chain reacts.</summary>
    WingRustle,
    /// <summary>Brief lateral head shake + hair toss (humanoids / furry).</summary>
    HeadShake,
    /// <summary>Single nod/dip; head / hair responds.</summary>
    HeadDip,
    /// <summary>Full-body side-to-side shimmy (humanoids).</summary>
    BodyShimmy,
    /// <summary>Surface fur ripple; fur chain reacts.</summary>
    FurRipple,
    /// <summary>Snout-bob / sniff motion; body Y impulse.</summary>
    SnoutSniff,
    /// <summary>Subtle blink combined with a tiny head dip (humanoids).</summary>
    BlinkDip,
    /// <summary>Weight shift from one side to the other (quadrupeds).</summary>
    PawShift,
    /// <summary>Leg scratch cycle (quadrupeds, rare).</summary>
    Scratch,
}

// ── Per-event base cooldowns ──────────────────────────────────────────────────

// (min ticks, additional random range ticks)
internal static class EventCooldowns
{
    internal static readonly Dictionary<IdleEventKind, (int Min, int Range)> Table = new()
    {
        [IdleEventKind.EarTwitch]  = (80,  120),
        [IdleEventKind.TailFlick]  = (60,  100),
        [IdleEventKind.TailWag]    = (50,   80),
        [IdleEventKind.WingRustle] = (90,  140),
        [IdleEventKind.HeadShake]  = (120, 180),
        [IdleEventKind.HeadDip]    = (70,  110),
        [IdleEventKind.BodyShimmy] = (160, 250),
        [IdleEventKind.FurRipple]  = (55,   90),
        [IdleEventKind.SnoutSniff] = (65,   95),
        [IdleEventKind.BlinkDip]   = (100, 150),
        [IdleEventKind.PawShift]   = (90,  130),
        [IdleEventKind.Scratch]    = (300, 500),
    };
}

// ── Per-character idle event scheduler ───────────────────────────────────────

/// <summary>
/// Tracks per-<see cref="IdleEventKind"/> scheduling state for one character.
/// Events fire at randomised-but-bounded intervals so the idle never looks like
/// a repeating loop.  Not thread-safe (single-threaded game update).
/// </summary>
public sealed class IdleEventScheduler
{
    private readonly Dictionary<IdleEventKind, int> nextFireTick = new();

    /// <summary>
    /// Returns <c>true</c> if <paramref name="kind"/> should fire on
    /// <paramref name="currentTick"/>, and schedules the next fire time.
    /// <paramref name="eventRate"/> &gt; 1 = more frequent; ≤ 0 = never.
    /// </summary>
    public bool ShouldFire(IdleEventKind kind, int currentTick, float eventRate = 1.0f)
    {
        if (eventRate <= 0f) return false;

        if (!nextFireTick.TryGetValue(kind, out var next))
        {
            // Stagger first-fire so every kind doesn't burst simultaneously on spawn.
            var (min, range) = EventCooldowns.Table[kind];
            var firstDelay   = (int)((min + Game1.random.Next(range)) / Math.Max(0.1f, eventRate));
            nextFireTick[kind] = currentTick + firstDelay;
            return false;
        }

        if (currentTick < next) return false;

        // Compute next interval and record it.
        var (minC, rangeC) = EventCooldowns.Table[kind];
        var cooldown        = (int)((minC + Game1.random.Next(rangeC)) / Math.Max(0.1f, eventRate));
        nextFireTick[kind]  = currentTick + Math.Max(1, cooldown);
        return true;
    }

    /// <summary>Reset all scheduling state (e.g. on location change, new day).</summary>
    public void Reset() => nextFireTick.Clear();
}
