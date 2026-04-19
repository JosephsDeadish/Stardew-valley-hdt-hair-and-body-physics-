namespace StardewHdtPhysics;

public sealed class ModConfig
{
    // ── System toggles ────────────────────────────────────────────────────────
    public bool EnableBodyPhysics { get; set; } = true;
    public bool EnableHairPhysics { get; set; } = true;
    public bool EnableRagdollKnockback { get; set; } = true;
    public bool EnableIdleMotion { get; set; } = true;
    public bool EnableMonsterBodyPhysics { get; set; } = true;
    public bool EnableMonsterRagdoll { get; set; } = true;
    public bool EnableNpcSwordKnockdown { get; set; } = true;
    public bool EnableFarmAnimalPhysics { get; set; } = true;
    public bool EnableEnvironmentalPhysics { get; set; } = true;
    public bool EnableItemCollisionPhysics { get; set; } = true;
    public bool EnableWindDetection { get; set; } = true;
    public bool EnableHitDirectionalImpulse { get; set; } = true;
    public bool EnableClothingPhysicsModifier { get; set; } = true;
    public bool EnableHitstopEffect { get; set; } = true;

    // ── Feminine body strengths ───────────────────────────────────────────────
    public float FemaleBreastStrength { get; set; } = 0.75f;
    public float FemaleButtStrength { get; set; } = 0.5f;
    public float FemaleThighStrength { get; set; } = 0.4f;
    public float FemaleBellyStrength { get; set; } = 0.3f;

    // ── Masculine body strengths ──────────────────────────────────────────────
    public float MaleButtStrength { get; set; } = 0.45f;
    public float MaleGroinStrength { get; set; } = 0.45f;
    public float MaleThighStrength { get; set; } = 0.35f;
    public float MaleBellyStrength { get; set; } = 0.25f;

    // ── HDT Hair physics ──────────────────────────────────────────────────────
    public float HairStrength { get; set; } = 0.55f;
    public float HairWindBoostOutdoors { get; set; } = 1.0f;
    public float HairDampeningIndoors { get; set; } = 0.45f;

    // ── Ragdoll & knockback ───────────────────────────────────────────────────
    public float RagdollChanceUnderLowHealth { get; set; } = 0.5f;
    public float RagdollKnockbackStrength { get; set; } = 1.5f;
    public float NpcSwordKnockdownChance { get; set; } = 0.4f;
    /// <summary>Player HP must be at or below this value for ragdoll to activate (0–100).</summary>
    public float RagdollHealthThreshold { get; set; } = 30f;
    /// <summary>Chance (0–1) each clothing slot (hat, shirt, pants, shoes) flies off on ragdoll.</summary>
    public float RagdollClothingScatterChance { get; set; } = 0.10f;
    /// <summary>Chance (0–1) that one inventory item is dropped during ragdoll.</summary>
    public float RagdollItemDropChance { get; set; } = 0.15f;

    // ── Monster archetype physics ─────────────────────────────────────────────
    public float MonsterArchetypeStrength { get; set; } = 0.55f;

    // ── Farm animal physics ───────────────────────────────────────────────────
    public float FarmAnimalPhysicsStrength { get; set; } = 0.45f;

    // ── Environmental physics ─────────────────────────────────────────────────
    public float EnvironmentalPhysicsStrength { get; set; } = 0.5f;

    // ── Presets ───────────────────────────────────────────────────────────────
    public string Preset { get; set; } = "Default";

    /// <summary>
    /// Manual gender overrides keyed by NPC/farmer display name (case-insensitive).
    /// Accepted values: "Feminine", "Masculine", "Androgynous".
    /// Overrides take priority over all automatic sprite and gender detection.
    /// Edit config.json directly or via the GMCM page instruction text.
    /// Example: { "Krobus": "Feminine", "Sam": "Feminine" }
    /// </summary>
    public Dictionary<string, string> GenderOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
