using StardewValley;

namespace StardewHdtPhysics;

public sealed class SpriteProfileDetector
{
    private readonly Dictionary<string, SpriteProfile> profilesByName;
    private readonly List<SpriteProfile> textureProfiles;
    private readonly Dictionary<Type, (System.Reflection.PropertyInfo? Property, System.Reflection.FieldInfo? Field)> spriteTextureMembers = new();

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
