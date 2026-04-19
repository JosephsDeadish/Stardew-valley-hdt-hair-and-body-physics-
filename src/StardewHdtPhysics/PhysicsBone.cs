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
    // Euler stability bounds (spring-unit space, before PhysicsVisualScale).
    // Velocity cap: 2.5 units/tick ≈ 25 px at scale-10 — prevents runaway oscillation
    // when stiffness is high or many chain segments accumulate energy.
    // Displacement cap: 4.0 units ≈ 40 px — stops positional explosion on long chains.
    private const float MaxVelSq  = 2.5f * 2.5f;
    private const float MaxDisplSq = 4.0f * 4.0f;

    public void Step(Vector2 externalForce, float stiffness, float damping)
    {
        // Spring restoring force + viscous damping
        var restoreForce = -Position * stiffness;
        var dampForce    = -Velocity * damping;

        Velocity += externalForce + restoreForce + dampForce;
        Position += Velocity;

        // ── Euler stability clamps ────────────────────────────────────────────
        var velSq = Velocity.LengthSquared();
        if (velSq > MaxVelSq)
            Velocity *= 2.5f / MathF.Sqrt(velSq);

        var posSq = Position.LengthSquared();
        if (posSq > MaxDisplSq)
            Position *= 4.0f / MathF.Sqrt(posSq);
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
///
/// Breast multi-bone chain (HDT style, index 3–10):
///   The centre-of-mass BreastL/R bones are the primary spring (slots 3/4).
///   BreastUpperL/R (slots 7/8) model the upper-breast / cleavage region —
///     slightly stiffer, moves less on vertical bounce, more lateral splay.
///   BreastLowerL/R (slots 9/10) model the lower-breast / underside —
///     softest bone, most Y-bounce, gravity-assisted droop.
///   Together these three sub-bones per side produce a realistic HDT wave:
///     Upper → Centre → Lower progressively lags, creating a top-stay/bottom-jiggle motion.
/// </summary>
public static class BoneIndex
{
    // Shared (both profiles)
    public const int BellyCenter = 0;
    public const int ThighL      = 1;
    public const int ThighR      = 2;

    // Feminine-specific (centre-of-mass breast)
    public const int BreastL = 3;
    public const int BreastR = 4;
    public const int ButtL   = 5;
    public const int ButtR   = 6;

    // Feminine: extra breast bones — HDT multi-bone chain
    public const int BreastUpperL = 7;   // upper-breast / cleavage apex, stiffer
    public const int BreastUpperR = 8;
    public const int BreastLowerL = 9;   // lower-breast / underside, softest + most bounce
    public const int BreastLowerR = 10;

    // Masculine-specific (overlaps feminine slots — same memory, different semantics)
    public const int Groin  = 3;  // alias of BreastL slot for masculine
    public const int MButtL = 5;  // alias of ButtL
    public const int MButtR = 6;  // alias of ButtR

    /// <summary>Total number of body bones per character (7 base + 4 extra breast).</summary>
    public const int BoneCount = 11;
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
    ///   BreastL/R        — equal Y (vertical bounce) + mirrored X (lateral jello splay)
    ///   BreastUpperL/R   — stiffer, less Y, more lateral (apex stays more in place)
    ///   BreastLowerL/R   — softest, most Y, gravity droop (underside jiggles most)
    ///   ButtL/R    — strong Y + gentle mirrored X
    ///   BellyCenter— Y-dominant gentle bounce  (shirt-covered → breastMult)
    ///   ThighL/R   — Y-dominant with mirrored X step bounce  (pants-covered → lowerBodyMult)
    ///   Groin (M)  — X-dominant slinky oscillation  (pants-covered → lowerBodyMult)
    ///
    /// <paramref name="breastMult"/>:    [0.5, 1.0] from shirt coverage. Hat/boots = 1.0 (no effect on breast).
    /// <paramref name="lowerBodyMult"/>: [0.5, 1.0] from pants+boots coverage. Hat/shirt = 1.0 (no effect on lower body).
    /// Both default to 1.0 when clothing physics modifier is off or no clothing is worn.
    /// </summary>
    public void Step(
        BodyProfileType profile,
        Vector2 centerForce,
        float stiffness,
        float damping,
        ModConfig cfg,
        float breastMult = 1f,
        float lowerBodyMult = 1f)
    {
        if (profile == BodyProfileType.Feminine)
        {
            // Per-bone config strength × per-region clothing multiplier.
            // Breast and belly → breastMult (shirt slot).
            // Butt, thigh        → lowerBodyMult (pants+boots slots).
            var bStr  = cfg.FemaleBreastStrength  * breastMult;
            var buStr = cfg.FemaleButtStrength     * lowerBodyMult;
            var beStr = cfg.FemaleBellyStrength    * breastMult;   // belly is under shirt, not pants
            var thStr = cfg.FemaleThighStrength    * lowerBodyMult;

            // ── Centre-of-mass breast (primary) ───────────────────────────────
            var breastForce = new Vector2(centerForce.X, centerForce.Y * 1.20f);
            Bones[BoneIndex.BreastL].Step(
                new Vector2(-breastForce.X * 0.55f, breastForce.Y) * bStr,
                stiffness * 0.90f, damping);
            Bones[BoneIndex.BreastR].Step(
                new Vector2( breastForce.X * 0.55f, breastForce.Y) * bStr,
                stiffness * 0.90f, damping);

            // ── Upper breast — cleavage apex (stiffer, less Y, more lateral) ──
            Bones[BoneIndex.BreastUpperL].Step(
                new Vector2(-breastForce.X * 0.70f, breastForce.Y * 0.55f) * bStr,
                stiffness * 1.15f, damping * 1.10f);
            Bones[BoneIndex.BreastUpperR].Step(
                new Vector2( breastForce.X * 0.70f, breastForce.Y * 0.55f) * bStr,
                stiffness * 1.15f, damping * 1.10f);

            // ── Lower breast — underside (softest, most Y, gravity droop) ─────
            Bones[BoneIndex.BreastLowerL].Step(
                new Vector2(-breastForce.X * 0.40f, breastForce.Y * 1.55f) * bStr,
                stiffness * 0.72f, damping * 0.80f);
            Bones[BoneIndex.BreastLowerR].Step(
                new Vector2( breastForce.X * 0.40f, breastForce.Y * 1.55f) * bStr,
                stiffness * 0.72f, damping * 0.80f);

            // Butt: strong Y, gentle mirrored X  (pants-covered)
            Bones[BoneIndex.ButtL].Step(
                new Vector2(-centerForce.X * 0.30f, centerForce.Y * 1.30f) * buStr,
                stiffness, damping);
            Bones[BoneIndex.ButtR].Step(
                new Vector2( centerForce.X * 0.30f, centerForce.Y * 1.30f) * buStr,
                stiffness, damping);

            // Belly: moderate Y, tiny X  (shirt-covered)
            Bones[BoneIndex.BellyCenter].Step(
                new Vector2(centerForce.X * 0.20f, centerForce.Y * 0.90f) * beStr,
                stiffness * 1.10f, damping * 1.10f);

            // Thighs: step-rhythm Y + mirrored X  (pants-covered)
            Bones[BoneIndex.ThighL].Step(
                new Vector2(-centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
            Bones[BoneIndex.ThighR].Step(
                new Vector2( centerForce.X * 0.40f, centerForce.Y * 0.80f) * thStr,
                stiffness * 1.05f, damping);
        }
        else if (profile == BodyProfileType.Masculine)
        {
            // Groin, butt, thighs → lowerBodyMult.  Belly → breastMult (shirt covers belly for males too).
            var grStr = cfg.MaleGroinStrength  * lowerBodyMult;
            var buStr = cfg.MaleButtStrength   * lowerBodyMult;
            var beStr = cfg.MaleBellyStrength  * breastMult;
            var thStr = cfg.MaleThighStrength  * lowerBodyMult;

            // Groin: X-dominant slinky (side-to-side oscillation)
            Bones[BoneIndex.Groin].Step(
                new Vector2(centerForce.X * 1.20f, centerForce.Y * 0.35f) * grStr,
                stiffness * 0.85f, damping * 0.90f);

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

            // Extra breast slots (indices 7-10) unused for masculine — zero force, spring to rest.
            Bones[BoneIndex.BreastUpperL].Step(Vector2.Zero, stiffness * 2f, damping * 2f);
            Bones[BoneIndex.BreastUpperR].Step(Vector2.Zero, stiffness * 2f, damping * 2f);
            Bones[BoneIndex.BreastLowerL].Step(Vector2.Zero, stiffness * 2f, damping * 2f);
            Bones[BoneIndex.BreastLowerR].Step(Vector2.Zero, stiffness * 2f, damping * 2f);
        }
        else // Androgynous
        {
            var baseStr = 0.35f;
            // Androgynous blends both region mults evenly.
            var avgMult = (breastMult + lowerBodyMult) * 0.5f;
            var gentleForce = centerForce * baseStr * avgMult;
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
            // Multi-bone breast chain blend:
            //   Upper (15%) + Centre (35%) + Lower (15%) = 65% breast contribution
            //   Upper stays more in place (stiffer), Lower jiggles most (softest).
            var breastUpper = (Bones[BoneIndex.BreastUpperL].Position + Bones[BoneIndex.BreastUpperR].Position) * 0.5f;
            var breastCentre = (Bones[BoneIndex.BreastL].Position    + Bones[BoneIndex.BreastR].Position)       * 0.5f;
            var breastLower = (Bones[BoneIndex.BreastLowerL].Position + Bones[BoneIndex.BreastLowerR].Position) * 0.5f;
            var breastBlend = breastUpper * 0.15f + breastCentre * 0.35f + breastLower * 0.15f;  // = 0.65f total

            var butt   = (Bones[BoneIndex.ButtL].Position   + Bones[BoneIndex.ButtR].Position)   * 0.5f;
            var belly  = Bones[BoneIndex.BellyCenter].Position;
            var thigh  = (Bones[BoneIndex.ThighL].Position  + Bones[BoneIndex.ThighR].Position)  * 0.5f;

            // Weighted blend: breast chain 65%, butt 20%, belly 10%, thighs 5%
            return breastBlend + butt * 0.20f + belly * 0.10f + thigh * 0.05f;
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

// ── Wing chain physics ────────────────────────────────────────────────────────

/// <summary>
/// Per-wing spring chain modelling a flexible wing (bat, dragon, bird).
/// Inspired by Source Engine Jiggle Bones and Skyrim HDT wing setups.
///
/// Architecture (4 bones, innermost → outermost):
///   WingRoot  — attached to the creature's shoulder/back, receives full body force
///   WingInner — mid-inner segment; receives 80% of root force + spring toward root
///   WingOuter — mid-outer; receives 65% force + spring toward inner
///   WingTip   — free tip; receives 50% force + spring toward outer (most elastic)
///
/// The chain automatically produces the characteristic "flapping" motion:
///   On a wingbeat, the root gets a strong Y impulse; tip lags behind ~6 ticks
///   creating a visually realistic fold/unfold wave along the wing surface.
///
/// Two instances (Left + Right) should be created per winged creature.
/// L wing and R wing are given mirrored X forces so they flap symmetrically.
/// </summary>
public sealed class WingChain
{
    // Fixed 4 bones (Source/HDT wing convention)
    public const int SegmentCount = 4;

    // Attenuations per segment (how much parent external force passes down the chain)
    private static readonly float[] ForceAttenuation = { 1.00f, 0.80f, 0.65f, 0.50f };

    // How strongly each segment is spring-pulled toward its parent
    private static readonly float[] ChainInfluence = { 0f, 0.30f, 0.28f, 0.26f };

    private readonly BoneState[] segments = new BoneState[SegmentCount];

    /// <summary>Positive = left wing, Negative = right wing (mirrors X force).</summary>
    public readonly float LateralSign;

    public WingChain(float lateralSign = 1f)
    {
        LateralSign = lateralSign > 0 ? 1f : -1f;
    }

    /// <summary>
    /// Advance the wing chain by one tick.
    /// </summary>
    /// <param name="rootForce">Body-center external force (from velocity/wingbeat impulse).</param>
    /// <param name="stiffness">Spring return constant.</param>
    /// <param name="damping">Damping coefficient.</param>
    public void Step(Vector2 rootForce, float stiffness, float damping)
    {
        // Mirror X so left/right wings are symmetric
        var mirroredForce = new Vector2(rootForce.X * LateralSign, rootForce.Y);

        for (int i = 0; i < SegmentCount; i++)
        {
            var force = mirroredForce * ForceAttenuation[i];

            // Chain spring: each bone pulled toward its parent
            if (i > 0)
            {
                var toParent = segments[i - 1].Position - segments[i].Position;
                force += toParent * ChainInfluence[i];
            }

            // Wing tip is the most elastic — slightly softer spring
            var s = stiffness * (1f - i * 0.06f);  // root=s, tip=s*0.82
            var d = damping  * (1f - i * 0.03f);   // root=d, tip=d*0.91

            segments[i].Step(force, Math.Max(0.01f, s), Math.Max(0.01f, d));
        }
    }

    /// <summary>Apply an instant velocity impulse (wingbeat, hit, explosion).</summary>
    public void ApplyImpulse(Vector2 impulse)
    {
        var mir = new Vector2(impulse.X * LateralSign, impulse.Y);
        for (int i = 0; i < SegmentCount; i++)
        {
            segments[i].ApplyImpulse(mir * ForceAttenuation[i]);
        }
    }

    /// <summary>
    /// Visual displacement of the wing tip (weighted toward outer segments).
    /// Use this for the sprite offset.
    /// </summary>
    public Vector2 GetTipDisplacement()
    {
        // Weighted: tip 45%, outer 30%, inner 15%, root 10%
        return segments[3].Position * 0.45f
             + segments[2].Position * 0.30f
             + segments[1].Position * 0.15f
             + segments[0].Position * 0.10f;
    }

    public bool IsNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            if (!segments[i].IsNearRest(threshold)) return false;
        }
        return true;
    }

    public void Reset()
    {
        for (int i = 0; i < SegmentCount; i++) segments[i].Reset();
    }
}

/// <summary>
/// Paired left + right wings. One WingPair per winged creature.
/// </summary>
public sealed class WingPair
{
    public readonly WingChain Left  = new( 1f);
    public readonly WingChain Right = new(-1f);

    /// <summary>Advance both wings by one tick.</summary>
    public void Step(Vector2 bodyForce, float stiffness, float damping)
    {
        Left.Step(bodyForce,  stiffness, damping);
        Right.Step(bodyForce, stiffness, damping);
    }

    /// <summary>Apply impulse to both wings simultaneously.</summary>
    public void ApplyImpulse(Vector2 impulse)
    {
        Left.ApplyImpulse(impulse);
        Right.ApplyImpulse(impulse);
    }

    /// <summary>
    /// Combined visual displacement (average of both wing tips, X cancels on symmetric flap
    /// leaving the Y displacement that drives the up/down wingbeat visual).
    /// </summary>
    public Vector2 GetVisualDisplacement()
    {
        var l = Left.GetTipDisplacement();
        var r = Right.GetTipDisplacement();
        // Average Y (both wings go up/down together), X is kept for asymmetric banking
        return new Vector2((l.X + r.X) * 0.5f, (l.Y + r.Y) * 0.5f);
    }

    public bool IsNearRest(float threshold = 0.003f)
        => Left.IsNearRest(threshold) && Right.IsNearRest(threshold);

    public void Reset()
    {
        Left.Reset();
        Right.Reset();
    }
}

// ── Fur chain physics ─────────────────────────────────────────────────────────

/// <summary>
/// Surface fur ripple chain.  Simulates the motion of fur or short hair along a
/// creature's body surface — not pendant like hair, but surface-following with
/// lateral wave propagation.
///
/// Architecture:
///   3–6 segments, base-to-tip direction runs away from the creature's skin.
///   Base segment tracks the body closely (high chain influence).
///   Middle segments have intermediate lag.
///   Tip segments are the most free and produce the "ripple" visible at the fur surface.
///
/// Force model differs from HairChain:
///   • Base gets full body force (same as HairChain root)
///   • Attenuation is gentler (0.80 per step) — fur sticks to the body more than hair
///   • Chain influence is higher (0.40) — each segment is pulled harder toward its parent
///     so the fur doesn't fly out as far as hair does
/// </summary>
public sealed class FurChain
{
    private readonly BoneState[] segments;
    private readonly int segmentCount;

    public FurChain(int segmentCount)
    {
        this.segmentCount = Math.Max(2, Math.Min(segmentCount, 6));
        segments = new BoneState[this.segmentCount];
    }

    public void Step(Vector2 baseForce, float stiffness, float damping)
    {
        const float Attenuation   = 0.80f;
        const float ChainInfluence = 0.40f;

        segments[0].Step(baseForce, stiffness, damping);

        var force = baseForce;
        for (int i = 1; i < segmentCount; i++)
        {
            force *= Attenuation;
            var toParent = segments[i - 1].Position - segments[i].Position;
            segments[i].Step(force + toParent * ChainInfluence, stiffness * 0.85f, damping * 0.88f);
        }
    }

    public void ApplyImpulse(Vector2 impulse)
    {
        for (int i = 0; i < segmentCount; i++)
            segments[i].ApplyImpulse(impulse * MathF.Pow(0.80f, i));
    }

    /// <summary>Weighted average of all segments (fur moves uniformly unlike hair tip).</summary>
    public Vector2 GetDisplacement()
    {
        if (segmentCount == 0) return Vector2.Zero;
        var sum = Vector2.Zero;
        var weight = 1.0f;
        var totalW = 0f;
        for (int i = 0; i < segmentCount; i++)
        {
            sum    += segments[i].Position * weight;
            totalW += weight;
            weight *= 0.8f;
        }
        return totalW > 0 ? sum / totalW : Vector2.Zero;
    }

    public bool IsNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < segmentCount; i++)
            if (!segments[i].IsNearRest(threshold)) return false;
        return true;
    }

    public void Reset()
    {
        for (int i = 0; i < segmentCount; i++) segments[i].Reset();
    }

    public int SegmentCount => segmentCount;
}

// ── Tail chain physics ────────────────────────────────────────────────────────

/// <summary>
/// Pendant tail chain.  Like HairChain but tuned for a heavier, stiffer tail:
///   • Base bone is stiffer (close to the body, can't flail freely)
///   • Outer bones soften progressively toward the tip
///   • Lateral component is emphasized (tails wag side-to-side more than hair flows)
///   • Gravity simulation: tail droops downward when stationary (Y bias toward rest)
///
/// Default 4 segments: tail_root → tail_mid1 → tail_mid2 → tail_tip.
/// Dragon tails use 5 segments for extra length.
/// </summary>
public sealed class TailChain
{
    private readonly BoneState[] segments;
    private readonly int segmentCount;

    public TailChain(int segmentCount)
    {
        this.segmentCount = Math.Max(2, Math.Min(segmentCount, 6));
        segments = new BoneState[this.segmentCount];
    }

    public void Step(Vector2 bodyForce, float stiffness, float damping)
    {
        const float Attenuation   = 0.72f;   // tails are heavier, attenuate faster
        const float ChainInfluence = 0.32f;  // stiffer chain than hair

        // Emphasize lateral (X) force for tail wag
        var tailForce = new Vector2(bodyForce.X * 1.40f, bodyForce.Y * 0.85f);

        segments[0].Step(tailForce, stiffness, damping);

        var force = tailForce;
        for (int i = 1; i < segmentCount; i++)
        {
            force *= Attenuation;
            var toParent = segments[i - 1].Position - segments[i].Position;
            // Tail tip softens progressively
            var s = stiffness * (1f - i * 0.08f);
            var d = damping   * (1f - i * 0.05f);
            segments[i].Step(force + toParent * ChainInfluence, Math.Max(0.02f, s), Math.Max(0.02f, d));
        }
    }

    public void ApplyImpulse(Vector2 impulse)
    {
        // Lateral impulse for tail wag, attenuated toward tip
        var lateralImpulse = new Vector2(impulse.X * 1.3f, impulse.Y * 0.8f);
        for (int i = 0; i < segmentCount; i++)
            segments[i].ApplyImpulse(lateralImpulse * MathF.Pow(0.72f, i));
    }

    public Vector2 GetTipDisplacement()
    {
        if (segmentCount < 2) return segments[0].Position;
        var tip    = segments[segmentCount - 1].Position;
        var midTip = segments[segmentCount - 2].Position;
        return tip * 0.55f + midTip * 0.45f;
    }

    public bool IsNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < segmentCount; i++)
            if (!segments[i].IsNearRest(threshold)) return false;
        return true;
    }

    public void Reset()
    {
        for (int i = 0; i < segmentCount; i++) segments[i].Reset();
    }

    public int SegmentCount => segmentCount;
}

// ── Animal bone group ─────────────────────────────────────────────────────────

/// <summary>
/// Per-animal spring bone set modelled after source-engine animal jiggle bones.
/// Each species gets anatomically appropriate bone assignments:
///
///   Chicken/Duck/Bird:
///     EarL/EarR → comb/wattle bobs, Snout → beak peck, Body → chest bounce
///   Rabbit:
///     EarL/EarR → independent long ear flop (very soft springs), Body → hop bounce
///   Cow/Goat/Sheep/Pig/Bull:
///     EarL/EarR → ear flick, Snout → head bob/sniff, Body → belly jiggle (large mass)
///   Horse/Ostrich:
///     EarL/EarR → ear prick, Body → back bounce while running
///   Dog/Cat (pet):
///     EarL/EarR → ear flop (soft for floppy ears, firm for pointed), Body → body wag
///
/// Bone indices within this group (not shared with BoneIndex):
/// </summary>
public static class AnimalBoneIdx
{
    public const int EarL  = 0;
    public const int EarR  = 1;
    public const int Snout = 2;  // beak/snout/head
    public const int Body  = 3;  // chest/belly center

    public const int Count = 4;
}

/// <summary>
/// Holds the spring state for all animal bones.
/// Created lazily per animal instance.
/// </summary>
public sealed class AnimalBoneGroup
{
    public readonly BoneState[] Bones = new BoneState[AnimalBoneIdx.Count];

    /// <summary>
    /// Advance all animal bones one tick.
    /// </summary>
    /// <param name="bodyForce">External force from velocity/idle (body-center).</param>
    /// <param name="stiffness">Base spring constant.</param>
    /// <param name="damping">Base damping.</param>
    /// <param name="isHeavy">True for large animals (cow, pig, etc.) — reduces ear/snout elasticity.</param>
    public void Step(Vector2 bodyForce, float stiffness, float damping, bool isHeavy)
    {
        // Body: moderate response (large mass = stiffer)
        var bodyScale = isHeavy ? 0.60f : 0.85f;
        var bodyStiff = isHeavy ? stiffness * 1.20f : stiffness;
        var bodyDamp  = isHeavy ? damping   * 1.15f : damping;
        Bones[AnimalBoneIdx.Body].Step(bodyForce * bodyScale, bodyStiff, bodyDamp);

        // Ears: mirrored X, moderate Y — ears flick sideways and bounce
        var earScale  = isHeavy ? 0.45f : 0.90f;  // rabbits = very floppy (light) vs cow = stiff
        var earStiff  = isHeavy ? stiffness * 0.85f : stiffness * 0.55f;  // floppy vs firm
        var earDamp   = isHeavy ? damping   * 0.90f : damping   * 0.65f;
        Bones[AnimalBoneIdx.EarL].Step(
            new Vector2(-bodyForce.X * 0.60f, bodyForce.Y * 0.75f) * earScale,
            earStiff, earDamp);
        Bones[AnimalBoneIdx.EarR].Step(
            new Vector2( bodyForce.X * 0.60f, bodyForce.Y * 0.75f) * earScale,
            earStiff, earDamp);

        // Snout/beak: Y-dominant (bob/peck), gentle X
        var snoutScale = isHeavy ? 0.40f : 0.65f;
        Bones[AnimalBoneIdx.Snout].Step(
            new Vector2(bodyForce.X * 0.20f, bodyForce.Y * 1.10f) * snoutScale,
            stiffness * 0.95f, damping);
    }

    public void ApplyImpulse(Vector2 impulse)
    {
        Bones[AnimalBoneIdx.Body].ApplyImpulse(impulse * 0.85f);
        Bones[AnimalBoneIdx.EarL].ApplyImpulse(new Vector2(-impulse.X * 0.70f, impulse.Y * 0.60f));
        Bones[AnimalBoneIdx.EarR].ApplyImpulse(new Vector2( impulse.X * 0.70f, impulse.Y * 0.60f));
        Bones[AnimalBoneIdx.Snout].ApplyImpulse(new Vector2(impulse.X * 0.30f, impulse.Y * 0.90f));
    }

    /// <summary>Blended visual displacement from all animal bones.</summary>
    public Vector2 GetVisualDisplacement()
    {
        var body  = Bones[AnimalBoneIdx.Body].Position;
        var earL  = Bones[AnimalBoneIdx.EarL].Position;
        var earR  = Bones[AnimalBoneIdx.EarR].Position;
        var snout = Bones[AnimalBoneIdx.Snout].Position;

        // Body 50%, ears average 25%, snout 25%
        var earAvg = (earL + earR) * 0.5f;
        return body * 0.50f + earAvg * 0.25f + snout * 0.25f;
    }

    public bool IsAllNearRest(float threshold = 0.003f)
    {
        for (int i = 0; i < AnimalBoneIdx.Count; i++)
            if (!Bones[i].IsNearRest(threshold)) return false;
        return true;
    }

    public void Reset()
    {
        for (int i = 0; i < AnimalBoneIdx.Count; i++) Bones[i].Reset();
    }
}



// ── Typed physics debris particles ───────────────────────────────────────────

/// <summary>
/// Material type of a typed physics particle.
/// Controls its visual appearance (colour, size) and physical constants
/// (gravity, drag, bounce coefficient).
/// Self-contained — requires no additional mods or content packs.
/// </summary>
public enum PhysicsParticleKind
{
    /// <summary>Elongated brown chip expelled from wood impacts and tree falls.</summary>
    WoodSplinter,
    /// <summary>Tiny tan dot, heavy air drag, fast fade — sawdust clouds from cut wood.</summary>
    Sawdust,
    /// <summary>Grey square, heavier gravity, one-bounce — gravel/stone from rocks and geodes.</summary>
    StoneChunk,
    /// <summary>Warm copper/bronze tint — ore fragments from ore veins and geodes.</summary>
    OreChunk,
    /// <summary>Bright-blue tint, light — gem/crystal shards.</summary>
    GemChunk,
}

/// <summary>
/// A single typed physics debris particle rendered by the mod's own SpriteBatch pass.
/// Simulates a parabolic arc with air resistance, a one-time bounce reaction,
/// and gradual fade-out over its lifetime.
/// All position/velocity values are in world-pixel coordinates (64 units = 1 tile).
/// No extra content required — draws coloured pixel quads directly.
/// </summary>
public sealed class TypedPhysicsParticle
{
    /// <summary>World-space position in pixels.</summary>
    public Vector2 Position;

    /// <summary>
    /// Velocity in world-pixels per game tick.
    /// Positive Y = downward (screen-space convention).
    /// </summary>
    public Vector2 Velocity;

    /// <summary>Current spin angle in radians.</summary>
    public float Rotation;

    /// <summary>Spin rate in radians per tick; decays with air resistance.</summary>
    public float RotationVelocity;

    /// <summary>How many game ticks this particle has been alive.</summary>
    public int AgeTicks;

    /// <summary>Total lifetime in ticks before the particle is removed.</summary>
    public int MaxAgeTicks;

    /// <summary>Visual/physical type — determines colour, size, gravity, and drag.</summary>
    public PhysicsParticleKind Kind;

    /// <summary>
    /// Rendered size in game-pixel units (before zoom scaling).
    /// Set at spawn time using per-kind size range so particles vary even within the same kind.
    /// </summary>
    public float Size;

    /// <summary>
    /// Set to <c>true</c> after the particle has already bounced once.
    /// Prevents infinite multi-bounces.
    /// </summary>
    public bool HasBounced;
}
