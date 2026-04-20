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
    private const float MaxVelSq   = 2.5f * 2.5f;
    private const float MaxDisplSq = 4.0f * 4.0f;
    // Below this threshold (both pos and vel) the bone snaps to rest to stop micro-jitter.
    private const float SnapThresholdSq = 0.0015f * 0.0015f;

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

        // ── Snap to rest — prevent perpetual micro-jitter ─────────────────────
        // When both displacement and velocity are negligibly small, zero them out
        // exactly.  This prevents floating-point residuals accumulating over time
        // and avoids the "infinite low-frequency buzz" problem described in the
        // physics design guide.
        if (Position.LengthSquared() < SnapThresholdSq && Velocity.LengthSquared() < SnapThresholdSq)
        {
            Position = Vector2.Zero;
            Velocity = Vector2.Zero;
        }
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

    /// <summary>
    /// Returns <c>true</c> if <paramref name="boneIndex"/> is a <b>limb secondary-offset bone</b>.
    ///
    /// <b>Limb design rule:</b> limb bones (ThighL/R, ButtL/R, Groin) add a <em>secondary
    /// physics offset</em> on top of the character's animation pose — they do NOT replace or
    /// drive the animated limb position.  The animation system owns the limb position; the
    /// physics bone only contributes a subtle soft-tissue jiggle, step-recoil, or weight-shift.
    ///
    /// Contrast with breast bones, whose displacements are primary visual contributions that
    /// are directly blended into the final character offset.
    /// </summary>
    public static bool IsLimbSecondaryOffsetBone(int boneIndex)
        => boneIndex is ThighL or ThighR or ButtL or ButtR or Groin;  // Groin = BreastL alias for masculine
}

// ── Body anchor table ─────────────────────────────────────────────────────────

/// <summary>
/// Per-facing-direction visibility weights for each <see cref="BoneIndex"/> slot.
///
/// These weights encode how much each physics bone's displacement should
/// contribute to the final blended visual offset, depending on which side of
/// the character is facing the camera.  A weight of 0 means "not visible from
/// this angle — suppress"; 1 means "fully visible — apply at full strength."
///
/// Design rationale (sprite-based fake-bone approach):
///   • In a 2D sprite game the whole character sprite moves as one unit.
///   • We can't independently offset a breast region vs a butt region.
///   • Instead, <see cref="BoneGroup.ComputeVisualDisplacement"/> blends all bone
///     positions into one representative offset, weighted by visibility.
///   • The resulting offset is what gets applied to <c>character.Position</c>.
///   • This correctly suppresses, e.g., breast bounce when we're looking at the
///     character's back, and suppresses butt when looking at the front.
///
/// Facing convention (matches Stardew Valley's <c>FacingDirection</c>):
///   0 = Up   (back of character visible)
///   1 = Right (character profile, right side)
///   2 = Down  (front of character visible)  ← default
///   3 = Left  (character profile, left side)
/// </summary>
public static class BodyAnchorTable
{
    // [facing, boneIndex] = visibility weight [0..1]
    // Organized as: facing-row, bone-column for cache friendliness.
    private static readonly float[,] Weights = new float[4, BoneIndex.BoneCount]
    {
        // ── Facing 0 (Up / back of character) ─────────────────────────────────
        // Butt dominant, thighs visible, belly/breasts suppressed (hidden behind body).
        {
            /* BellyCenter    */ 0.05f,
            /* ThighL         */ 0.50f,
            /* ThighR         */ 0.50f,
            /* BreastL        */ 0.10f,
            /* BreastR        */ 0.10f,
            /* ButtL          */ 0.90f,
            /* ButtR          */ 0.90f,
            /* BreastUpperL   */ 0.07f,
            /* BreastUpperR   */ 0.07f,
            /* BreastLowerL   */ 0.08f,
            /* BreastLowerR   */ 0.08f,
        },
        // ── Facing 1 (Right / profile, right side visible) ────────────────────
        // BreastR and ButtR are near-side and most prominent.
        {
            /* BellyCenter    */ 0.40f,
            /* ThighL         */ 0.30f,
            /* ThighR         */ 0.70f,
            /* BreastL        */ 0.25f,
            /* BreastR        */ 0.85f,
            /* ButtL          */ 0.25f,
            /* ButtR          */ 0.80f,
            /* BreastUpperL   */ 0.15f,
            /* BreastUpperR   */ 0.65f,
            /* BreastLowerL   */ 0.20f,
            /* BreastLowerR   */ 0.80f,
        },
        // ── Facing 2 (Down / front of character) ──────────────────────────────
        // Breasts and belly fully visible.  Butt suppressed.
        {
            /* BellyCenter    */ 0.80f,
            /* ThighL         */ 0.55f,
            /* ThighR         */ 0.55f,
            /* BreastL        */ 0.90f,
            /* BreastR        */ 0.90f,
            /* ButtL          */ 0.10f,
            /* ButtR          */ 0.10f,
            /* BreastUpperL   */ 0.70f,
            /* BreastUpperR   */ 0.70f,
            /* BreastLowerL   */ 0.95f,
            /* BreastLowerR   */ 0.95f,
        },
        // ── Facing 3 (Left / profile, left side visible) ──────────────────────
        // BreastL and ButtL are near-side (mirror of facing 1).
        {
            /* BellyCenter    */ 0.40f,
            /* ThighL         */ 0.70f,
            /* ThighR         */ 0.30f,
            /* BreastL        */ 0.85f,
            /* BreastR        */ 0.25f,
            /* ButtL          */ 0.80f,
            /* ButtR          */ 0.25f,
            /* BreastUpperL   */ 0.65f,
            /* BreastUpperR   */ 0.15f,
            /* BreastLowerL   */ 0.80f,
            /* BreastLowerR   */ 0.20f,
        },
    };

    /// <summary>
    /// Returns the visibility weight [0..1] for <paramref name="boneIndex"/> when the
    /// character is <paramref name="facing"/> (0=Up, 1=Right, 2=Down, 3=Left).
    /// Out-of-range inputs return 0.5 (neutral fallback).
    /// </summary>
    public static float Get(int boneIndex, int facing)
    {
        if ((uint)facing >= 4 || (uint)boneIndex >= BoneIndex.BoneCount)
            return NeutralWeight;
        return Weights[facing, boneIndex];
    }

    // Fallback weight for out-of-range inputs: mid-range so neither suppressed nor amplified.
    private const float NeutralWeight = 0.5f;

    /// <summary>
    /// Per-facing lateral scale for the left or right breast bone.
    /// Encodes how prominent the lateral (X-axis) jiggle component should be
    /// for that breast given the current facing direction.
    ///
    /// When the near-side breast is facing the camera (e.g. BreastR when facing right),
    /// it should contribute more lateral splay.  The far-side breast is partially hidden
    /// and contributes less.
    ///
    /// Returns a Vector2(lateralLeftBreast, lateralRightBreast) scale pair.
    /// </summary>
    public static (float lateralL, float lateralR) BreastLateralScale(int facing)
        => facing switch
        {
            0 => (0.20f, 0.20f),  // facing up — breasts hidden, minimal lateral
            1 => (0.30f, 0.80f),  // facing right — BreastR near-side, dominant
            2 => (0.60f, 0.60f),  // facing front — symmetric, standard
            3 => (0.80f, 0.30f),  // facing left  — BreastL near-side, dominant
            _ => (0.60f, 0.60f),
        };
}

// ── Fake-bone per-facing anchor offsets ──────────────────────────────────────

/// <summary>
/// Per-facing pixel anchor positions for every <see cref="BoneIndex"/> slot,
/// relative to the character's sprite draw origin (the sprite's centre-bottom
/// or top-left depending on convention — here we use top-left as (0,0) with Y
/// increasing downward, matching Stardew Valley's tile/draw space).
///
/// <b>Design rationale — fake-bone approach for sprite-based 2D characters:</b>
///
/// Stardew Valley characters are single sprites; there is no real skeleton.
/// We implement a fake-bone approach by hand-authoring anchor offsets for each
/// body zone and each facing direction.  Each physics bone then stores a LOCAL
/// displacement from that anchor:
/// <code>
///   bone_world_position = character_origin + AnchorOffset(bone, facing) + bone.Displacement
/// </code>
/// This is what keeps each bone "attached" to the correct region of the body
/// regardless of how physics forces move it.
///
/// <b>Breast anchor rule (enforced by design):</b>
/// BreastL/R and their Upper/Lower sub-bones always anchor to the upper-chest
/// region.  They are NEVER connected to the arm-limb anchor or the belly/torso
/// anchor.  Use <see cref="GetBreastChestAnchor"/> to retrieve the correct chest
/// anchor for either side.
///
/// <b>Limb anchor rule:</b>
/// ThighL/R and ButtL/R anchor to the lower-body / hip region.  Their physics
/// displacement is a SECONDARY OFFSET added on top of the animation pose — they
/// do not replace the animated limb position.  See <see cref="BoneIndex.IsLimbSecondaryOffsetBone"/>.
///
/// <b>Facing convention (matches Stardew Valley <c>FacingDirection</c>):</b>
/// <list type="bullet">
///   <item>0 = Up   — back of character visible; butt dominant</item>
///   <item>1 = Right — right-side profile; BreastR/ButtR near-side</item>
///   <item>2 = Down  — front visible; breasts dominant (default)</item>
///   <item>3 = Left  — left-side profile; BreastL/ButtL near-side</item>
/// </list>
///
/// <b>Example values (Facing Right / facing=1):</b>
/// <code>
///   BreastL anchor = (x+2, y-10)   ← far side, partially occluded
///   BreastR anchor = (x+6, y-10)   ← near side, prominent
///   ButtL   anchor = (x+1, y+8)    ← far side
///   ButtR   anchor = (x+4, y+8)    ← near side
///   ThighL  anchor = (x+1, y+14)   ← far side
///   ThighR  anchor = (x+4, y+14)   ← near side
/// </code>
/// </summary>
public static class BodyZoneAnchorOffsets
{
    // [facing, boneIndex] = pixel offset (x, y) from character sprite draw origin.
    // X: negative = left of sprite centre; positive = right.
    // Y: negative = above sprite centre (up); positive = below (down).
    private static readonly Vector2[,] Offsets = new Vector2[4, BoneIndex.BoneCount]
    {
        // ── Facing 0 (Up / back of character) ─────────────────────────────────
        // Butt visible and dominant.  Breasts are hidden behind the body — small offsets.
        {
            /* BellyCenter  */ new Vector2( 0f,   2f),
            /* ThighL       */ new Vector2(-4f,  14f),
            /* ThighR       */ new Vector2( 4f,  14f),
            /* BreastL      */ new Vector2(-3f, -10f),
            /* BreastR      */ new Vector2( 3f, -10f),
            /* ButtL        */ new Vector2(-5f,   6f),
            /* ButtR        */ new Vector2( 5f,   6f),
            /* BreastUpperL */ new Vector2(-3f, -13f),
            /* BreastUpperR */ new Vector2( 3f, -13f),
            /* BreastLowerL */ new Vector2(-3f,  -8f),
            /* BreastLowerR */ new Vector2( 3f,  -8f),
        },
        // ── Facing 1 (Right / right side visible) ─────────────────────────────
        // BreastR and ButtR are near-side — positioned further right (larger +x).
        // BreastL and ButtL are far-side — partially occluded, closer to centre.
        {
            /* BellyCenter  */ new Vector2( 3f,   2f),
            /* ThighL       */ new Vector2( 1f,  14f),
            /* ThighR       */ new Vector2( 4f,  14f),
            /* BreastL      */ new Vector2( 2f, -10f),
            /* BreastR      */ new Vector2( 6f, -10f),
            /* ButtL        */ new Vector2( 1f,   8f),
            /* ButtR        */ new Vector2( 4f,   8f),
            /* BreastUpperL */ new Vector2( 2f, -13f),
            /* BreastUpperR */ new Vector2( 6f, -13f),
            /* BreastLowerL */ new Vector2( 2f,  -8f),
            /* BreastLowerR */ new Vector2( 6f,  -8f),
        },
        // ── Facing 2 (Down / front of character) ──────────────────────────────
        // Symmetric front view.  Breasts fully visible and laterally spread.
        // Butt suppressed (behind the body).
        {
            /* BellyCenter  */ new Vector2( 0f,   2f),
            /* ThighL       */ new Vector2(-4f,  14f),
            /* ThighR       */ new Vector2( 4f,  14f),
            /* BreastL      */ new Vector2(-6f, -10f),
            /* BreastR      */ new Vector2( 6f, -10f),
            /* ButtL        */ new Vector2(-4f,   8f),
            /* ButtR        */ new Vector2( 4f,   8f),
            /* BreastUpperL */ new Vector2(-6f, -13f),
            /* BreastUpperR */ new Vector2( 6f, -13f),
            /* BreastLowerL */ new Vector2(-6f,  -8f),
            /* BreastLowerR */ new Vector2( 6f,  -8f),
        },
        // ── Facing 3 (Left / left side visible) ───────────────────────────────
        // Mirror of Facing 1: BreastL and ButtL are near-side (negative x dominant).
        {
            /* BellyCenter  */ new Vector2(-3f,   2f),
            /* ThighL       */ new Vector2(-4f,  14f),
            /* ThighR       */ new Vector2(-1f,  14f),
            /* BreastL      */ new Vector2(-6f, -10f),
            /* BreastR      */ new Vector2(-2f, -10f),
            /* ButtL        */ new Vector2(-4f,   8f),
            /* ButtR        */ new Vector2(-1f,   8f),
            /* BreastUpperL */ new Vector2(-6f, -13f),
            /* BreastUpperR */ new Vector2(-2f, -13f),
            /* BreastLowerL */ new Vector2(-6f,  -8f),
            /* BreastLowerR */ new Vector2(-2f,  -8f),
        },
    };

    /// <summary>
    /// Returns the pixel anchor offset for <paramref name="boneIndex"/> when the
    /// character is <paramref name="facing"/> (0=Up, 1=Right, 2=Down, 3=Left).
    /// Out-of-range inputs return <see cref="Vector2.Zero"/>.
    /// </summary>
    public static Vector2 Get(int boneIndex, int facing)
    {
        if ((uint)facing >= 4 || (uint)boneIndex >= BoneIndex.BoneCount)
            return Vector2.Zero;
        return Offsets[facing, boneIndex];
    }

    /// <summary>
    /// Returns the upper-chest anchor offset for the breast chain on the
    /// specified side.  This is the stiffest bone in the chain
    /// (<see cref="BoneIndex.BreastUpperL"/> / <see cref="BoneIndex.BreastUpperR"/>)
    /// and acts as the fixed chest anchor that <see cref="BoneIndex.BreastL"/>/<c>R</c>
    /// and <see cref="BoneIndex.BreastLowerL"/>/<c>R</c> chain toward.
    ///
    /// <b>Breast anchor rule:</b> breast bones always use this upper-chest anchor.
    /// They are never attached to the arm-limb anchor or the belly/torso anchor.
    /// </summary>
    /// <param name="facing">0=Up, 1=Right, 2=Down, 3=Left.</param>
    /// <param name="side">-1 = left side (BreastUpperL), +1 = right side (BreastUpperR).</param>
    public static Vector2 GetBreastChestAnchor(int facing, int side)
    {
        var idx = side < 0 ? BoneIndex.BreastUpperL : BoneIndex.BreastUpperR;
        return Get(idx, facing);
    }
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
    ///   BreastUpperL/R — stiffer, chest-anchored; stepped first so Center/Lower can chain to it
    ///   BreastL/R      — centre-of-mass; Y 1.40×; springs toward BreastUpper (chain pull)
    ///   BreastLowerL/R — softest, most Y 1.80×; springs toward BreastCenter (chain pull)
    ///   ButtL/R        — strong Y + gentle mirrored X
    ///   BellyCenter    — Y-dominant gentle bounce  (shirt-covered → breastMult)
    ///   ThighL/R       — Y-dominant + mirrored X step bounce  (pants-covered → lowerBodyMult)
    ///   Groin (M)      — X-dominant slinky oscillation  (pants-covered → lowerBodyMult)
    ///
    /// <paramref name="breastMult"/>:    [0.5, 1.0] from shirt coverage.
    /// <paramref name="lowerBodyMult"/>: [0.5, 1.0] from pants+boots coverage.
    /// <paramref name="facing"/>: 0=Up, 1=Right, 2=Down(default), 3=Left.
    ///   Controls per-side lateral prominence of breast bones so the near-side
    ///   breast is more expressive than the far-side breast in profile views.
    ///   Also affects how chain constraints are weighted per bone.
    /// </summary>
    // Pull fraction applied each tick to spring each breast sub-bone toward its parent.
    // At 0.18, a parent 0.5 units away exerts ~0.09 units/tick of corrective force —
    // strong enough to keep the chain connected, gentle enough not to override the spring.
    private const float BreastChainPull = 0.18f;

    public void Step(
        BodyProfileType profile,
        Vector2 centerForce,
        float stiffness,
        float damping,
        ModConfig cfg,
        float breastMult = 1f,
        float lowerBodyMult = 1f,
        int facing = 2)
    {
        if (profile == BodyProfileType.Feminine)
        {
            // Per-bone config strength × per-region clothing multiplier.
            var bStr  = cfg.FemaleBreastStrength  * breastMult;
            var buStr = cfg.FemaleButtStrength     * lowerBodyMult;
            var beStr = cfg.FemaleBellyStrength    * breastMult;
            var thStr = cfg.FemaleThighStrength    * lowerBodyMult;

            // Y amplified 1.40 for gravity-bias jiggle (HDT OCBC style)
            var breastForce = new Vector2(centerForce.X, centerForce.Y * 1.40f);

            // Per-facing lateral prominence: near-side breast dominant, far-side reduced.
            // This implements the "left breast stays attached to left chest anchor,
            // right breast stays attached to right chest anchor" requirement.
            var (latL, latR) = BodyAnchorTable.BreastLateralScale(facing);

            // ── Step 1: BreastUpper — chest-anchored, stiffest ────────────────
            // These are the "closest to the body" bones and define the anchor
            // the Center and Lower bones will chain toward.
            Bones[BoneIndex.BreastUpperL].Step(
                new Vector2(-breastForce.X * (latL * 1.25f), breastForce.Y * 0.50f) * bStr,
                stiffness * 1.18f, damping * 1.12f);
            Bones[BoneIndex.BreastUpperR].Step(
                new Vector2( breastForce.X * (latR * 1.25f), breastForce.Y * 0.50f) * bStr,
                stiffness * 1.18f, damping * 1.12f);

            // ── Step 2: BreastCenter — main mass, chains toward BreastUpper ───
            // Chain-pull vectors: offset from this bone's position toward its parent.
            // These keep each sub-bone "attached" to the one above it in the chain.
            var chainPullVecUpperL = Bones[BoneIndex.BreastUpperL].Position - Bones[BoneIndex.BreastL].Position;
            var chainPullVecUpperR = Bones[BoneIndex.BreastUpperR].Position - Bones[BoneIndex.BreastR].Position;
            Bones[BoneIndex.BreastL].Step(
                new Vector2(-breastForce.X * latL, breastForce.Y) * bStr + chainPullVecUpperL * BreastChainPull,
                stiffness * 0.88f, damping);
            Bones[BoneIndex.BreastR].Step(
                new Vector2( breastForce.X * latR, breastForce.Y) * bStr + chainPullVecUpperR * BreastChainPull,
                stiffness * 0.88f, damping);

            // ── Step 3: BreastLower — softest, chains toward BreastCenter ─────
            // Y 1.80× + chain toward Center → creates the HDT top-stay/bottom-jiggle cascade:
            //   Upper stays → Center lags → Lower swings the most.
            var chainPullVecCenterL = Bones[BoneIndex.BreastL].Position - Bones[BoneIndex.BreastLowerL].Position;
            var chainPullVecCenterR = Bones[BoneIndex.BreastR].Position - Bones[BoneIndex.BreastLowerR].Position;
            Bones[BoneIndex.BreastLowerL].Step(
                new Vector2(-breastForce.X * (latL * 0.58f), breastForce.Y * 1.80f) * bStr + chainPullVecCenterL * BreastChainPull,
                stiffness * 0.68f, damping * 0.76f);
            Bones[BoneIndex.BreastLowerR].Step(
                new Vector2( breastForce.X * (latR * 0.58f), breastForce.Y * 1.80f) * bStr + chainPullVecCenterR * BreastChainPull,
                stiffness * 0.68f, damping * 0.76f);

            // ── Secondary-offset limb bones (feminine) ────────────────────────
            // IMPORTANT: Butt/Thigh/Belly are secondary-offset bones.
            // They add soft-tissue jiggle / step-recoil ON TOP of the animation pose.
            // They do NOT replace or drive the animated limb position — the animation
            // system owns the limb position; these bones only contribute a subtle offset.
            // See BoneIndex.IsLimbSecondaryOffsetBone() for the canonical list.

            // Butt: strong Y, gentle mirrored X  (pants-covered; anchored at hip/rear)
            Bones[BoneIndex.ButtL].Step(
                new Vector2(-centerForce.X * 0.32f, centerForce.Y * 1.35f) * buStr,
                stiffness, damping);
            Bones[BoneIndex.ButtR].Step(
                new Vector2( centerForce.X * 0.32f, centerForce.Y * 1.35f) * buStr,
                stiffness, damping);

            // Belly: moderate Y, tiny X  (shirt-covered; secondary offset, not primary)
            Bones[BoneIndex.BellyCenter].Step(
                new Vector2(centerForce.X * 0.20f, centerForce.Y * 0.90f) * beStr,
                stiffness * 1.10f, damping * 1.10f);

            // Thighs: step-rhythm Y + mirrored X  (pants-covered; animation drives main
            // thigh position — physics adds flesh-jiggle + hit-recoil secondary offset)
            Bones[BoneIndex.ThighL].Step(
                new Vector2(-centerForce.X * 0.42f, centerForce.Y * 0.82f) * thStr,
                stiffness * 1.05f, damping);
            Bones[BoneIndex.ThighR].Step(
                new Vector2( centerForce.X * 0.42f, centerForce.Y * 0.82f) * thStr,
                stiffness * 1.05f, damping);
        }
        else if (profile == BodyProfileType.Masculine)
        {
            // Groin, butt, thighs → lowerBodyMult.  Belly → breastMult (shirt covers belly for males too).
            var grStr = cfg.MaleGroinStrength  * lowerBodyMult;
            var buStr = cfg.MaleButtStrength   * lowerBodyMult;
            var beStr = cfg.MaleBellyStrength  * breastMult;
            var thStr = cfg.MaleThighStrength  * lowerBodyMult;

            // Groin: X-dominant slinky (side-to-side oscillation; secondary offset —
            // animation drives the main lower-body position)
            Bones[BoneIndex.Groin].Step(
                new Vector2(centerForce.X * 1.20f, centerForce.Y * 0.35f) * grStr,
                stiffness * 0.85f, damping * 0.90f);

            // Butt: strong Y (secondary offset anchored at pelvis/hip rear)
            Bones[BoneIndex.MButtL].Step(
                new Vector2(-centerForce.X * 0.35f, centerForce.Y * 1.20f) * buStr,
                stiffness, damping);
            Bones[BoneIndex.MButtR].Step(
                new Vector2( centerForce.X * 0.35f, centerForce.Y * 1.20f) * buStr,
                stiffness, damping);

            // Belly (secondary offset)
            Bones[BoneIndex.BellyCenter].Step(
                new Vector2(centerForce.X * 0.20f, centerForce.Y * 0.80f) * beStr,
                stiffness * 1.10f, damping * 1.10f);

            // Thighs: secondary flesh-jiggle offset — animation drives main leg position
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
    ///
    /// IMPORTANT: this method only modifies <see cref="BoneState.Velocity"/> — it does
    /// NOT advance <see cref="BoneState.Position"/>.  Calling <see cref="Step"/> with
    /// zero spring parameters was incorrect because it also ran the Euler position-advance,
    /// causing bones to double-step in the same tick (once from SimulateBody's StepBoneGroup
    /// call and once from this call).  The fix distributes velocity kicks directly via
    /// <see cref="BoneState.ApplyImpulse"/> using the same anatomical axis scaling as Step.
    /// </summary>
    public void ApplyImpulse(BodyProfileType profile, Vector2 impulse, ModConfig cfg)
    {
        if (profile == BodyProfileType.Feminine)
        {
            var bStr  = cfg.FemaleBreastStrength;
            var buStr = cfg.FemaleButtStrength;
            var beStr = cfg.FemaleBellyStrength;
            var thStr = cfg.FemaleThighStrength;

            // Per-facing lateral scale isn't available here (no facing param) so use
            // symmetric front-view defaults (latL=latR=0.60) — good enough for impulses.
            const float Lat = 0.60f;

            Bones[BoneIndex.BreastUpperL].ApplyImpulse(new Vector2(-impulse.X * (Lat * 1.25f), impulse.Y * 0.50f) * bStr);
            Bones[BoneIndex.BreastUpperR].ApplyImpulse(new Vector2( impulse.X * (Lat * 1.25f), impulse.Y * 0.50f) * bStr);
            Bones[BoneIndex.BreastL].ApplyImpulse(new Vector2(-impulse.X * Lat, impulse.Y) * bStr);
            Bones[BoneIndex.BreastR].ApplyImpulse(new Vector2( impulse.X * Lat, impulse.Y) * bStr);
            Bones[BoneIndex.BreastLowerL].ApplyImpulse(new Vector2(-impulse.X * (Lat * 0.58f), impulse.Y * 1.80f) * bStr);
            Bones[BoneIndex.BreastLowerR].ApplyImpulse(new Vector2( impulse.X * (Lat * 0.58f), impulse.Y * 1.80f) * bStr);

            Bones[BoneIndex.ButtL].ApplyImpulse(new Vector2(-impulse.X * 0.32f, impulse.Y * 1.35f) * buStr);
            Bones[BoneIndex.ButtR].ApplyImpulse(new Vector2( impulse.X * 0.32f, impulse.Y * 1.35f) * buStr);
            Bones[BoneIndex.BellyCenter].ApplyImpulse(new Vector2(impulse.X * 0.20f, impulse.Y * 0.90f) * beStr);
            Bones[BoneIndex.ThighL].ApplyImpulse(new Vector2(-impulse.X * 0.42f, impulse.Y * 0.82f) * thStr);
            Bones[BoneIndex.ThighR].ApplyImpulse(new Vector2( impulse.X * 0.42f, impulse.Y * 0.82f) * thStr);
        }
        else if (profile == BodyProfileType.Masculine)
        {
            var grStr = cfg.MaleGroinStrength;
            var buStr = cfg.MaleButtStrength;
            var beStr = cfg.MaleBellyStrength;
            var thStr = cfg.MaleThighStrength;

            Bones[BoneIndex.Groin].ApplyImpulse(new Vector2(impulse.X * 1.20f, impulse.Y * 0.35f) * grStr);
            Bones[BoneIndex.MButtL].ApplyImpulse(new Vector2(-impulse.X * 0.35f, impulse.Y * 1.20f) * buStr);
            Bones[BoneIndex.MButtR].ApplyImpulse(new Vector2( impulse.X * 0.35f, impulse.Y * 1.20f) * buStr);
            Bones[BoneIndex.BellyCenter].ApplyImpulse(new Vector2(impulse.X * 0.20f, impulse.Y * 0.80f) * beStr);
            Bones[BoneIndex.ThighL].ApplyImpulse(new Vector2(-impulse.X * 0.40f, impulse.Y * 0.80f) * thStr);
            Bones[BoneIndex.ThighR].ApplyImpulse(new Vector2( impulse.X * 0.40f, impulse.Y * 0.80f) * thStr);
        }
        else // Androgynous
        {
            var gentleForce = impulse * 0.35f * 0.65f;
            for (int i = 0; i < BoneIndex.BoneCount; i++)
                Bones[i].ApplyImpulse(gentleForce);
        }
    }

    /// <summary>
    /// Compute the blended visual displacement for this group.
    /// Returns a Vector2 suitable for use as a screen-pixel offset after multiplying
    /// by PhysicsVisualScale.
    ///
    /// <paramref name="facing"/>: 0=Up, 1=Right, 2=Down(default), 3=Left.
    /// Per-bone visibility weights from <see cref="BodyAnchorTable"/> suppress bones
    /// that are not visible at the current facing direction (e.g. breasts when
    /// facing away from camera, butt when facing toward camera).
    /// </summary>
    public Vector2 ComputeVisualDisplacement(BodyProfileType profile, int facing = 2)
    {
        if (profile == BodyProfileType.Feminine)
        {
            // Multi-bone breast chain blend (HDT cascade):
            //   Upper → Centre → Lower progressively lags.
            // Each bone weighted by its facing-specific visibility so that, for
            // example, breast bounce is suppressed when looking at the character's back.
            var wBuL  = BodyAnchorTable.Get(BoneIndex.BreastUpperL, facing);
            var wBuR  = BodyAnchorTable.Get(BoneIndex.BreastUpperR, facing);
            var wBcL  = BodyAnchorTable.Get(BoneIndex.BreastL,      facing);
            var wBcR  = BodyAnchorTable.Get(BoneIndex.BreastR,      facing);
            var wBlL  = BodyAnchorTable.Get(BoneIndex.BreastLowerL, facing);
            var wBlR  = BodyAnchorTable.Get(BoneIndex.BreastLowerR, facing);
            var wButtL = BodyAnchorTable.Get(BoneIndex.ButtL,       facing);
            var wButtR = BodyAnchorTable.Get(BoneIndex.ButtR,       facing);
            var wBelly = BodyAnchorTable.Get(BoneIndex.BellyCenter, facing);
            var wThL   = BodyAnchorTable.Get(BoneIndex.ThighL,      facing);
            var wThR   = BodyAnchorTable.Get(BoneIndex.ThighR,      facing);

            // Each breast sub-bone contribution: Upper 10%, Centre 35%, Lower 25%.
            // Visibility weight scales the whole contribution so hidden bones don't drive the offset.
            var upperContrib = (Bones[BoneIndex.BreastUpperL].Position * wBuL
                              + Bones[BoneIndex.BreastUpperR].Position * wBuR) * 0.5f * 0.10f;
            var centreContrib = (Bones[BoneIndex.BreastL].Position * wBcL
                               + Bones[BoneIndex.BreastR].Position * wBcR) * 0.5f * 0.35f;
            var lowerContrib  = (Bones[BoneIndex.BreastLowerL].Position * wBlL
                               + Bones[BoneIndex.BreastLowerR].Position * wBlR) * 0.5f * 0.25f;
            var breastBlend = upperContrib + centreContrib + lowerContrib;  // up to 0.70 total

            var butt  = (Bones[BoneIndex.ButtL].Position  * wButtL
                       + Bones[BoneIndex.ButtR].Position  * wButtR) * 0.5f;
            var belly = Bones[BoneIndex.BellyCenter].Position * wBelly;
            var thigh = (Bones[BoneIndex.ThighL].Position * wThL
                       + Bones[BoneIndex.ThighR].Position * wThR) * 0.5f;

            return breastBlend + butt * 0.18f + belly * 0.08f + thigh * 0.04f;
        }
        else if (profile == BodyProfileType.Masculine)
        {
            // Groin shares the BoneIndex.BreastL slot (= 3), whose BodyAnchorTable weights
            // already encode "front of character = high visibility, back = low".  Apply that
            // weight so Groin jiggle is suppressed when facing away from the camera, matching
            // the same rule applied to every other bone in this path.
            var wGroin = BodyAnchorTable.Get(BoneIndex.Groin,       facing);
            var wButtL = BodyAnchorTable.Get(BoneIndex.MButtL,      facing);
            var wButtR = BodyAnchorTable.Get(BoneIndex.MButtR,      facing);
            var wBelly = BodyAnchorTable.Get(BoneIndex.BellyCenter,  facing);
            var wThL   = BodyAnchorTable.Get(BoneIndex.ThighL,      facing);
            var wThR   = BodyAnchorTable.Get(BoneIndex.ThighR,      facing);

            var groin = Bones[BoneIndex.Groin].Position * wGroin;
            var butt  = (Bones[BoneIndex.MButtL].Position * wButtL + Bones[BoneIndex.MButtR].Position * wButtR) * 0.5f;
            var belly = Bones[BoneIndex.BellyCenter].Position * wBelly;
            var thigh = (Bones[BoneIndex.ThighL].Position * wThL   + Bones[BoneIndex.ThighR].Position * wThR) * 0.5f;

            return groin * 0.35f + butt * 0.35f + belly * 0.20f + thigh * 0.10f;
        }
        else
        {
            // Androgynous: visibility-weighted average of all bones.
            var sum = Vector2.Zero;
            var totalW = 0f;
            for (int i = 0; i < BoneIndex.BoneCount; i++)
            {
                var w = BodyAnchorTable.Get(i, facing);
                sum    += Bones[i].Position * w;
                totalW += w;
            }

            return totalW > 0f ? sum / totalW : Vector2.Zero;
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

        // Each subsequent segment: receives attenuated force + spring toward parent.
        // Per-spec chain rules:
        //   • stiffness compounds softer toward tip  (root stiffer, tip most elastic)
        //   • damping compounds slightly higher toward tip  (tip stability, less buzz)
        //   • force attenuates 70% per step so tips don't get full-body energy
        const float Attenuation    = 0.70f;
        const float ParentInfluence = 0.25f;

        var force = rootExternalForce;
        var s     = stiffness;
        var d     = damping;
        for (int i = 1; i < segmentCount; i++)
        {
            force *= Attenuation;
            s     *= 0.88f;  // progressively softer: seg1≈0.88k, seg2≈0.77k, seg3≈0.68k …
            d     *= 1.04f;  // progressively more damped (slight tip stability, per spec)

            // Chain constraint: spring pull toward parent's position
            var toParent  = segments[i - 1].Position - segments[i].Position;
            var chainForce = toParent * ParentInfluence;

            segments[i].Step(force + chainForce, s, d);
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

            // Wing tip is the most elastic — slightly softer spring.
            // Stiffness decreases toward tip (elastic, floppy wing tip).
            // Damping increases slightly toward tip (per-spec stability: less tip buzz).
            var s = stiffness * (1f - i * 0.06f);  // root=s, tip=s*0.82
            var d = damping  * (1f + i * 0.03f);   // root=d, tip=d*1.09 (slightly more settled)

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
        const float Attenuation    = 0.80f;
        const float ChainInfluence = 0.40f;

        segments[0].Step(baseForce, stiffness, damping);

        // Per-spec: root stiffest → tip progressively softer; damping increases toward tip.
        var force = baseForce;
        var s     = stiffness;
        var d     = damping;
        for (int i = 1; i < segmentCount; i++)
        {
            force *= Attenuation;
            s     *= 0.88f;  // progressively softer toward tip
            d     *= 1.04f;  // slightly more damped toward tip (stability)
            var toParent = segments[i - 1].Position - segments[i].Position;
            segments[i].Step(force + toParent * ChainInfluence, s, d);
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
            // Tail tip softens progressively; damping increases toward tip per spec
            // (prevents tip buzz while keeping the heavier root snappy).
            var s = stiffness * (1f - i * 0.08f);         // root=s, i=1:0.92s, i=2:0.84s …
            var d = damping   * (1f + i * 0.04f);         // root=d, i=1:1.04d, i=2:1.08d …
            segments[i].Step(force + toParent * ChainInfluence, Math.Max(0.02f, s), d);
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
    /// Number of bounces the particle has performed off the ground plane.
    /// Multi-bounce is allowed until <see cref="MaxBounces"/> is reached.
    /// </summary>
    public int BounceCount;

    /// <summary>Maximum number of ground bounces before the particle slides to rest.</summary>
    public const int MaxBounces = 3;

    /// <summary>
    /// Set to <c>true</c> after the particle has already bounced once.
    /// Kept for backward-compat; use <see cref="BounceCount"/> for new logic.
    /// </summary>
    public bool HasBounced;

    /// <summary>
    /// World-Y position of the virtual ground plane for this particle.
    /// Set to spawn-Y + a per-kind drop height so particles arc and land
    /// realistically rather than falling off-screen forever.
    /// </summary>
    public float GroundY;

    /// <summary>
    /// When <c>true</c>, the particle has reached the ground, lost most energy,
    /// and now only responds to walk-scatter impulses (skips gravity integration).
    /// </summary>
    public bool IsResting;
}
