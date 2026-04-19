namespace StardewHdtPhysics;

public enum BodyProfileType
{
    Feminine,
    Masculine,
    Androgynous
}

/// <summary>
/// Physics archetype for monsters.  Determines which impulse model is applied:
/// Slime=bouncy jello, Bat=floppy wings, Worm=squishy/stretchy,
/// FlyingBug=wing+leg vibration, Furry=fur ripple, Skeleton=snappy bones, Generic=standard.
/// </summary>
public enum MonsterPhysicsArchetype
{
    Generic,
    Slime,
    Bat,
    Worm,
    FlyingBug,
    Furry,
    Skeleton
}

/// <summary>
/// Data-driven rule loaded from assets/monsterArchetypes.json.
/// The first rule whose NameContains matches the monster's display name (case-insensitive) wins.
/// </summary>
public sealed class MonsterArchetypeRule
{
    public string NameContains { get; set; } = string.Empty;
    public string Archetype { get; set; } = "Generic";
}

public sealed class SpriteProfile
{
    public string CharacterName { get; set; } = string.Empty;
    public string SpriteTextureContains { get; set; } = string.Empty;
    public BodyProfileType ProfileType { get; set; } = BodyProfileType.Androgynous;
}
