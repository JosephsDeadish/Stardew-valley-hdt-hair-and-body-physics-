using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewHdtPhysics;

public sealed class ModEntry : Mod
{
    private readonly Dictionary<long, Vector2> lastPositions = new();
    private readonly Dictionary<long, Vector2> bodyImpulse = new();
    private readonly Dictionary<long, Vector2> hairImpulse = new();
    private readonly List<PhysicsPreset> presets = new();

    private ModConfig config = new();
    private SpriteProfileDetector detector = new(Array.Empty<SpriteProfile>());

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.LoadData(helper);
        this.ApplyPresetIfMatched();
        this.RegisterConfigMenu();

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.lastPositions.Clear();
        this.bodyImpulse.Clear();
        this.hairImpulse.Clear();
    }

    private void LoadData(IModHelper helper)
    {
        var profiles = helper.Data.ReadJsonFile<List<SpriteProfile>>("assets/spriteProfiles.json") ?? new List<SpriteProfile>();
        this.presets.Clear();
        this.presets.AddRange(helper.Data.ReadJsonFile<List<PhysicsPreset>>("assets/presets.json") ?? new List<PhysicsPreset>());
        this.detector = new SpriteProfileDetector(profiles);
    }

    private void ApplyPresetIfMatched()
    {
        var preset = this.presets.FirstOrDefault(p => string.Equals(p.Name, this.config.Preset, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        this.config.FemaleBreastStrength = preset.FemaleBreastStrength;
        this.config.FemaleButtStrength = preset.FemaleButtStrength;
        this.config.FemaleThighStrength = preset.FemaleThighStrength;
        this.config.FemaleBellyStrength = preset.FemaleBellyStrength;
        this.config.MaleButtStrength = preset.MaleButtStrength;
        this.config.MaleGroinStrength = preset.MaleGroinStrength;
        this.config.MaleThighStrength = preset.MaleThighStrength;
        this.config.MaleBellyStrength = preset.MaleBellyStrength;
        this.config.HairStrength = preset.HairStrength;
    }

    private void RegisterConfigMenu()
    {
        var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
        {
            return;
        }

        api.Register(this.ModManifest, () => this.config = new ModConfig(), () => this.Helper.WriteConfig(this.config));

        api.AddSectionTitle(this.ModManifest, () => "System Toggles");
        api.AddBoolOption(this.ModManifest, () => this.config.EnableBodyPhysics, value => this.config.EnableBodyPhysics = value, () => "Enable body physics");
        api.AddBoolOption(this.ModManifest, () => this.config.EnableHairPhysics, value => this.config.EnableHairPhysics = value, () => "Enable hair physics");
        api.AddBoolOption(this.ModManifest, () => this.config.EnableIdleMotion, value => this.config.EnableIdleMotion = value, () => "Enable idle motion");
        api.AddBoolOption(this.ModManifest, () => this.config.EnableRagdollKnockback, value => this.config.EnableRagdollKnockback = value, () => "Enable ragdoll-style knockback");

        api.AddSectionTitle(this.ModManifest, () => "Preset");
        api.AddTextOption(
            this.ModManifest,
            () => this.config.Preset,
            value =>
            {
                this.config.Preset = value;
                this.ApplyPresetIfMatched();
            },
            () => "Preset",
            allowedValues: this.presets.Select(p => p.Name).ToArray());

        api.AddSectionTitle(this.ModManifest, () => "Female Strength");
        api.AddNumberOption(this.ModManifest, () => this.config.FemaleBreastStrength, value => this.config.FemaleBreastStrength = value, () => "Breast", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.FemaleButtStrength, value => this.config.FemaleButtStrength = value, () => "Butt", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.FemaleThighStrength, value => this.config.FemaleThighStrength = value, () => "Thigh", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.FemaleBellyStrength, value => this.config.FemaleBellyStrength = value, () => "Belly", min: 0, max: 2, interval: 0.05f);

        api.AddSectionTitle(this.ModManifest, () => "Male Strength");
        api.AddNumberOption(this.ModManifest, () => this.config.MaleButtStrength, value => this.config.MaleButtStrength = value, () => "Butt", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.MaleGroinStrength, value => this.config.MaleGroinStrength = value, () => "Groin", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.MaleThighStrength, value => this.config.MaleThighStrength = value, () => "Thigh", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.MaleBellyStrength, value => this.config.MaleBellyStrength = value, () => "Belly", min: 0, max: 2, interval: 0.05f);

        api.AddSectionTitle(this.ModManifest, () => "Hair / Ragdoll");
        api.AddNumberOption(this.ModManifest, () => this.config.HairStrength, value => this.config.HairStrength = value, () => "Hair strength", min: 0, max: 2, interval: 0.05f);
        api.AddNumberOption(this.ModManifest, () => this.config.RagdollChanceUnderLowHealth, value => this.config.RagdollChanceUnderLowHealth = value, () => "Ragdoll chance at low health", min: 0, max: 1, interval: 0.05f);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
        {
            return;
        }

        foreach (var character in this.EnumerateCharacters(Game1.currentLocation))
        {
            var key = character.GetHashCode();
            var current = character.Position;
            if (!this.lastPositions.TryGetValue(key, out var last))
            {
                this.lastPositions[key] = current;
                continue;
            }

            var velocity = current - last;
            var profile = this.detector.Resolve(character);
            this.SimulateBody(character, profile, velocity);
            this.SimulateHair(character, velocity);
            this.SimulateIdle(character, velocity);
            this.TryApplyLowHealthRagdoll(character, velocity);

            this.lastPositions[key] = current;
        }
    }

    private IEnumerable<Character> EnumerateCharacters(GameLocation location)
    {
        if (Game1.player is not null)
        {
            yield return Game1.player;
        }

        foreach (var npc in location.characters)
        {
            if (npc.IsMonster || npc.Name.Equals("Horse", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return npc;
        }
    }

    private void SimulateBody(Character character, BodyProfileType profile, Vector2 velocity)
    {
        if (!this.config.EnableBodyPhysics)
        {
            return;
        }

        var key = character.GetHashCode();
        if (!this.bodyImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var baseStrength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength + this.config.FemaleThighStrength + this.config.FemaleBellyStrength) / 4f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength + this.config.MaleThighStrength + this.config.MaleBellyStrength) / 4f,
            _ => 0.35f
        };

        impulse += new Vector2(-velocity.X, -velocity.Y) * (0.03f + (baseStrength * 0.04f));
        impulse *= 0.86f;

        this.bodyImpulse[key] = impulse;
    }

    private void SimulateHair(Character character, Vector2 velocity)
    {
        if (!this.config.EnableHairPhysics)
        {
            return;
        }

        var key = character.GetHashCode();
        if (!this.hairImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var windBoost = Game1.currentLocation.IsOutdoors ? 1f : 0.45f;
        impulse += new Vector2(-velocity.X, -velocity.Y) * (0.02f * this.config.HairStrength * windBoost);
        impulse *= 0.88f;

        this.hairImpulse[key] = impulse;
    }

    private void SimulateIdle(Character character, Vector2 velocity)
    {
        if (!this.config.EnableIdleMotion || velocity.LengthSquared() > 0.0001f)
        {
            return;
        }

        if (character is Farmer farmer && farmer.UsingTool)
        {
            return;
        }

        if (Game1.ticks % 180 != 0)
        {
            return;
        }

        var key = character.GetHashCode();
        var impulse = this.bodyImpulse.TryGetValue(key, out var existing) ? existing : Vector2.Zero;
        var randomWave = new Vector2(
            Game1.random.NextSingle() - 0.5f,
            Game1.random.NextSingle() - 0.5f) * 0.24f;

        this.bodyImpulse[key] = impulse + randomWave;
    }

    private void TryApplyLowHealthRagdoll(Character character, Vector2 velocity)
    {
        if (!this.config.EnableRagdollKnockback || character is not Farmer farmer)
        {
            return;
        }

        if (farmer.health >= 30 || velocity.LengthSquared() < 2f)
        {
            return;
        }

        if (Game1.random.NextDouble() > this.config.RagdollChanceUnderLowHealth)
        {
            return;
        }

        var nudge = Vector2.Normalize(velocity) * 1.5f;
        if (float.IsNaN(nudge.X) || float.IsNaN(nudge.Y))
        {
            return;
        }

        farmer.Position += nudge;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
        {
            return;
        }

        if (!e.Button.IsUseToolButton())
        {
            return;
        }

        var key = Game1.player.GetHashCode();
        this.bodyImpulse[key] = Vector2.Zero;
        this.hairImpulse[key] = Vector2.Zero;
    }
}
