using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── ItemPhysics infrastructure ────────────────────────────────────────────────
//
// Provides a self-contained rigid-body simulation layer for world items heavier
// than pure VFX particles (ore chunks, gem shards, wood splinters that persist
// on the ground, etc.).
//
// Key differences from TypedPhysicsParticle (pure VFX):
//   • Per-material bucket caps — tree-fall wood can't evict gem chunks
//   • Sleep/wake system — settled items stop simulating until disturbed
//   • Distance-based LOD — full/half/skip simulation tiers by tile range
//   • Material-based bounce, friction, drag, terminal velocity
//   • Break threshold → enqueues ItemPhysicsBreakEvent (ModEntry spawns VFX)
//   • Euler stability — velocity magnitude clamped per tick
//
// All world-pixel / tick units (64 world-px = 1 tile, 60 ticks = 1 second).
// ─────────────────────────────────────────────────────────────────────────────

// ── Material and shape enums ─────────────────────────────────────────────────

/// <summary>
/// Physical material class for <see cref="ItemPhysicsState"/>.
/// Determines gravity, drag, bounce, friction, terminal velocity, and what
/// debris particles are spawned when the item breaks.
/// </summary>
public enum ItemPhysicsMaterial
{
    /// <summary>Heavy, low bounce, high friction. Pickaxe chips, geode shards.</summary>
    Stone = 0,
    /// <summary>Heavy+, slight ring bounce, medium friction. Metal ore fragments.</summary>
    Metal = 1,
    /// <summary>Medium, low bounce, splintery break. Axe chips, log pieces.</summary>
    Wood  = 2,
    /// <summary>Very light, almost no bounce, high air drag. Cloth scraps, petal dust.</summary>
    Cloth = 3,
    /// <summary>Light, low bounce, very fragile (low BreakSpeed). Glass shards, bottles.</summary>
    Glass = 4,
    /// <summary>Medium, medium-high bounce, crystalline break. Gem clusters.</summary>
    Gem   = 5,
    /// <summary>Heavy, low bounce, similar to Stone but copper/bronze VFX.</summary>
    Ore   = 6,
}

/// <summary>
/// Collision / query shape used by <see cref="ItemPhysicsState"/>.
/// Point is the cheapest (no area query). Others define scatter and render extents.
/// </summary>
public enum ItemPhysicsShape
{
    Point,
    Circle,
    Box,
    Capsule,
}

// ── Per-material physical constants ──────────────────────────────────────────

/// <summary>
/// Immutable per-material physics constants.
/// All values use world-pixel / tick units.
/// </summary>
public readonly struct ItemPhysicsMaterialProps
{
    /// <summary>Downward acceleration per tick (px/tick²).</summary>
    public readonly float Gravity;
    /// <summary>Velocity multiplier per tick (air resistance). Typical [0.90, 0.99].</summary>
    public readonly float Drag;
    /// <summary>Y-velocity reflection coefficient on bounce. 0 = dead stop, 1 = elastic.</summary>
    public readonly float BounceCoeff;
    /// <summary>X-velocity loss on bounce (0 = frictionless, 1 = full stop).</summary>
    public readonly float Friction;
    /// <summary>Rotation velocity multiplier per tick (angular drag).</summary>
    public readonly float AngularDrag;
    /// <summary>Maximum downward speed (terminal velocity, px/tick).</summary>
    public readonly float TerminalVelocity;
    /// <summary>Impact Y-speed (px/tick) that triggers a break event.</summary>
    public readonly float BreakSpeed;
    /// <summary>Kind of <see cref="TypedPhysicsParticle"/> debris spawned when the item breaks.</summary>
    public readonly PhysicsParticleKind DebrisKind;
    /// <summary>Number of debris particles spawned on break.</summary>
    public readonly int DebrisCount;
    /// <summary>Maximum live items of this material (per-bucket cap).</summary>
    public readonly int BucketCap;
    /// <summary>Walk-scatter radius (px). Heavier = smaller so heavy chunks don't jump around.</summary>
    public readonly float ScatterRadius;
    /// <summary>Walk-scatter sensitivity multiplier [0, 1]. Heavy chunks near 0, cloth near 1.</summary>
    public readonly float ScatterSensitivity;

    public ItemPhysicsMaterialProps(
        float gravity, float drag, float bounceCoeff, float friction,
        float angularDrag, float terminalVel, float breakSpeed,
        PhysicsParticleKind debrisKind, int debrisCount, int bucketCap,
        float scatterRadius, float scatterSensitivity)
    {
        Gravity            = gravity;
        Drag               = drag;
        BounceCoeff        = bounceCoeff;
        Friction           = friction;
        AngularDrag        = angularDrag;
        TerminalVelocity   = terminalVel;
        BreakSpeed         = breakSpeed;
        DebrisKind         = debrisKind;
        DebrisCount        = debrisCount;
        BucketCap          = bucketCap;
        ScatterRadius      = scatterRadius;
        ScatterSensitivity = scatterSensitivity;
    }

    /// <summary>Lookup table — returns per-material physical constants.</summary>
    public static ItemPhysicsMaterialProps For(ItemPhysicsMaterial mat) => mat switch
    {
        //                          grav  drag  bnc   fric  adrg  term  brk   debrisKind                          cnt  cap  scR  scS
        ItemPhysicsMaterial.Stone => new(0.22f, 0.98f, 0.30f, 0.55f, 0.97f, 9f, 4.5f, PhysicsParticleKind.StoneChunk,   4,  40, 35f, 0.35f),
        ItemPhysicsMaterial.Metal => new(0.20f, 0.98f, 0.42f, 0.45f, 0.96f, 9f, 5.0f, PhysicsParticleKind.StoneChunk,   3,  35, 38f, 0.30f),
        ItemPhysicsMaterial.Wood  => new(0.15f, 0.97f, 0.25f, 0.60f, 0.96f, 8f, 3.5f, PhysicsParticleKind.WoodSplinter, 5,  50, 55f, 0.70f),
        ItemPhysicsMaterial.Cloth => new(0.06f, 0.91f, 0.05f, 0.80f, 0.90f, 5f, 2.0f, PhysicsParticleKind.Sawdust,      3,  25, 80f, 1.00f),
        ItemPhysicsMaterial.Glass => new(0.18f, 0.97f, 0.20f, 0.50f, 0.94f, 8f, 2.5f, PhysicsParticleKind.GemChunk,     6,  25, 45f, 0.60f),
        ItemPhysicsMaterial.Gem   => new(0.14f, 0.96f, 0.50f, 0.40f, 0.95f, 8f, 6.0f, PhysicsParticleKind.GemChunk,     4,  30, 45f, 0.55f),
        ItemPhysicsMaterial.Ore   => new(0.20f, 0.97f, 0.35f, 0.50f, 0.97f, 9f, 4.0f, PhysicsParticleKind.OreChunk,     4,  40, 38f, 0.40f),
        _                         => new(0.18f, 0.97f, 0.30f, 0.55f, 0.96f, 9f, 4.0f, PhysicsParticleKind.StoneChunk,   3,  35, 45f, 0.50f),
    };
}

// ── Break event ───────────────────────────────────────────────────────────────

/// <summary>
/// Raised by <see cref="ItemPhysicsWorld"/> when an item impacts hard enough to shatter.
/// ModEntry consumes these to call <c>SpawnTypedDebris</c>.
/// </summary>
public readonly record struct ItemPhysicsBreakEvent(
    Vector2            Position,
    PhysicsParticleKind DebrisKind,
    int                Count);

// ── Item state ────────────────────────────────────────────────────────────────

/// <summary>
/// Full simulation state for one physics-driven world item.
/// Managed exclusively by <see cref="ItemPhysicsWorld"/>.
/// </summary>
public sealed class ItemPhysicsState
{
    // ── Motion ───────────────────────────────────────────────────────────────
    public Vector2 Position;
    public Vector2 Velocity;
    public float   Rotation;
    public float   RotationVelocity;

    // ── Descriptor ───────────────────────────────────────────────────────────
    public ItemPhysicsMaterial Material;
    public ItemPhysicsShape    Shape;
    /// <summary>Radius for Circle/Capsule; half-extent X for Box.</summary>
    public float ShapeRadius    = 4f;
    /// <summary>Half-extent Y for Box. Unused for other shapes.</summary>
    public float ShapeHalfH     = 4f;
    /// <summary>Rendered visual size in world-pixels (independent of shape).</summary>
    public float VisualSize     = 3.5f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public int  AgeTicks;
    /// <summary>0 = immortal.</summary>
    public int  MaxAgeTicks;
    public bool IsAlive = true;

    // ── Sleep state ───────────────────────────────────────────────────────────
    /// <summary>Consecutive ticks spent below the speed threshold.</summary>
    public int  SlowTicks;
    public bool IsAsleep;

    // ── Bounce / break ────────────────────────────────────────────────────────
    public bool HasBounced;
    public bool IsBroken;

    // ── LOD ───────────────────────────────────────────────────────────────────
    /// <summary>Distance to the player, updated each world step.</summary>
    public float DistToPlayer;
    /// <summary>Frame counter for half-rate LOD (skip every other tick).</summary>
    public int   LodCounter;
}

// ── World ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages a collection of <see cref="ItemPhysicsState"/> objects with:
/// <list type="bullet">
///   <item><b>Per-material bucket caps</b> — tree-fall wood cannot evict gem/ore chunks.</item>
///   <item><b>Sleep / wake system</b> — idle items stop simulating until disturbed.</item>
///   <item><b>Distance-based LOD</b> — full / half / skip simulation by tile range.</item>
///   <item><b>Material-based collision</b> — distinct bounce coefficient, friction, and drag per kind.</item>
///   <item><b>Euler stability</b> — velocity clamped per tick; terminal velocity enforced.</item>
///   <item><b>Break threshold</b> — hard impacts enqueue <see cref="ItemPhysicsBreakEvent"/>.</item>
/// </list>
/// Thread-unsafe; update exclusively from the game's main thread.
/// </summary>
public sealed class ItemPhysicsWorld
{
    // ── LOD distance thresholds (world-pixels) ───────────────────────────────
    /// <summary>Items closer than this receive full simulation every tick.</summary>
    private const float DistFull = 256f;  // 4 tiles
    /// <summary>Items in [DistFull, DistHalf] simulate every 2 ticks.</summary>
    private const float DistHalf = 640f;  // 10 tiles
    // Items beyond DistHalf are skipped unless they are still awake.

    // ── Sleep thresholds ──────────────────────────────────────────────────────
    private const float SleepSpeedSq     = 0.08f * 0.08f; // below this speed → accumulate sleep ticks
    private const int   SleepTicksNeeded = 50;             // ticks at low speed before sleeping (~0.8 s)
    private const float WakeImpulseMin   = 0.12f;          // scatter force (px/tick) needed to wake

    // ── Velocity stability clamp (world-px / tick) ────────────────────────────
    private const float MaxInitialVelSq = 12f * 12f; // cap on spawn impulse

    // ── Per-material buckets ──────────────────────────────────────────────────
    private readonly List<ItemPhysicsState>[]    _buckets;
    private readonly ItemPhysicsMaterialProps[]  _props;

    // ── Break event queue ─────────────────────────────────────────────────────
    private readonly Queue<ItemPhysicsBreakEvent> _breakQueue = new();

    // ── Player tracking (internal — used to derive scatter velocity) ──────────
    private Vector2 _lastPlayerPos;
    private bool    _hasPlayerPos;

    // ── Total item count (for diagnostics) ───────────────────────────────────
    public int TotalCount
    {
        get
        {
            var n = 0;
            foreach (var b in _buckets) n += b.Count;
            return n;
        }
    }

    public ItemPhysicsWorld()
    {
        var matCount = Enum.GetValues<ItemPhysicsMaterial>().Length;
        _buckets     = new List<ItemPhysicsState>[matCount];
        _props       = new ItemPhysicsMaterialProps[matCount];

        for (int m = 0; m < matCount; m++)
        {
            var mat      = (ItemPhysicsMaterial)m;
            _buckets[m]  = new List<ItemPhysicsState>();
            _props[m]    = ItemPhysicsMaterialProps.For(mat);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn a new physics item.  If the per-material bucket is full the oldest
    /// item of the same material is evicted first (tree-fall wood cannot evict gems).
    /// The spawn velocity is clamped to prevent Euler blow-up.
    /// </summary>
    public void Spawn(
        Vector2             position,
        Vector2             velocity,
        ItemPhysicsMaterial material,
        ItemPhysicsShape    shape       = ItemPhysicsShape.Circle,
        float               shapeRadius = 4f,
        float               visualSize  = 3.5f,
        int                 maxAgeTicks = 0)
    {
        var mi   = (int)material;
        var list = _buckets[mi];
        var cap  = _props[mi].BucketCap;

        // Per-bucket eviction: never evict from a different material's quota
        if (list.Count >= cap)
            list.RemoveAt(0);

        // Clamp initial velocity for Euler stability
        var velSq = velocity.LengthSquared();
        if (velSq > MaxInitialVelSq)
            velocity *= 12f / MathF.Sqrt(velSq);

        list.Add(new ItemPhysicsState
        {
            Position         = position,
            Velocity         = velocity,
            RotationVelocity = (Random.Shared.NextSingle() - 0.5f) * 0.20f,
            Material         = material,
            Shape            = shape,
            ShapeRadius      = shapeRadius,
            VisualSize       = visualSize,
            MaxAgeTicks      = maxAgeTicks,
        });
    }

    /// <summary>
    /// Advance all live items by one game tick.
    /// <paramref name="playerPos"/> is used for LOD distance and scatter radius.
    /// <paramref name="scatterStr"/> is the global scatter strength multiplier (from ModConfig).
    /// </summary>
    public void Step(Vector2 playerPos, float scatterStr)
    {
        // Derive player velocity from last known position
        var playerVel   = _hasPlayerPos ? playerPos - _lastPlayerPos : Vector2.Zero;
        var playerMoved = playerVel.LengthSquared() > 0.05f;
        _lastPlayerPos  = playerPos;
        _hasPlayerPos   = true;

        for (int mi = 0; mi < _buckets.Length; mi++)
        {
            var list  = _buckets[mi];
            var props = _props[mi];

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                item.AgeTicks++;

                // Expire mortal items
                if (item.MaxAgeTicks > 0 && item.AgeTicks >= item.MaxAgeTicks)
                {
                    list.RemoveAt(i);
                    continue;
                }

                // Already broken — remove (break event was already enqueued)
                if (item.IsBroken)
                {
                    list.RemoveAt(i);
                    continue;
                }

                // Update distance-to-player for LOD
                item.DistToPlayer = Vector2.Distance(item.Position, playerPos);

                // ── LOD tier selection ──────────────────────────────────────
                // Beyond DistHalf: skip sleeping items entirely; awake items get half-rate
                if (item.DistToPlayer > DistHalf)
                {
                    if (item.IsAsleep)
                        continue;
                    // Far but still settling — use half-rate
                    item.LodCounter++;
                    if ((item.LodCounter & 1) == 1)
                        continue;
                }
                else if (item.DistToPlayer > DistFull)
                {
                    // Medium range: half-rate
                    item.LodCounter++;
                    if ((item.LodCounter & 1) == 1)
                        continue;
                }
                // Close range: always simulate (LodCounter not advanced)

                // ── Walk-scatter impulse ────────────────────────────────────
                // Older ("resting") particles need a stronger shove to wake up.
                float scatterMag = 0f;
                if (scatterStr > 0f && playerMoved)
                {
                    var dist = item.DistToPlayer;
                    if (dist < props.ScatterRadius && dist > 0.5f)
                    {
                        // Direction away from player
                        var dx = item.Position.X - playerPos.X;
                        var dy = item.Position.Y - playerPos.Y;
                        var dLen = MathF.Sqrt(dx * dx + dy * dy);
                        if (dLen > 0f)
                        {
                            dx /= dLen; dy /= dLen;

                            var falloff    = 1f - dist / props.ScatterRadius;
                            var ageResist  = Math.Min(1f, item.AgeTicks / 120f); // 0 fresh → 1 settled
                            var wakeThresh = ageResist * 0.5f;                   // settled need harder push
                            var rawMag     = playerVel.Length() * falloff * scatterStr * props.ScatterSensitivity;

                            if (rawMag > wakeThresh)
                            {
                                scatterMag = (rawMag - wakeThresh) * (1f - ageResist * 0.5f);
                                item.Velocity.X       += dx * scatterMag;
                                item.Velocity.Y       += dy * scatterMag;
                                item.RotationVelocity += (Random.Shared.NextSingle() - 0.5f) * scatterMag * 0.2f;
                            }
                        }
                    }
                }

                // Wake a sleeping item if the scatter force is strong enough
                if (item.IsAsleep)
                {
                    if (scatterMag >= WakeImpulseMin)
                    {
                        item.IsAsleep  = false;
                        item.SlowTicks = 0;
                    }
                    else
                    {
                        continue; // sleeping and not woken — skip physics
                    }
                }

                // ── Gravity (enforce terminal velocity) ────────────────────
                if (item.Velocity.Y < props.TerminalVelocity)
                    item.Velocity.Y = MathF.Min(item.Velocity.Y + props.Gravity, props.TerminalVelocity);

                // ── One-time bounce (when falling speed crosses threshold) ──
                if (!item.HasBounced && item.AgeTicks > 8 && item.Velocity.Y > 1.8f)
                {
                    var impactSpeed = MathF.Abs(item.Velocity.Y);
                    item.Velocity.Y       *= -props.BounceCoeff;
                    item.Velocity.X       *= (1f - props.Friction);
                    item.RotationVelocity *= 1.3f;
                    item.HasBounced        = true;

                    // Break check — hard impact shatters fragile materials
                    if (impactSpeed >= props.BreakSpeed)
                    {
                        item.IsBroken = true;
                        _breakQueue.Enqueue(new ItemPhysicsBreakEvent(
                            item.Position, props.DebrisKind, props.DebrisCount));
                        list.RemoveAt(i);
                        continue;
                    }
                }

                // ── Air drag ───────────────────────────────────────────────
                item.Velocity         *= props.Drag;
                item.RotationVelocity *= props.AngularDrag;

                // ── Euler velocity clamp (Euler stability — prevents runaway on long frames) ─
                // Cap total speed at 3× terminal velocity so horizontal arcs aren't wrongly
                // throttled; the per-axis gravity cap above handles downward terminal speed.
                var velSq    = item.Velocity.LengthSquared();
                var maxVelSq = props.TerminalVelocity * props.TerminalVelocity * 9f; // (3×)²
                if (velSq > maxVelSq)
                    item.Velocity *= (props.TerminalVelocity * 3f) / MathF.Sqrt(velSq);

                // ── Position integration ───────────────────────────────────
                item.Position += item.Velocity;
                item.Rotation += item.RotationVelocity;

                // ── Sleep detection ────────────────────────────────────────
                if (item.Velocity.LengthSquared() < SleepSpeedSq &&
                    MathF.Abs(item.RotationVelocity) < 0.01f)
                {
                    item.SlowTicks++;
                    if (item.SlowTicks >= SleepTicksNeeded)
                        item.IsAsleep = true;
                }
                else
                {
                    item.SlowTicks = 0;
                }
            }
        }
    }

    /// <summary>Try to dequeue a break event.  Returns <c>false</c> when the queue is empty.</summary>
    public bool TryPopBreakEvent(out ItemPhysicsBreakEvent evt)
        => _breakQueue.TryDequeue(out evt);

    /// <summary>
    /// Provides read-only access to all live items in a material bucket for rendering.
    /// </summary>
    public IReadOnlyList<ItemPhysicsState> GetBucket(ItemPhysicsMaterial material)
        => _buckets[(int)material];

    /// <summary>
    /// Returns the number of live items in the specified material bucket.
    /// </summary>
    public int BucketCount(ItemPhysicsMaterial material)
        => _buckets[(int)material].Count;

    /// <summary>Clear all items and pending break events (call on location change / save load).</summary>
    public void Clear()
    {
        foreach (var b in _buckets) b.Clear();
        _breakQueue.Clear();
        _hasPlayerPos = false;
    }
}
