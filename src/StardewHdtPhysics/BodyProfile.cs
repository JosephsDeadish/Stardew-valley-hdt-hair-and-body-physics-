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
/// Weight class for floating Debris objects.
/// Governs how far and fast a debris chunk flies when hit or walked through.
/// </summary>
public enum DebrisWeightClass
{
    Light,   // fiber, twigs, weeds, mixed seeds — scatter easily
    Medium,  // gems, crystals, coal — moderate weight
    Heavy    // stones, ore, geodes — hard to move, don't fly far
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

/// <summary>
/// Data-driven rule loaded from assets/debrisPhysics.json.
/// Maps item name keywords or item IDs to a debris weight class.
/// First matching rule wins.
/// </summary>
public sealed class DebrisWeightRule
{
    /// <summary>Case-insensitive substring of the item's display name.</summary>
    public string ItemNameContains { get; set; } = string.Empty;
    /// <summary>Exact item ID (optional). Matched before ItemNameContains.</summary>
    public string ItemId { get; set; } = string.Empty;
    /// <summary>Light, Medium, or Heavy.</summary>
    public string WeightClass { get; set; } = "Medium";
}

public sealed class SpriteProfile
{
    public string CharacterName { get; set; } = string.Empty;
    public string SpriteTextureContains { get; set; } = string.Empty;
    public BodyProfileType ProfileType { get; set; } = BodyProfileType.Androgynous;
}
