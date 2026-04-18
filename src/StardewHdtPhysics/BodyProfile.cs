namespace StardewHdtPhysics;

public enum BodyProfileType
{
    Feminine,
    Masculine,
    Androgynous
}

public sealed class SpriteProfile
{
    public string CharacterName { get; set; } = string.Empty;
    public string SpriteTextureContains { get; set; } = string.Empty;
    public BodyProfileType ProfileType { get; set; } = BodyProfileType.Androgynous;
    public bool ForceHumanoid { get; set; }
}
