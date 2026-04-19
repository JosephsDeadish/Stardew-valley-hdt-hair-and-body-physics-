using StardewValley;

namespace StardewHdtPhysics;

public sealed class SpriteProfileDetector
{
    private readonly Dictionary<string, SpriteProfile> profilesByName;
    private readonly List<SpriteProfile> textureProfiles;
    private readonly Dictionary<Type, (System.Reflection.PropertyInfo? Property, System.Reflection.FieldInfo? Field)> spriteTextureMembers = new();

    // Config-level manual overrides — highest priority.
    private Dictionary<string, string> configOverrides = new(StringComparer.OrdinalIgnoreCase);

    public SpriteProfileDetector(IEnumerable<SpriteProfile> profiles)
    {
        var profileList = profiles.ToList();
        this.profilesByName = profileList
            .Where(p => !string.IsNullOrWhiteSpace(p.CharacterName))
            .ToDictionary(p => p.CharacterName, p => p, StringComparer.OrdinalIgnoreCase);
        this.textureProfiles = profileList
            .Where(p => !string.IsNullOrWhiteSpace(p.SpriteTextureContains))
            .ToList();
    }

    /// <summary>Sets the manual gender overrides from config (reloaded on save-loaded).</summary>
    public void SetConfigOverrides(Dictionary<string, string> overrides)
    {
        this.configOverrides = overrides ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public BodyProfileType Resolve(Character character)
    {
        // 1. Manual config override — highest priority, lets players correct any detection error.
        if (this.configOverrides.TryGetValue(character.Name, out var overrideValue)
            && TryParseProfileType(overrideValue, out var overrideType))
        {
            return overrideType;
        }

        // 2. Live sprite texture detection — catches runtime sprite replacers.
        var spriteTextureName = this.TryGetSpriteTextureName(character);
        if (!string.IsNullOrWhiteSpace(spriteTextureName))
        {
            var textureMatch = this.textureProfiles.FirstOrDefault(p =>
                spriteTextureName.Contains(p.SpriteTextureContains, StringComparison.OrdinalIgnoreCase));
            if (textureMatch is not null)
            {
                return textureMatch.ProfileType;
            }
        }

        // 3. Data-file profile by character name.
        if (this.profilesByName.TryGetValue(character.Name, out var explicitProfile))
        {
            return explicitProfile.ProfileType;
        }

        // 4. SMAPI/game gender fallback — version-safe for Stardew 1.5.6 (int) and 1.6 (enum).
        if (character is Farmer farmer)
        {
            return farmer.IsMale ? BodyProfileType.Masculine : BodyProfileType.Feminine;
        }

        // Use Convert.ToInt32 so this compiles and runs correctly on both:
        //   Stardew 1.5.6 where NPC.Gender is an int (0 = male, 1 = female)
        //   Stardew 1.6.x where NPC.Gender is a Gender enum (0 = male, 1 = female)
        try
        {
            var genderInt = Convert.ToInt32(character.Gender);
            if (genderInt == 1) return BodyProfileType.Feminine;
            if (genderInt == 0) return BodyProfileType.Masculine;
        }
        catch
        {
            // Fallback: any conversion failure → androgynous
        }

        return BodyProfileType.Androgynous;
    }

    private static bool TryParseProfileType(string value, out BodyProfileType result)
    {
        return Enum.TryParse(value, ignoreCase: true, out result);
    }

    private string TryGetSpriteTextureName(Character character)
    {
        var sprite = character.Sprite;
        if (sprite is null)
        {
            return string.Empty;
        }

        var spriteType = sprite.GetType();
        if (!this.spriteTextureMembers.TryGetValue(spriteType, out var members))
        {
            members = (
                spriteType.GetProperty("TextureName"),
                spriteType.GetField("textureName"));
            this.spriteTextureMembers[spriteType] = members;
        }

        var property = members.Property;
        if (property?.GetValue(sprite) is string textureNameFromProperty && !string.IsNullOrWhiteSpace(textureNameFromProperty))
        {
            return textureNameFromProperty;
        }

        var field = members.Field;
        if (field?.GetValue(sprite) is string textureNameFromField && !string.IsNullOrWhiteSpace(textureNameFromField))
        {
            return textureNameFromField;
        }

        return string.Empty;
    }
}
