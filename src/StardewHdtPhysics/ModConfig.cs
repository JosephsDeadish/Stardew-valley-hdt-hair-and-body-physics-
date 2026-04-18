namespace StardewHdtPhysics;

public sealed class ModConfig
{
    public bool EnableBodyPhysics { get; set; } = true;
    public bool EnableHairPhysics { get; set; } = true;
    public bool EnableRagdollKnockback { get; set; } = true;
    public bool EnableIdleMotion { get; set; } = true;

    public float FemaleBreastStrength { get; set; } = 0.55f;
    public float FemaleButtStrength { get; set; } = 0.5f;
    public float FemaleThighStrength { get; set; } = 0.4f;
    public float FemaleBellyStrength { get; set; } = 0.3f;

    public float MaleButtStrength { get; set; } = 0.45f;
    public float MaleGroinStrength { get; set; } = 0.45f;
    public float MaleThighStrength { get; set; } = 0.35f;
    public float MaleBellyStrength { get; set; } = 0.25f;

    public float HairStrength { get; set; } = 0.55f;
    public float RagdollChanceUnderLowHealth { get; set; } = 0.5f;
    public string Preset { get; set; } = "Default";
}
