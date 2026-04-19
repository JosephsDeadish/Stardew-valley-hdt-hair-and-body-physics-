using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── Spring-damper primitives for per-bone body and hair-chain physics ─────────
//
// Model: Euler-integrated spring-damper (mass=1, dt=1 tick).
//
//   netForce  = externalForce  -  stiffness × position  -  damping × velocity
//   velocity += netForce
//   position += velocity
//
// At 60 fps, a typical jelly bone (stiffness=0.12, damping=0.10) with a
// velocity kick of 0.4 will:
//   • reach peak displacement ~0.9 units (≈9 px at scale 10) in ~5 ticks
//   • cross back through zero at ~10 ticks
//   • fully settle within ~25–30 ticks (~0.5 seconds of visible jiggle)
//
// Firmer bone (stiffness=0.25, damping=0.15): settles in ~15 ticks.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-bone spring-damper state.  Thread-unsafe (single-threaded game update).
/// </summary>
public struct BoneState
{
    /// <summary>Displacement from the bone's natural rest position (units).</summary>
    public Vector2 Position;

    /// <summary>Current velocity of the bone (units/tick).</summary>
    public Vector2 Velocity;

    /// <summary>
    /// Advance the spring simulation by one game tick.
    /// </summary>
    /// <param name="externalForce">
    ///   Force applied this tick (e.g. from character velocity, explosion, hit).
    ///   Treat as a velocity kick: it is added directly to velocity before integration.
    /// </param>
    /// <param name="stiffness">Spring constant k. Higher = snappier return. Typical 0.08–0.30.</param>
    /// <param name="damping">Damping coefficient c. Higher = less oscillation. Typical 0.06–0.20.</param>
    public void Step(Vector2 externalForce, float stiffness, float damping)
    {
        // Spring restoring force + viscous damping
        var restoreForce = -Position * stiffness;
        var dampForce    = -Velocity * damping;

        Velocity += externalForce + restoreForce + dampForce;
        Position += Velocity;
    }

    /// <summary>
    /// Instantly add velocity (impulse response — hit, explosion, warp step, etc.).
    /// </summary>
    public void ApplyImpulse(Vector2 impulse)
    {
        Velocity += impulse;
    }

    /// <summary>True when the bone has negligible motion and can be skipped in the render path.</summary>
    public readonly bool IsNearRest(float threshold = 0.003f)
        => Position.LengthSquared() < threshold * threshold
        && Velocity.LengthSquared() < threshold * threshold;

    /// <summary>Reset position and velocity to rest instantly.</summary>
    public void Reset()
    {
        Position = Vector2.Zero;
        Velocity = Vector2.Zero;
    }
}

// ── Named bone indices for per-character BoneGroup ────────────────────────────

/// <summary>
/// Indices into the <see cref="BoneGroup.Bones"/> array.
/// Keep in sync with <see cref="BoneGroup.BoneCount"/>.
/// </summary>
public static class BoneIndex
{
    // Shared (both profiles)
    public const int BellyCenter = 0;
    public const int ThighL      = 1;
    public const int ThighR      = 2;

    // Feminine-specific
    public const int BreastL = 3;
    public const int BreastR = 4;
    public const int ButtL   = 5;
    public const int ButtR   = 6;

    // Masculine-specific (overlaps feminine slots — same memory, different semantics)
    public const int Groin  = 3;  // alias of BreastL slot for masculine
    public const int MButtL = 5;  // alias of ButtL
    public const int MButtR = 6;  // alias of ButtR

    /// <summary>Total number of body bones per character.</summary>
    public const int BoneCount = 7;
}

/// <summary>
/// Holds all <see cref="BoneState"/> values for one character.
/// Created lazily on first use per character key.
/// </summary>
public sealed class BoneGroup
{
    public readonly BoneState[] Bones = new BoneState[BoneIndex.BoneCount];

    public ref BoneState BellyCenter => ref Bones[BoneIndex.BellyCenter];
    public ref BoneState ThighL      => ref Bones[BoneIndex.ThighL];
    public ref BoneState ThighR      => ref Bones[BoneIndex.ThighR];
    public ref BoneState BreastL     => ref Bones[BoneIndex.BreastL];
    public ref BoneState BreastR     => ref Bones[BoneIndex.BreastR];
    public ref BoneState ButtL       => ref Bones[BoneIndex.ButtL];
    public ref BoneState ButtR       => ref Bones[BoneIndex.ButtR];

    /// <summary>
    /// Advance all bones one tick using a shared centre-of-mass external force.
    /// Each bone receives a scaled + axis-modified version of the force that
    /// reflects its anatomical role:
    ///   BreastL/R  — equal Y (vertical bounce) + mirrored X (lateral jello splay)
    ///   ButtL/R    — strong Y + gentle mirrored X
    ///   BellyCenter— Y-dominant gentle bounce
    ///   ThighL/R   — Y-dominant with mirrored X step bounce
    ///   Groin (M)  — X-dominant slinky oscillation
    /// </summary>
    public void Step(
        BodyProfileType profile,
        Vector2 centerForce,
        float stiffness,
        float damping,
        ModConfig cfg)
    {
        if (profile == BodyProfileType.Feminine)
        {
            var bStr  = cfg.FemaleBreastStrength;
            var buStr = cfg.FemaleButtStrength;
            var beStr = cfg.FemaleBellyStrength;
            var thStr = cfg.FemaleThighStrength;

            // Breast: strong vertical bounce, lateral jello (mirrored X for L vs R)
            var breastForce = new Vector2(centerForce.X, centerForce.Y * 1.20f);
            Bones[BoneIndex.BreastL].Step(
                new Vector2(-breastForce.X * 0.55f, breastForce.Y) * bStr,
                stiffness * 0.90f, damping);        // slightly bouncier than body
            Bones[BoneIndex.BreastR].Step(
                new Vector2( breastForce.X * 0.55f, breastForce.Y) * bStr,
                stiffness * 0.90f, damping);

            // Butt: strong Y, gentle mirrored X
            Bones[BoneIndex.ButtL].Step(
                new Vector2(-centerForce.X * 0.30f, centerForce.Y * 1.30f) * buStr,
                stiffness, damping);
            Bones[BoneIndex.ButtR].Step(
                new Vector2( centerForce.X * 0.30f, centerForce.Y * 1.30f) * buStr,
                stiffness, damping);

            // Belly: moderate Y, tiny X
            Bones[BoneIndex.BellyCenter].Step(
                new Vector2(centerForce.X * 0.20f, centerForce.Y * 0.90f) * beStr,
                stiffness * 1.10f, damping * 1.10f);  // slightly stiffer than breast

            // Thighs: step-rhythm Y + mirrored X
            Bones[BoneIndex.ThighL].Step(
                new Vector2(-centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
            Bones[BoneIndex.ThighR].Step(
                new Vector2( centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
        }
        else if (profile == BodyProfileType.Masculine)
        {
            var grStr = cfg.MaleGroinStrength;
            var buStr = cfg.MaleButtStrength;
            var beStr = cfg.MaleBellyStrength;
            var thStr = cfg.MaleThighStrength;

            // Groin: X-dominant slinky (side-to-side oscillation)
            Bones[BoneIndex.Groin].Step(
                new Vector2(centerForce.X * 1.20f, centerForce.Y * 0.35f) * grStr,
                stiffness * 0.85f, damping * 0.90f);  // most elastic bone

            // Butt: strong Y
            Bones[BoneIndex.MButtL].Step(
                new Vector2(-centerForce.X * 0.35f, centerForce.Y * 1.20f) * buStr,
                stiffness, damping);
            Bones[BoneIndex.MButtR].Step(
                new Vector2( centerForce.X * 0.35f, centerForce.Y * 1.20f) * buStr,
                stiffness, damping);

            // Belly
            Bones[BoneIndex.BellyCenter].Step(
                new Vector2(centerForce.X * 0.20f, centerForce.Y * 0.80f) * beStr,
                stiffness * 1.10f, damping * 1.10f);

            // Thighs
            Bones[BoneIndex.ThighL].Step(
                new Vector2(-centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
            Bones[BoneIndex.ThighR].Step(
                new Vector2( centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
        }
        else // Androgynous
        {
            var baseStr = 0.35f;
            var gentleForce = centerForce * baseStr;
            for (int i = 0; i < BoneIndex.BoneCount; i++)
            {
                Bones[i].Step(gentleForce * 0.65f, stiffness, damping);
            }
        }
    }

    /// <summary>
    /// Apply an instant impulse to all bones (hit, explosion, ragdoll, etc.).
    /// Each bone gets an anatomically scaled version of the impulse.
    /// </summary>
    public void ApplyImpulse(BodyProfileType profile, Vector2 impulse, ModConfig cfg)
    {
        // Reuse the Step path with zero stiffness/damping (pure velocity kick)
        // but we need to distribute it properly across bones.
        // Treat the impulse as an external force this tick.
        this.Step(profile, impulse, 0f, 0f, cfg);
    }

    /// <summary>
    /// Compute the blended visual displacement for this group.
    /// Returns a Vector2 suitable for use as a screen-pixel offset after multiplying
    /// by PhysicsVisualScale.
    /// </summary>
    public Vector2 ComputeVisualDisplacement(BodyProfileType profile)
    {
        if (profile == BodyProfileType.Feminine)
        {
            // Dominant: average breast positions (most visible in 2D top-down)
            var breast = (Bones[BoneIndex.BreastL].Position + Bones[BoneIndex.BreastR].Position) * 0.5f;
            var butt   = (Bones[BoneIndex.ButtL].Position   + Bones[BoneIndex.ButtR].Position)   * 0.5f;
            var belly  = Bones[BoneIndex.BellyCenter].Position;
            var thigh  = (Bones[BoneIndex.ThighL].Position  + Bones[BoneIndex.ThighR].Position)  * 0.5f;

            // Weighted blend: breast 50%, butt 25%, belly 15%, thighs 10%
            return breast * 0.50f + butt * 0.25f + belly * 0.15f + thigh * 0.10f;
        }
        else if (profile == BodyProfileType.Masculine)
        {
            var groin = Bones[BoneIndex.Groin].Position;
            var butt  = (Bones[BoneIndex.MButtL].Position + Bones[BoneIndex.MButtR].Position) * 0.5f;
            var belly = Bones[BoneIndex.BellyCenter].Position;
            var thigh = (Bones[BoneIndex.ThighL].Position + Bones[BoneIndex.ThighR].Position) * 0.5f;

            return groin * 0.35f + butt * 0.35f + belly * 0.20f + thigh * 0.10f;
        }
        else
        {
            // Androgynous: average of all
            var sum = Vector2.Zero;
            for (int i = 0; i < BoneIndex.BoneCount; i++)
            {
                sum += Bones[i].Position;
            }

            return sum / BoneIndex.BoneCount;
        }
    }

    /// <summary>Reset all bones to rest (on save load, location change, etc.).</summary>
    public void Reset()
    {
        for (int i = 0; i < BoneIndex.BoneCount; i++)
        {
            Bones[i].Reset();
        }
    }

    /// <summary>True when every bone is near rest (skip render path for performance).</summary>
    public bool IsAllNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < BoneIndex.BoneCount; i++)
        {
            if (!Bones[i].IsNearRest(threshold))
            {
                return false;
            }
        }

        return true;
    }
}

// ── Hair chain physics ────────────────────────────────────────────────────────

/// <summary>
/// A linked chain of hair segments simulated as cascading spring-dampers.
///
/// How it works:
///   Segment 0 is "anchored" at the head — it receives the full external force.
///   Segment i (i>0) is spring-chained to segment i-1:
///     • It receives a fraction of the external force (attenuated each step)
///     • The relative displacement to its parent pulls it back like a spring
///   The tip (last segment) gives the hair's tip position for visual offset.
///
/// This produces the natural cascade: the hair base reacts first, the tip follows
/// with a lag — creating the "flowing/whipping" HDT hair effect.
/// </summary>
public sealed class HairChain
{
    private readonly BoneState[] segments;
    private readonly int segmentCount;

    public HairChain(int segmentCount)
    {
        this.segmentCount = Math.Max(2, Math.Min(segmentCount, 8));
        segments = new BoneState[this.segmentCount];
    }

    /// <summary>Advance all segments one tick.</summary>
    /// <param name="rootExternalForce">Force applied to segment 0 (head movement, wind, etc.).</param>
    /// <param name="stiffness">Spring constant (same for all segments).</param>
    /// <param name="damping">Damping coefficient.</param>
    public void Step(Vector2 rootExternalForce, float stiffness, float damping)
    {
        // Segment 0: full external force, anchored spring (pulls to world origin)
        segments[0].Step(rootExternalForce, stiffness, damping);

        // Each subsequent segment: receives attenuated force + spring toward parent
        var attenuation = 0.70f;  // each segment gets 70% of parent's external force
        var parentInfluence = 0.25f; // how strongly each segment is pulled toward parent

        var force = rootExternalForce;
        for (int i = 1; i < segmentCount; i++)
        {
            force *= attenuation;

            // Chain constraint: spring pull toward parent's position
            var toParent = segments[i - 1].Position - segments[i].Position;
            var chainForce = toParent * parentInfluence;

            segments[i].Step(force + chainForce, stiffness * 0.80f, damping * 0.90f);
        }
    }

    /// <summary>Apply an instant velocity kick to all segments (wind gust, hit, etc.).</summary>
    public void ApplyImpulse(Vector2 impulse)
    {
        // Root gets full impulse; tips get attenuated (cascade effect)
        var scale = 1.0f;
        for (int i = 0; i < segmentCount; i++)
        {
            segments[i].ApplyImpulse(impulse * scale);
            scale *= 0.75f;
        }
    }

    /// <summary>
    /// Returns the visual displacement of the hair tip (weighted toward the last segment).
    /// This is the value to use for the hair visual offset in the render path.
    /// </summary>
    public Vector2 GetTipDisplacement()
    {
        if (segmentCount == 0)
        {
            return Vector2.Zero;
        }

        // Weight: last segment 50%, second-last 30%, third-last 20%
        var tip     = segments[segmentCount - 1].Position;
        var midTip  = segmentCount >= 2 ? segments[segmentCount - 2].Position : tip;
        var midRoot = segmentCount >= 3 ? segments[segmentCount - 3].Position : midTip;

        return tip * 0.50f + midTip * 0.30f + midRoot * 0.20f;
    }

    /// <summary>True when all segments are near rest.</summary>
    public bool IsNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < segmentCount; i++)
        {
            if (!segments[i].IsNearRest(threshold))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reset all segments to rest.</summary>
    public void Reset()
    {
        for (int i = 0; i < segmentCount; i++)
        {
            segments[i].Reset();
        }
    }

    public int SegmentCount => segmentCount;
}
