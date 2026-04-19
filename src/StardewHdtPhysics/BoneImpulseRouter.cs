using Microsoft.Xna.Framework;

namespace StardewHdtPhysics;

// ── BoneImpulseRouter ─────────────────────────────────────────────────────────
//
// Maps a contact hit (HitZone + force vector) to per-bone impulses for the
// BoneGroup, HairChain, WingPair, TailChain, and FurChain systems.
//
// Design rules:
//  • Collision / hitstop code calls BoneImpulseRouter.Route(...) — it does NOT
//    directly touch individual bones.
//  • Each HitZone defines: primary bones + multipliers, secondary falloff bones,
//    parent/root body bone for chain-through recoil.
//  • Force is distributed (not duplicated) — the total energy applied to all
//    bones adds up to approximately 1.0 × the incoming force magnitude.
//  • Hair / wings / tail get small follow-through impulse even when body is hit,
//    because a connected character feels like one unit.
//
// Zone → bone mapping overview (matches BoneIndex):
//   Torso/Belly → BellyCenter (primary), ThighL/R (small)
//   BreastL      → BreastL/UpperL/LowerL + small belly
//   BreastR      → BreastR/UpperR/LowerR + small belly
//   ButtL/R      → ButtL/R + small thigh
//   ThighL/R     → ThighL/R + small belly + small butt
//   HipL/R       → ThighL/R + ButtL/R (both sides, weighted)
//   Groin        → BellyCenter + ButtL + ButtR
//   Head/Torso → hair chain + small belly
//   Tail         → tail chain
//   WingL/R      → wing pair
//   Armor/Shield → body bones at 20 % (armour absorbs most)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless bone-impulse dispatcher.
/// Receives a <see cref="HitZone"/> and a force vector, then applies scaled
/// impulses to the provided bone/chain objects.  All parameters may be null —
/// the router skips absent systems gracefully.
/// </summary>
public static class BoneImpulseRouter
{
    // ── Tiny follow-through fractions applied to all systems regardless of zone
    private const float GlobalHairFollow  = 0.06f;  // hair always reacts a little
    private const float GlobalTailFollow  = 0.04f;
    private const float GlobalWingFollow  = 0.03f;
    private const float GlobalBodyFollow  = 0.03f;  // root body gets a tiny nudge from any hit

    /// <summary>
    /// Route <paramref name="force"/> to the correct bones given <paramref name="zone"/>.
    /// </summary>
    /// <param name="zone">Which body region was struck.</param>
    /// <param name="force">Raw force vector (magnitude encodes strength, direction encodes hit direction).</param>
    /// <param name="bones">Per-character body-bone group (may be null).</param>
    /// <param name="hair">Hair chain (may be null).</param>
    /// <param name="wings">Wing pair (may be null).</param>
    /// <param name="tail">Tail chain (may be null).</param>
    /// <param name="fur">Fur chain (may be null).</param>
    public static void Route(
        HitZone       zone,
        Vector2       force,
        BoneGroup?    bones,
        HairChain?    hair,
        WingPair?     wings,
        TailChain?    tail,
        FurChain?     fur)
        => Route(zone, force, bones, hair, wings, tail, fur, null);

    /// <summary>
    /// Route <paramref name="force"/> using species-specific follow-through multipliers
    /// from a <see cref="CreaturePhysicsProfile"/>.  Follows the same zone→bone mapping as
    /// the parameterless overload but scales appendage reactions by the profile's
    /// <c>HitHairFollowMult</c>, <c>HitTailFollowMult</c>, <c>HitWingFollowMult</c>, and
    /// <c>HitFurFollowMult</c> values.
    /// </summary>
    /// <param name="speciesProfile">
    /// Optional species profile.  When <c>null</c> the global default constants are used,
    /// making this method equivalent to the original <see cref="Route"/> overload.
    /// </param>
    public static void Route(
        HitZone                zone,
        Vector2                force,
        BoneGroup?             bones,
        HairChain?             hair,
        WingPair?              wings,
        TailChain?             tail,
        FurChain?              fur,
        CreaturePhysicsProfile? speciesProfile)
    {
        if (force.LengthSquared() < 0.0001f) return;

        // Per-species follow-through fractions (fall back to global defaults when no profile)
        var hairFollow = speciesProfile?.HitHairFollowMult ?? GlobalHairFollow;
        var tailFollow = speciesProfile?.HitTailFollowMult ?? GlobalTailFollow;
        var wingFollow = speciesProfile?.HitWingFollowMult ?? GlobalWingFollow;
        var furFollow  = speciesProfile?.HitFurFollowMult  ?? GlobalBodyFollow;

        // Scale body-bone force by species bone-impulse multiplier
        var boneForce = speciesProfile is not null
            ? force * speciesProfile.HitBoneImpulseMult
            : force;

        // Apply zone-specific distribution to body bones
        if (bones is not null)
            RouteBodyBones(zone, boneForce, bones);

        // Apply follow-through fractions to all connected appendage systems
        var hairForce = force * hairFollow;
        var tailForce = force * tailFollow;
        var wingForce = force * wingFollow;

        // Zone-specific overrides for appendage systems
        switch (zone)
        {
            case HitZone.Head:
            case HitZone.Torso:
            case HitZone.Belly:
                // Torso/head hits have stronger hair follow-through
                hairForce = force * Math.Max(hairFollow, 0.30f);
                break;

            case HitZone.Tail:
                // Direct tail hit — full force to tail chain
                tailForce = force * 0.90f;
                hairForce = force * Math.Min(hairFollow, 0.04f);
                break;

            case HitZone.WingL:
                wingForce = force * 0.90f;
                hairForce = force * Math.Min(hairFollow, 0.03f);
                break;

            case HitZone.WingR:
                wingForce = force * 0.90f;
                hairForce = force * Math.Min(hairFollow, 0.03f);
                break;

            case HitZone.EarL:
            case HitZone.EarR:
            case HitZone.Snout:
                hairForce = force * Math.Max(hairFollow, 0.20f);
                break;
        }

        // Apply to appendage chains
        if (hair is not null)
            ApplyHairImpulse(hair, hairForce);

        if (tail is not null)
            ApplyTailImpulse(tail, tailForce);

        if (wings is not null)
            ApplyWingImpulse(wings, zone, wingForce);

        if (fur is not null)
            ApplyFurImpulse(fur, force * furFollow);
    }

    // ── Body bone distribution ────────────────────────────────────────────────

    private static void RouteBodyBones(HitZone zone, Vector2 force, BoneGroup bones)
    {
        switch (zone)
        {
            case HitZone.Torso:
                // Centre of mass hit: belly first, thighs feel it a little
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.80f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.15f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.15f);
                break;

            case HitZone.Belly:
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 1.00f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.20f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.20f);
                ImpulseBone(bones, BoneIndex.ButtL,       force, 0.10f);
                ImpulseBone(bones, BoneIndex.ButtR,       force, 0.10f);
                break;

            case HitZone.Groin:
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.60f);
                ImpulseBone(bones, BoneIndex.ButtL,       force, 0.30f);
                ImpulseBone(bones, BoneIndex.ButtR,       force, 0.30f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.20f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.20f);
                break;

            case HitZone.BreastL:
                // Left breast: primary centre-of-mass, then upper/lower chain
                ImpulseBone(bones, BoneIndex.BreastL,      force, 1.00f);
                ImpulseBone(bones, BoneIndex.BreastUpperL, force, 0.50f);
                ImpulseBone(bones, BoneIndex.BreastLowerL, force, 0.70f);  // lower bounces more
                ImpulseBone(bones, BoneIndex.BellyCenter,  force, 0.15f);  // torso recoil
                ImpulseBone(bones, BoneIndex.BreastR,      force, 0.08f);  // tiny sympathetic
                break;

            case HitZone.BreastR:
                ImpulseBone(bones, BoneIndex.BreastR,      force, 1.00f);
                ImpulseBone(bones, BoneIndex.BreastUpperR, force, 0.50f);
                ImpulseBone(bones, BoneIndex.BreastLowerR, force, 0.70f);
                ImpulseBone(bones, BoneIndex.BellyCenter,  force, 0.15f);
                ImpulseBone(bones, BoneIndex.BreastL,      force, 0.08f);
                break;

            case HitZone.ButtL:
                ImpulseBone(bones, BoneIndex.ButtL,       force, 1.00f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.35f);
                ImpulseBone(bones, BoneIndex.ButtR,       force, 0.10f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.10f);
                break;

            case HitZone.ButtR:
                ImpulseBone(bones, BoneIndex.ButtR,       force, 1.00f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.35f);
                ImpulseBone(bones, BoneIndex.ButtL,       force, 0.10f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.10f);
                break;

            case HitZone.ThighL:
                ImpulseBone(bones, BoneIndex.ThighL,      force, 1.00f);
                ImpulseBone(bones, BoneIndex.ButtL,       force, 0.25f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.15f);
                break;

            case HitZone.ThighR:
                ImpulseBone(bones, BoneIndex.ThighR,      force, 1.00f);
                ImpulseBone(bones, BoneIndex.ButtR,       force, 0.25f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.15f);
                break;

            case HitZone.HipL:
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.70f);
                ImpulseBone(bones, BoneIndex.ButtL,       force, 0.50f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.20f);
                break;

            case HitZone.HipR:
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.70f);
                ImpulseBone(bones, BoneIndex.ButtR,       force, 0.50f);
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.20f);
                break;

            case HitZone.Head:
                // Head hit: mostly body recoil (no head bone yet), hair gets main force
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.30f);
                break;

            case HitZone.Armor:
            case HitZone.Shield:
                // Armour/shield absorbs most force — body bones feel very little
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.20f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.08f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.08f);
                break;

            case HitZone.ArmL:
            case HitZone.ArmR:
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.35f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.10f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.10f);
                break;

            default:
                // Generic fallback: small whole-body nudge
                ImpulseBone(bones, BoneIndex.BellyCenter, force, 0.40f);
                ImpulseBone(bones, BoneIndex.ThighL,      force, 0.10f);
                ImpulseBone(bones, BoneIndex.ThighR,      force, 0.10f);
                break;
        }
    }

    // ── Appendage helpers ─────────────────────────────────────────────────────

    private static void ApplyHairImpulse(HairChain chain, Vector2 force)
    {
        if (force.LengthSquared() < 0.0001f) return;
        // HairChain.ApplyImpulse already distributes root→tip with cascade attenuation
        chain.ApplyImpulse(force);
    }

    private static void ApplyTailImpulse(TailChain chain, Vector2 force)
    {
        if (force.LengthSquared() < 0.0001f) return;
        chain.ApplyImpulse(force);
    }

    private static void ApplyWingImpulse(WingPair pair, HitZone zone, Vector2 force)
    {
        if (force.LengthSquared() < 0.0001f) return;
        // Hit on one wing side affects that wing more; global hits affect both
        switch (zone)
        {
            case HitZone.WingL:
                pair.Left.ApplyImpulse(force * 0.90f);
                pair.Right.ApplyImpulse(force * 0.15f);
                break;
            case HitZone.WingR:
                pair.Right.ApplyImpulse(force * 0.90f);
                pair.Left.ApplyImpulse(force * 0.15f);
                break;
            default:
                pair.Left.ApplyImpulse(force * 0.35f);
                pair.Right.ApplyImpulse(force * 0.35f);
                break;
        }
    }

    private static void ApplyFurImpulse(FurChain chain, Vector2 force)
    {
        if (force.LengthSquared() < 0.0001f) return;
        chain.ApplyImpulse(force);
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private static void ImpulseBone(BoneGroup bones, int idx, Vector2 force, float scale)
    {
        if (idx < 0 || idx >= bones.Bones.Length) return;
        bones.Bones[idx].ApplyImpulse(force * scale);
    }
}
