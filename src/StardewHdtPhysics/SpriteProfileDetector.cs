using StardewValley;

namespace StardewHdtPhysics;

public sealed class SpriteProfileDetector
{
    private readonly Dictionary<string, SpriteProfile> profilesByName;
    private readonly List<SpriteProfile> textureProfiles;

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

    public BodyProfileType Resolve(Character character)
    {
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

        if (this.profilesByName.TryGetValue(character.Name, out var explicitProfile))
        {
            return explicitProfile.ProfileType;
        }

        if (character is Farmer farmer)
        {
            return farmer.IsMale ? BodyProfileType.Masculine : BodyProfileType.Feminine;
        }

        if (character.Gender == 1)
        {
            return BodyProfileType.Feminine;
        }

        if (character.Gender == 0)
        {
            return BodyProfileType.Masculine;
        }

        return BodyProfileType.Androgynous;
    }

    private string TryGetSpriteTextureName(Character character)
    {
        var sprite = character.Sprite;
        if (sprite is null)
        {
            return string.Empty;
        }

        var property = sprite.GetType().GetProperty("TextureName");
        if (property?.GetValue(sprite) is string textureNameFromProperty && !string.IsNullOrWhiteSpace(textureNameFromProperty))
        {
            return textureNameFromProperty;
        }

        var field = sprite.GetType().GetField("textureName");
        if (field?.GetValue(sprite) is string textureNameFromField && !string.IsNullOrWhiteSpace(textureNameFromField))
        {
            return textureNameFromField;
        }

        return string.Empty;
    }
}
