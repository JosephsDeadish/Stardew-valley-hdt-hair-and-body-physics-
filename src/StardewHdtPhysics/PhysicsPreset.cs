namespace StardewHdtPhysics;

public sealed class PhysicsPreset
{
    public string Name { get; set; } = "Default";
    public float FemaleBreastStrength { get; set; }
    public float FemaleButtStrength { get; set; }
    public float FemaleThighStrength { get; set; }
    public float FemaleBellyStrength { get; set; }
    public float MaleButtStrength { get; set; }
    public float MaleGroinStrength { get; set; }
    public float MaleThighStrength { get; set; }
    public float MaleBellyStrength { get; set; }
    public float HairStrength { get; set; }
    public float RagdollKnockbackStrength { get; set; } = 1.5f;
    public float EnvironmentalPhysicsStrength { get; set; } = 0.5f;
}
