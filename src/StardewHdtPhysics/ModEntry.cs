using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;

namespace StardewHdtPhysics;

public sealed class ModEntry : Mod
{
    // ── Per-character physics state ───────────────────────────────────────────
    private readonly Dictionary<int, Vector2> lastPositions = new();
    private readonly Dictionary<int, Vector2> bodyImpulse = new();
    private readonly Dictionary<int, Vector2> hairImpulse = new();
    private readonly Dictionary<int, Vector2> monsterBodyImpulse = new();
    private readonly Dictionary<int, NpcKnockdownState> npcKnockdown = new();
    private readonly Dictionary<int, NpcKnockdownState> farmAnimalKnockdown = new();
    private readonly List<PhysicsPreset> presets = new();
    private readonly List<MonsterArchetypeRule> monsterArchetypeRules = new();

    // ── Environmental physics state ───────────────────────────────────────────
    private Vector2 grassBendDisplacement = Vector2.Zero;
    private Vector2 grassBendVelocity = Vector2.Zero;

    // ── Wind / weather ────────────────────────────────────────────────────────
    private float currentWindStrength = 0f;
    private float currentRainStrength = 0f;
    private float currentSnowStrength = 0f;

    // ── Hit tracking ──────────────────────────────────────────────────────────
    private int lastPlayerHealth = -1;

    // ── Hitstop ───────────────────────────────────────────────────────────────
    private int hitstopTicksRemaining = 0;

    // ── Optional mod integrations ─────────────────────────────────────────────
    private bool fashionSenseLoaded = false;

    private ModConfig config = new();
    private SpriteProfileDetector detector = new(Array.Empty<SpriteProfile>());

    // ── Entry ─────────────────────────────────────────────────────────────────
    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.LoadData(helper);
        this.ApplyPresetIfMatched();

        this.fashionSenseLoaded = helper.ModRegistry.IsLoaded("Flashshifter.FashionSense");
        if (this.fashionSenseLoaded)
        {
            this.Monitor.Log("Fashion Sense detected — custom hair physics will apply to all FS hairs.", LogLevel.Info);
        }

        this.RegisterConfigMenu();

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.ClearAllState();
        this.config = this.Helper.ReadConfig<ModConfig>();
        this.detector.SetConfigOverrides(this.config.GenderOverrides);
        this.UpdateWindStrength();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.UpdateWindStrength();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
        {
            return;
        }

        // Hitstop: brief physics pause on significant hit for impact feedback
        if (this.hitstopTicksRemaining > 0)
        {
            this.hitstopTicksRemaining--;
            return;
        }

        // Track player health — detect damage taken for directional hit impulse
        if (this.config.EnableHitDirectionalImpulse && this.config.EnableBodyPhysics && Game1.player is not null)
        {
            var hp = Game1.player.health;
            if (this.lastPlayerHealth >= 0 && hp < this.lastPlayerHealth)
            {
                this.ApplyHitImpulse(Game1.player, this.lastPlayerHealth - hp);
            }
            this.lastPlayerHealth = hp;
        }

        if (e.IsMultipleOf(300))
        {
            this.UpdateWindStrength();
        }

        var location = Game1.currentLocation;

        // ── Humanoid characters (farmer + NPCs + pets)
        foreach (var character in this.EnumerateHumanoids(location))
        {
            var key = this.GetCharacterKey(character);
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
            this.TickNpcKnockdown(character);

            this.lastPositions[key] = current;
        }

        // ── Monsters (all archetypes — slime, bat, worm, bug, furry, skeleton, generic)
        if (this.config.EnableMonsterBodyPhysics || this.config.EnableMonsterRagdoll)
        {
            foreach (var monster in this.EnumerateMonsters(location))
            {
                var key = this.GetCharacterKey(monster);
                var current = monster.Position;
                if (!this.lastPositions.TryGetValue(key, out var last))
                {
                    this.lastPositions[key] = current;
                    continue;
                }

                var velocity = current - last;
                var profile = this.detector.Resolve(monster);
                this.SimulateMonsterBody(monster, profile, velocity);
                this.SimulateMonsterRagdoll(monster, velocity);

                this.lastPositions[key] = current;
            }
        }

        // ── Farm animals (chickens, cows, sheep, pigs, ducks, rabbits, etc.)
        if (this.config.EnableFarmAnimalPhysics)
        {
            foreach (var animal in EnumerateFarmAnimals(location))
            {
                var key = this.GetCharacterKey(animal);
                var current = animal.Position;
                if (!this.lastPositions.TryGetValue(key, out var last))
                {
                    this.lastPositions[key] = current;
                    continue;
                }

                var velocity = current - last;
                this.SimulateFarmAnimalBody(animal, velocity);
                this.TickFarmAnimalKnockdown(animal);

                this.lastPositions[key] = current;
            }
        }

        // ── Environmental (every 3rd tick for performance)
        if (this.config.EnableEnvironmentalPhysics && e.IsMultipleOf(3))
        {
            this.SimulateEnvironmental(location);
        }
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

        var playerKey = this.GetCharacterKey(Game1.player);
        this.bodyImpulse[playerKey] = Vector2.Zero;
        this.hairImpulse[playerKey] = Vector2.Zero;

        // NPC sword knockdown — harmless cosmetic knockback
        if (this.config.EnableNpcSwordKnockdown
            && Game1.player.CurrentTool is MeleeWeapon
            && Game1.currentLocation is not null)
        {
            foreach (var character in this.EnumerateHumanoids(Game1.currentLocation))
            {
                if (character is Farmer)
                {
                    continue;
                }

                this.TryApplyNpcSwordKnockdown(character);
            }
        }

        // Farm animal sword/tool collision reaction
        if (this.config.EnableFarmAnimalPhysics && Game1.currentLocation is not null)
        {
            foreach (var animal in EnumerateFarmAnimals(Game1.currentLocation))
            {
                this.TryApplyFarmAnimalCollision(animal);
            }
        }

        // Tool swing disturbs grass based on tool weight/shape
        if (this.config.EnableItemCollisionPhysics)
        {
            this.ApplyToolSwingToEnvironment(Game1.player.CurrentTool, Game1.player);
        }
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    private void ClearAllState()
    {
        this.lastPositions.Clear();
        this.bodyImpulse.Clear();
        this.hairImpulse.Clear();
        this.monsterBodyImpulse.Clear();
        this.npcKnockdown.Clear();
        this.farmAnimalKnockdown.Clear();
        this.grassBendDisplacement = Vector2.Zero;
        this.grassBendVelocity = Vector2.Zero;
        this.hitstopTicksRemaining = 0;
        this.lastPlayerHealth = -1;
    }

    private void LoadData(IModHelper helper)
    {
        var profiles = helper.Data.ReadJsonFile<List<SpriteProfile>>("assets/spriteProfiles.json") ?? new List<SpriteProfile>();
        this.presets.Clear();
        this.presets.AddRange(helper.Data.ReadJsonFile<List<PhysicsPreset>>("assets/presets.json") ?? new List<PhysicsPreset>());
        this.monsterArchetypeRules.Clear();
        this.monsterArchetypeRules.AddRange(
            helper.Data.ReadJsonFile<List<MonsterArchetypeRule>>("assets/monsterArchetypes.json")
            ?? new List<MonsterArchetypeRule>());
        this.detector = new SpriteProfileDetector(profiles);
        this.detector.SetConfigOverrides(this.config.GenderOverrides);
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
        this.config.RagdollKnockbackStrength = preset.RagdollKnockbackStrength;
        this.config.MonsterArchetypeStrength = preset.MonsterArchetypeStrength;
        this.config.FarmAnimalPhysicsStrength = preset.FarmAnimalPhysicsStrength;
        this.config.EnvironmentalPhysicsStrength = preset.EnvironmentalPhysicsStrength;
    }

    // ── Wind detection ────────────────────────────────────────────────────────

    private void UpdateWindStrength()
    {
        if (!this.config.EnableWindDetection)
        {
            this.currentWindStrength = 0f;
            this.currentRainStrength = 0f;
            this.currentSnowStrength = 0f;
            return;
        }

        float wind = 0f;

        try
        {
            var windField = typeof(Game1).GetField("wind",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            if (windField?.GetValue(null) is float w)
            {
                wind = Math.Abs(w);
            }
        }
        catch
        {
            // Reflection unavailable — fall through to weather heuristics
        }

        this.currentRainStrength = Game1.isRaining ? (Game1.isLightning ? 0.85f : 0.5f) : 0f;
        if (Game1.isRaining) wind = Math.Max(wind, this.currentRainStrength * 0.8f);

        this.currentSnowStrength = Game1.isSnowing ? 0.35f : 0f;

        if (wind < 0.1f)
        {
            if (Game1.IsFall) wind = 0.25f;
            else if (Game1.IsWinter) wind = 0.35f;
            else if (Game1.IsSpring) wind = 0.15f;
        }

        this.currentWindStrength = Math.Clamp(wind, 0f, 1.5f);
    }

    private float GetHairWindMultiplier()
    {
        if (Game1.currentLocation is null || !Game1.currentLocation.IsOutdoors)
        {
            return this.config.HairDampeningIndoors;
        }

        return this.config.HairWindBoostOutdoors + (this.currentWindStrength * 0.35f);
    }

    // ── Character enumeration ─────────────────────────────────────────────────

    private IEnumerable<Character> EnumerateHumanoids(GameLocation location)
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

    private IEnumerable<NPC> EnumerateMonsters(GameLocation location)
    {
        foreach (var npc in location.characters)
        {
            if (npc.IsMonster)
            {
                yield return npc;
            }
        }
    }

    /// <summary>
    /// Enumerates farm animals from pasture (Farm) and animal housing (Coop/Barn/AnimalHouse).
    /// Compatible with all vanilla and mod-added animals in those location types.
    /// </summary>
    private static IEnumerable<FarmAnimal> EnumerateFarmAnimals(GameLocation location)
    {
        if (location is Farm farm)
        {
            foreach (var animal in farm.animals.Values)
            {
                yield return animal;
            }
        }
        else if (location is AnimalHouse house)
        {
            foreach (var animal in house.animals.Values)
            {
                yield return animal;
            }
        }
    }

    // ── Body physics ──────────────────────────────────────────────────────────

    private void SimulateBody(Character character, BodyProfileType profile, Vector2 velocity)
    {
        if (!this.config.EnableBodyPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(character);
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

        // Clothing modifier: heavier outfit slightly dampens physics (realistic cloth resistance)
        var clothingMult = (this.config.EnableClothingPhysicsModifier && character is Farmer cfm)
            ? this.GetClothingPhysicsMultiplier(cfm)
            : 1f;

        impulse += new Vector2(-velocity.X, -velocity.Y) * ((0.03f + (baseStrength * 0.04f)) * clothingMult);

        // Extra breast bounce for feminine profile: "very bouncy stretchy breast jiggle galore"
        // Breast physics: independent vertical oscillation with slower decay than overall body
        if (profile == BodyProfileType.Feminine)
        {
            var breastExtra = this.config.FemaleBreastStrength * 0.035f * clothingMult;
            impulse += new Vector2(-velocity.X * 0.4f, -velocity.Y * 0.8f) * breastExtra;
            impulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * breastExtra * 0.3f,
                0f);
        }

        // Swimming: water resistance — stronger movement wave but rapid oscillations are damped
        if (character is Farmer swimmingFarmer && swimmingFarmer.swimming.Value)
        {
            impulse += new Vector2(-velocity.X, -velocity.Y) * (0.05f + baseStrength * 0.06f);
            impulse *= 0.82f;
            this.bodyImpulse[key] = impulse;
            return;
        }

        // Feminine gets slightly slower decay = bouncier, longer settling jiggles
        impulse *= profile == BodyProfileType.Feminine ? 0.84f : 0.86f;

        this.bodyImpulse[key] = impulse;
    }

    // ── Monster body physics ──────────────────────────────────────────────────

    /// <summary>
    /// Archetype-specific physics for ALL monsters:
    ///   Slime   = bouncy jello (high impulse, slow decay 0.92, random wobble)
    ///   Bat     = floppy wings (lateral flutter, snap-back 0.80)
    ///   Worm    = squishy stretch (Y-axis dominant, 0.84 decay)
    ///   FlyingBug = wing/thorax/leg vibration (rapid micro-oscillation, 0.78)
    ///   Furry   = fur ripple (gentle wave, slow decay 0.90)
    ///   Skeleton = bone clatter (high impulse, very fast decay 0.72)
    ///   Generic = standard physics (0.86)
    /// Female monster mods (beast girls, slime girls, etc.) get humanoid body overlay on top.
    /// Supports all vanilla monsters and modded creatures/Creatures and Cuties/Pokemon mods.
    /// </summary>
    private void SimulateMonsterBody(NPC monster, BodyProfileType profile, Vector2 velocity)
    {
        if (!this.config.EnableMonsterBodyPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(monster);
        if (!this.monsterBodyImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var strength = this.config.MonsterArchetypeStrength;
        var archetype = this.DetectMonsterArchetype(monster);

        float decay;
        switch (archetype)
        {
            case MonsterPhysicsArchetype.Slime:
                // Bouncy jello: high impulse, very slow decay, random wobble in all directions
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.06f * strength);
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.018f,
                    (Game1.random.NextSingle() - 0.5f) * 0.018f) * strength;
                decay = 0.92f;
                break;

            case MonsterPhysicsArchetype.Bat:
                // Floppy wings: fast lateral flutter, quick snap-back
                impulse += new Vector2(-velocity.X * 0.08f, velocity.Y * 0.025f) * strength;
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.012f, 0f) * strength;
                decay = 0.80f;
                break;

            case MonsterPhysicsArchetype.Worm:
                // Squishy stretch: Y-axis dominant (compression/extension like a worm)
                impulse += new Vector2(-velocity.X * 0.025f, -velocity.Y * 0.07f) * strength;
                decay = 0.84f;
                break;

            case MonsterPhysicsArchetype.FlyingBug:
                // Wing/thorax/leg vibration: light rapid micro-oscillation
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.04f * strength);
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.015f,
                    (Game1.random.NextSingle() - 0.5f) * 0.01f) * strength;
                decay = 0.78f;
                break;

            case MonsterPhysicsArchetype.Furry:
                // Fur ripple: gentle surface wave, slow natural decay
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.03f * strength);
                decay = 0.90f;
                break;

            case MonsterPhysicsArchetype.Skeleton:
                // Bone clatter: sharp snappy physics, very fast decay
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.07f * strength);
                decay = 0.72f;
                break;

            default: // Generic
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.04f * strength);
                decay = 0.86f;
                break;
        }

        // Overlay feminine body physics for female monster sprite mods
        if (profile == BodyProfileType.Feminine)
        {
            var femStrength = (this.config.FemaleBreastStrength + this.config.FemaleButtStrength
                + this.config.FemaleThighStrength + this.config.FemaleBellyStrength) / 4f;
            impulse += new Vector2(-velocity.X, -velocity.Y) * (0.03f + femStrength * 0.04f);
        }

        impulse *= decay;
        this.monsterBodyImpulse[key] = impulse;
    }

    private MonsterPhysicsArchetype DetectMonsterArchetype(NPC monster)
    {
        var name = monster.Name ?? string.Empty;
        foreach (var rule in this.monsterArchetypeRules)
        {
            if (!string.IsNullOrEmpty(rule.NameContains)
                && name.Contains(rule.NameContains, StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<MonsterPhysicsArchetype>(rule.Archetype, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return MonsterPhysicsArchetype.Generic;
    }

    // ── Hit directional impulse ───────────────────────────────────────────────

    /// <summary>
    /// Applies a directional body and hair impulse when the player takes damage.
    /// Identifies the nearest attacker within ~6 tiles and creates an impulse FROM the attacker
    /// TOWARD the player, modeling body parts flying away from the impact source then bouncing back:
    ///   - Skeleton smacks from the left → boobs/butt fly right then spring back
    ///   - Slime hits the belly → belly ripples outward from contact point
    ///   - Explosion → radial push from closest corner of blast radius
    ///   - Trap/floor hazard → direction inferred from last movement
    /// Damage fraction of max HP determines impulse magnitude (heavy hit = more jiggle).
    /// </summary>
    private void ApplyHitImpulse(Farmer player, int damage)
    {
        if (!this.config.EnableBodyPhysics)
        {
            return;
        }

        var playerKey = this.GetCharacterKey(player);
        var profile = this.detector.Resolve(player);
        var baseStrength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength
                + this.config.FemaleThighStrength + this.config.FemaleBellyStrength) / 4f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength
                + this.config.MaleThighStrength + this.config.MaleBellyStrength) / 4f,
            _ => 0.35f
        };

        // Find nearest monster as likely attacker (within ~6 tiles = 192 pixels)
        Vector2 attackDir = Vector2.Zero;
        float closestDist = float.MaxValue;
        if (Game1.currentLocation is not null)
        {
            foreach (var npc in this.EnumerateMonsters(Game1.currentLocation))
            {
                var dist = Vector2.Distance(npc.Position, player.Position);
                if (dist < closestDist && dist < 192f)
                {
                    closestDist = dist;
                    // FROM monster TOWARD player — direction body parts are thrown by the hit
                    attackDir = player.Position - npc.Position;
                }
            }
        }

        // Fall back to last-frame velocity (traps, explosions, floor hazards)
        if (attackDir.LengthSquared() < 0.001f)
        {
            if (this.lastPositions.TryGetValue(playerKey, out var lastPos))
            {
                attackDir = player.Position - lastPos;
            }
        }

        if (attackDir.LengthSquared() < 0.001f)
        {
            return;
        }

        attackDir = Vector2.Normalize(attackDir);
        if (float.IsNaN(attackDir.X) || float.IsNaN(attackDir.Y))
        {
            return;
        }

        // Scale to damage fraction of max HP so heavy hits cause more jiggle than chip damage
        var damageFactor = Math.Clamp((float)damage / Math.Max(1, player.maxHealth) * 8f, 0.1f, 1.5f);
        var hitImpulse = attackDir * (baseStrength * damageFactor * 0.55f);

        var existing = this.bodyImpulse.TryGetValue(playerKey, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[playerKey] = existing + hitImpulse;

        // Hair also reacts — whips toward impact direction then settles
        if (this.config.EnableHairPhysics)
        {
            var hairExisting = this.hairImpulse.TryGetValue(playerKey, out var hi) ? hi : Vector2.Zero;
            this.hairImpulse[playerKey] = hairExisting + hitImpulse * (this.config.HairStrength * 0.7f);
        }

        // Hitstop: brief physics freeze for impact feedback (~50 ms at 60 ticks/s)
        if (this.config.EnableHitstopEffect && damageFactor > 0.3f)
        {
            this.hitstopTicksRemaining = Math.Clamp((int)(damageFactor * 4f), 1, 6);
        }
    }

    // ── Clothing physics helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns a multiplier (0.88–1.0) based on how many clothing slots the farmer has filled.
    /// More clothing = slightly dampened physics (cloth adds mass and restricts jiggle).
    /// Hat, shirt, pants, and shoes each contribute a small reduction.
    /// </summary>
    private float GetClothingPhysicsMultiplier(Farmer farmer)
    {
        int clothingCount = 0;
        if (farmer.hat.Value is not null) clothingCount++;
        if (farmer.shirtItem.Value is not null) clothingCount++;
        if (farmer.pantsItem.Value is not null) clothingCount++;
        if (farmer.boots.Value is not null) clothingCount++;
        return 1f - (clothingCount * 0.03f);
    }

    /// <summary>
    /// Each clothing slot (hat, shirt, pants, shoes) has a configured chance to fly off during
    /// ragdoll. Removed items become pickable debris scattered near the farmer.
    /// Shirt and pants scatter close (cloth physics), boots may roll further (heavier).
    /// </summary>
    private void TryScatterClothing(Farmer farmer)
    {
        if (!this.config.EnableClothingPhysicsModifier || Game1.currentLocation is null)
        {
            return;
        }

        var chance = this.config.RagdollClothingScatterChance;

        if (farmer.hat.Value is not null && Game1.random.NextDouble() < chance)
        {
            var offset = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 80f,
                (Game1.random.NextSingle() - 0.5f) * 80f);
            Game1.createItemDebris(farmer.hat.Value, farmer.Position + offset, farmer.FacingDirection, Game1.currentLocation);
            farmer.hat.Value = null;
        }

        if (farmer.shirtItem.Value is not null && Game1.random.NextDouble() < chance)
        {
            var offset = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 60f,
                (Game1.random.NextSingle() - 0.5f) * 60f);
            Game1.createItemDebris(farmer.shirtItem.Value, farmer.Position + offset, farmer.FacingDirection, Game1.currentLocation);
            farmer.shirtItem.Value = null;
        }

        if (farmer.pantsItem.Value is not null && Game1.random.NextDouble() < chance)
        {
            var offset = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 60f,
                (Game1.random.NextSingle() - 0.5f) * 60f);
            Game1.createItemDebris(farmer.pantsItem.Value, farmer.Position + offset, farmer.FacingDirection, Game1.currentLocation);
            farmer.pantsItem.Value = null;
        }

        if (farmer.boots.Value is not null && Game1.random.NextDouble() < chance)
        {
            // Boots bounce/roll a bit further (heavier, harder sole)
            var offset = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 96f,
                (Game1.random.NextSingle() - 0.5f) * 96f);
            Game1.createItemDebris(farmer.boots.Value, farmer.Position + offset, farmer.FacingDirection, Game1.currentLocation);
            farmer.boots.Value = null;
        }
    }

    /// <summary>
    /// Configurable chance to knock one random item from inventory during ragdoll.
    /// Item is scattered nearby as pickable debris. Simulates items jolted loose by impact.
    /// </summary>
    private void TryDropInventoryItem(Farmer farmer)
    {
        if (Game1.currentLocation is null)
        {
            return;
        }

        if (Game1.random.NextDouble() >= this.config.RagdollItemDropChance)
        {
            return;
        }

        // Pick a random non-null, non-active-tool inventory item
        var candidates = farmer.Items
            .Where(item => item is not null && item != farmer.CurrentItem)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var dropped = candidates[Game1.random.Next(candidates.Count)];
        farmer.removeItemFromInventory(dropped);

        var offset = new Vector2(
            (Game1.random.NextSingle() - 0.5f) * 80f,
            (Game1.random.NextSingle() - 0.5f) * 80f);
        Game1.createItemDebris(dropped, farmer.Position + offset, farmer.FacingDirection, Game1.currentLocation);
    }

    /// <summary>
    /// Body jiggle for farm animals. Heavy animals (cow, goat, sheep, pig, ostrich) get lower
    /// impulse and slower decay. Light animals (chicken, duck, rabbit) are bouncier.
    /// Compatible with all vanilla animals and any mod-added animals in Farm/AnimalHouse.
    /// </summary>
    private void SimulateFarmAnimalBody(FarmAnimal animal, Vector2 velocity)
    {
        if (velocity.LengthSquared() < 0.01f)
        {
            return;
        }

        var key = this.GetCharacterKey(animal);
        if (!this.bodyImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var typeName = animal.type.Value ?? string.Empty;
        var strength = this.config.FarmAnimalPhysicsStrength;

        var isHeavy = typeName.Contains("Cow", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Goat", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Sheep", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Pig", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Bull", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Ostrich", StringComparison.OrdinalIgnoreCase);

        var impulseScale = isHeavy ? 0.04f : 0.07f;
        var decay = isHeavy ? 0.88f : 0.83f;

        impulse += new Vector2(-velocity.X, -velocity.Y) * (impulseScale * strength);
        impulse *= decay;
        this.bodyImpulse[key] = impulse;
    }

    /// <summary>
    /// Cosmetic collision impulse on farm animals when player uses a tool/sword nearby.
    /// No health loss, no upset animals — purely visual bounce/startle reaction.
    /// </summary>
    private void TryApplyFarmAnimalCollision(FarmAnimal animal)
    {
        if (Game1.player is null)
        {
            return;
        }

        var dist = Vector2.Distance(animal.Position, Game1.player.Position);
        if (dist > 80f)
        {
            return;
        }

        if (Game1.random.NextDouble() > 0.65)
        {
            return;
        }

        var dir = animal.Position - Game1.player.Position;
        if (dir.LengthSquared() < 0.001f)
        {
            dir = new Vector2(0f, 1f);
        }
        else
        {
            dir = Vector2.Normalize(dir);
        }

        var key = this.GetCharacterKey(animal);
        this.farmAnimalKnockdown[key] = new NpcKnockdownState
        {
            Impulse = dir * (this.config.RagdollKnockbackStrength * 0.45f),
            TicksRemaining = 10
        };

        var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = bodyEntry + dir * 0.35f;
    }

    private void TickFarmAnimalKnockdown(FarmAnimal animal)
    {
        var key = this.GetCharacterKey(animal);
        if (!this.farmAnimalKnockdown.TryGetValue(key, out var state))
        {
            return;
        }

        if (state.TicksRemaining <= 0)
        {
            this.farmAnimalKnockdown.Remove(key);
            return;
        }

        animal.Position += state.Impulse;
        state.Impulse *= 0.80f;
        state.TicksRemaining--;
    }

    // ── Hair physics ──────────────────────────────────────────────────────────
    // Applied to ALL characters regardless of hair type.
    // Works with vanilla hairs, mod-added hairs, and Fashion Sense custom hairs.

    private void SimulateHair(Character character, Vector2 velocity)
    {
        if (!this.config.EnableHairPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(character);
        if (!this.hairImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var windMult = this.GetHairWindMultiplier();
        var isOutdoors = Game1.currentLocation?.IsOutdoors == true;

        // Swimming: hair fans out and floats in water (buoyant gentle spread, slow oscillation)
        if (character is Farmer swimmingFarmer && swimmingFarmer.swimming.Value)
        {
            impulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.015f,
                -(Game1.random.NextSingle() * 0.012f)) * this.config.HairStrength;
            impulse *= 0.93f;
            this.hairImpulse[key] = impulse;
            return;
        }

        // Rain: heavier hair — downward droop, dampened lateral flow
        if (this.currentRainStrength > 0f && isOutdoors)
        {
            impulse += new Vector2(0f, this.currentRainStrength * 0.012f) * this.config.HairStrength;
            windMult *= Math.Max(0.3f, 1f - this.currentRainStrength * 0.4f);
        }

        // Snow: light upward flutter (snowflakes catching in hair)
        if (this.currentSnowStrength > 0f && isOutdoors)
        {
            var flutter = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.007f,
                -(Game1.random.NextSingle() * 0.005f)) * this.config.HairStrength * this.currentSnowStrength;
            impulse += flutter;
        }

        // Ambient wind drift when standing still outdoors
        if (velocity.LengthSquared() < 0.0001f && isOutdoors)
        {
            impulse += new Vector2(this.currentWindStrength * 0.008f, 0f) * this.config.HairStrength;
        }

        // Movement-based flow
        impulse += new Vector2(-velocity.X, -velocity.Y) * (0.02f * this.config.HairStrength * windMult);
        impulse *= 0.88f;

        this.hairImpulse[key] = impulse;
    }

    // ── Idle motion ───────────────────────────────────────────────────────────

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

        if (Game1.ticks % 180 != (this.GetCharacterKey(character) % 180))
        {
            return;
        }

        var key = this.GetCharacterKey(character);
        var impulse = this.bodyImpulse.TryGetValue(key, out var existing) ? existing : Vector2.Zero;
        var randomWave = new Vector2(
            Game1.random.NextSingle() - 0.5f,
            Game1.random.NextSingle() - 0.5f) * 0.24f;

        this.bodyImpulse[key] = impulse + randomWave;
    }

    // ── Ragdoll ───────────────────────────────────────────────────────────────

    private void TryApplyLowHealthRagdoll(Character character, Vector2 velocity)
    {
        if (!this.config.EnableRagdollKnockback || character is not Farmer farmer)
        {
            return;
        }

        if (farmer.health > (int)this.config.RagdollHealthThreshold || velocity.LengthSquared() < 2f)
        {
            return;
        }

        if (Game1.random.NextDouble() > this.config.RagdollChanceUnderLowHealth)
        {
            return;
        }

        if (velocity.LengthSquared() <= 0.0001f)
        {
            return;
        }

        var nudge = Vector2.Normalize(velocity) * this.config.RagdollKnockbackStrength;
        if (float.IsNaN(nudge.X) || float.IsNaN(nudge.Y))
        {
            return;
        }

        farmer.Position += nudge;

        // Scatter clothing and possibly drop an item from inventory
        this.TryScatterClothing(farmer);
        this.TryDropInventoryItem(farmer);

        // Ragdolling body crashes through grass — flatten it outward
        if (this.config.EnableEnvironmentalPhysics && Game1.currentLocation?.IsOutdoors == true)
        {
            this.grassBendVelocity += nudge * 0.04f;
        }
    }

    /// <summary>
    /// Monster ragdoll: extra positional nudge when struck and moving fast.
    /// Supports all modded creatures — any NPC marked IsMonster qualifies.
    /// Ragdolled monsters crashing through grass flatten and push it aside.
    /// </summary>
    private void SimulateMonsterRagdoll(NPC monster, Vector2 velocity)
    {
        if (!this.config.EnableMonsterRagdoll)
        {
            return;
        }

        if (velocity.LengthSquared() < 1f)
        {
            return;
        }

        if (Game1.random.NextDouble() > 0.35)
        {
            return;
        }

        var nudge = Vector2.Normalize(velocity) * (this.config.RagdollKnockbackStrength * 0.6f);
        if (float.IsNaN(nudge.X) || float.IsNaN(nudge.Y))
        {
            return;
        }

        monster.Position += nudge;

        // Skeleton disintegration: bones scatter in multiple directions then slowly reassemble
        // (decay of 0.72 means the scatter impulse self-corrects within ~15 ticks)
        if (this.DetectMonsterArchetype(monster) == MonsterPhysicsArchetype.Skeleton)
        {
            var scatterKey = this.GetCharacterKey(monster);
            var sImpulse = this.monsterBodyImpulse.TryGetValue(scatterKey, out var si) ? si : Vector2.Zero;
            for (int i = 0; i < 4; i++)
            {
                var boneDir = new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 2f,
                    (Game1.random.NextSingle() - 0.5f) * 2f);
                sImpulse += boneDir * (this.config.RagdollKnockbackStrength * 0.3f);
            }
            this.monsterBodyImpulse[scatterKey] = sImpulse;
        }

        // Monster ragdolling into grass flattens it
        if (this.config.EnableEnvironmentalPhysics && Game1.currentLocation?.IsOutdoors == true)
        {
            this.grassBendVelocity += nudge * 0.035f;
        }
    }

    // ── NPC sword knockdown ───────────────────────────────────────────────────

    private sealed class NpcKnockdownState
    {
        public Vector2 Impulse;
        public int TicksRemaining;
    }

    private void TryApplyNpcSwordKnockdown(Character character)
    {
        if (Game1.player is null)
        {
            return;
        }

        var dist = Vector2.Distance(character.Position, Game1.player.Position);
        if (dist > 96f)
        {
            return;
        }

        if (Game1.random.NextDouble() > this.config.NpcSwordKnockdownChance)
        {
            return;
        }

        var dir = character.Position - Game1.player.Position;
        if (dir.LengthSquared() < 0.001f)
        {
            dir = new Vector2(0, 1);
        }
        else
        {
            dir = Vector2.Normalize(dir);
        }

        var key = this.GetCharacterKey(character);
        this.npcKnockdown[key] = new NpcKnockdownState
        {
            Impulse = dir * (this.config.RagdollKnockbackStrength * 0.8f),
            TicksRemaining = 12
        };

        var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = bodyEntry + dir * 0.5f;
    }

    private void TickNpcKnockdown(Character character)
    {
        if (!this.config.EnableNpcSwordKnockdown || character is Farmer)
        {
            return;
        }

        var key = this.GetCharacterKey(character);
        if (!this.npcKnockdown.TryGetValue(key, out var state))
        {
            return;
        }

        if (state.TicksRemaining <= 0)
        {
            this.npcKnockdown.Remove(key);
            return;
        }

        character.Position += state.Impulse;
        state.Impulse *= 0.82f;
        state.TicksRemaining--;
    }

    // ── Environmental physics ─────────────────────────────────────────────────

    /// <summary>
    /// Spring-force simulation for grass bend and environmental physics.
    ///  - Grass bends against player's direction of travel (body collision)
    ///  - Wind causes slow oscillating lateral drift
    ///  - Ragdolled characters/monsters crash through and flatten grass (fed via grassBendVelocity)
    ///  - Tool swings disturb grass based on tool weight/shape (ApplyToolSwingToEnvironment)
    ///  - Rock rolls differently from sticks: pickaxe=heavy concentrated impact, scythe=wide sweep
    /// </summary>
    private void SimulateEnvironmental(GameLocation location)
    {
        if (Game1.player is null)
        {
            return;
        }

        var strength = this.config.EnvironmentalPhysicsStrength;

        var windDrift = this.config.EnableWindDetection && location.IsOutdoors
            ? new Vector2(this.currentWindStrength * 0.04f * strength, 0f)
            : Vector2.Zero;

        if (this.lastPositions.TryGetValue(this.GetCharacterKey(Game1.player), out var lastPos))
        {
            var playerVelocity = Game1.player.Position - lastPos;
            if (playerVelocity.LengthSquared() > 0.25f)
            {
                this.grassBendVelocity += -playerVelocity * (0.012f * strength);
            }
        }

        this.grassBendVelocity += windDrift;
        this.grassBendVelocity -= this.grassBendDisplacement * (0.06f * strength);
        this.grassBendVelocity *= 0.85f;
        this.grassBendDisplacement += this.grassBendVelocity;

        if (this.grassBendDisplacement.LengthSquared() > 9f)
        {
            this.grassBendDisplacement = Vector2.Normalize(this.grassBendDisplacement) * 3f;
        }
    }

    /// <summary>
    /// Tool swings disturb the environment (grass, debris, rocks) based on tool weight and shape.
    ///   Pickaxe = heavy concentrated smash (rocks fly short distance — heavy but round so some roll)
    ///   Axe = heavy lateral chop (logs barely move — heavy and not round)
    ///   MeleeWeapon = medium lateral knock (sticks tumble lightly)
    ///   Hoe = light forward push
    /// This models how rock vs stick collide differently: rock is heavier so less grass disturbance
    /// per hit but rolls further once moving; stick causes more visible grass sweep as it tumbles.
    /// </summary>
    private void ApplyToolSwingToEnvironment(Tool? tool, Farmer player)
    {
        if (!this.config.EnableItemCollisionPhysics || !this.config.EnableEnvironmentalPhysics)
        {
            return;
        }

        if (tool is null || Game1.currentLocation is null)
        {
            return;
        }

        var strength = this.config.EnvironmentalPhysicsStrength;

        var impulseScale = tool switch
        {
            Pickaxe     => 0.10f,
            Axe         => 0.08f,
            MeleeWeapon mw when mw.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase) => 0.09f,
            MeleeWeapon => 0.06f,
            Hoe         => 0.04f,
            _           => 0.03f
        };

        var swingDir = player.FacingDirection switch
        {
            0 => new Vector2(0f, -1f),
            1 => new Vector2(1f, 0f),
            2 => new Vector2(0f, 1f),
            3 => new Vector2(-1f, 0f),
            _ => Vector2.Zero
        };

        this.grassBendVelocity += swingDir * (impulseScale * strength);
    }

    // ── GMCM registration ─────────────────────────────────────────────────────

    private void RegisterConfigMenu()
    {
        var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
        {
            return;
        }

        api.Register(
            this.ModManifest,
            reset: () =>
            {
                this.config = new ModConfig();
                this.detector.SetConfigOverrides(this.config.GenderOverrides);
                this.UpdateWindStrength();
            },
            save: () =>
            {
                this.Helper.WriteConfig(this.config);
                this.detector.SetConfigOverrides(this.config.GenderOverrides);
                this.UpdateWindStrength();
            });

        // ── System toggles ────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "System Toggles");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableBodyPhysics, v => this.config.EnableBodyPhysics = v,
            () => "Enable body physics",
            () => "Jiggle simulation for all body parts on farmers and humanoid NPCs.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableHairPhysics, v => this.config.EnableHairPhysics = v,
            () => "Enable HDT hair physics",
            () => "Bouncy/flowing hair motion reacting to movement, wind, rain, and snow.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableIdleMotion, v => this.config.EnableIdleMotion = v,
            () => "Enable idle motion",
            () => "Subtle body sway when standing still.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableRagdollKnockback, v => this.config.EnableRagdollKnockback = v,
            () => "Enable ragdoll knockback",
            () => "Extra physics knockback at ≤30 HP based on configured chance.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableMonsterBodyPhysics, v => this.config.EnableMonsterBodyPhysics = v,
            () => "Enable monster body physics",
            () => "Per-archetype physics for ALL monsters: slime=jello, bat=floppy wings, worm=stretchy, bug=vibration, furry=fur, skeleton=bone clatter.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableMonsterRagdoll, v => this.config.EnableMonsterRagdoll = v,
            () => "Enable monster ragdoll",
            () => "Ragdoll knockback for all monsters when struck. Supports all modded creatures.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableNpcSwordKnockdown, v => this.config.EnableNpcSwordKnockdown = v,
            () => "Enable NPC/pet sword knockdown",
            () => "Sword swings near NPCs and pets cause harmless cosmetic knockback. No damage, no anger.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableFarmAnimalPhysics, v => this.config.EnableFarmAnimalPhysics = v,
            () => "Enable farm animal physics",
            () => "Body jiggle and tool collision reactions for chickens, cows, sheep, pigs, ducks, rabbits, and all mod-added farm animals.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableEnvironmentalPhysics, v => this.config.EnableEnvironmentalPhysics = v,
            () => "Enable environmental physics",
            () => "Grass bends when walked through, flattened by ragdolled bodies, and disturbed by tool swings.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableItemCollisionPhysics, v => this.config.EnableItemCollisionPhysics = v,
            () => "Enable item collision physics",
            () => "Tool swings disturb grass/debris based on tool weight: heavy pickaxe=rock-roll impact, scythe=wide sweep, sword=lateral knock.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWindDetection, v => { this.config.EnableWindDetection = v; this.UpdateWindStrength(); },
            () => "Enable wind detection",
            () => "Boosts hair and grass physics on windy/rainy/snowy days. Reads game weather, season, and wind mods.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableHitDirectionalImpulse, v => this.config.EnableHitDirectionalImpulse = v,
            () => "Enable hit directional impulse",
            () => "Body parts and hair fly in the direction of the hit when the player takes damage. " +
                  "Skeleton smacks → boobs/butt bounce away from the hand. Slime hit → belly ripple at impact point. Explosion → radial body push.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableClothingPhysicsModifier, v => this.config.EnableClothingPhysicsModifier = v,
            () => "Enable clothing physics modifier",
            () => "Worn clothing slightly dampens body physics (cloth resistance). Also enables ragdoll clothing scatter (hat/shirt/pants/shoes can fly off).");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableHitstopEffect, v => this.config.EnableHitstopEffect = v,
            () => "Enable hitstop effect",
            () => "Brief physics freeze (~3–6 ticks) on significant hits for impact feedback. Scales with damage dealt.");

        // ── Quick presets ─────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Quick Presets");
        api.AddParagraph(this.ModManifest, () => "Presets set all physics strengths at once. Options: Soft, Default, High.");
        api.AddTextOption(
            this.ModManifest,
            () => this.config.Preset,
            value => { this.config.Preset = value; this.ApplyPresetIfMatched(); },
            () => "Preset",
            () => "Instantly applies a full set of physics strength values.",
            allowedValues: this.presets.Count > 0 ? this.presets.Select(p => p.Name).ToArray() : new[] { "Default" });

        // ── Feminine body strengths ───────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Feminine Physics Strength");
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleBreastStrength, v => this.config.FemaleBreastStrength = v,
            () => "Breast", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleButtStrength, v => this.config.FemaleButtStrength = v,
            () => "Butt", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleThighStrength, v => this.config.FemaleThighStrength = v,
            () => "Thigh", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleBellyStrength, v => this.config.FemaleBellyStrength = v,
            () => "Belly", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Masculine body strengths ──────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Masculine Physics Strength");
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleButtStrength, v => this.config.MaleButtStrength = v,
            () => "Butt", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleGroinStrength, v => this.config.MaleGroinStrength = v,
            () => "Groin", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleThighStrength, v => this.config.MaleThighStrength = v,
            () => "Thigh", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleBellyStrength, v => this.config.MaleBellyStrength = v,
            () => "Belly", () => "0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── HDT Hair physics ──────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "HDT Hair Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Hair physics apply to ALL hair types: vanilla, mod-added, and Fashion Sense custom hairs. " +
            "Rain = heavy droopy hair. Snow = light flutter. Wind = flow and trail. " +
            (this.fashionSenseLoaded ? "Fashion Sense detected — FS hairs are included." : "Fashion Sense not detected (optional)."));
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairStrength, v => this.config.HairStrength = v,
            () => "Hair strength",
            () => "Overall flow strength. 0 = still, 2 = very bouncy.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairWindBoostOutdoors, v => this.config.HairWindBoostOutdoors = v,
            () => "Outdoor wind boost",
            () => "Multiplier outdoors. Higher = longer trailing effect when running.", 0.1f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairDampeningIndoors, v => this.config.HairDampeningIndoors = v,
            () => "Indoor dampening",
            () => "Multiplier indoors. Lower = calmer hair inside buildings.", 0f, 1f, 0.05f);

        // ── Ragdoll & knockback ───────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Ragdoll & Knockback");
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollChanceUnderLowHealth, v => this.config.RagdollChanceUnderLowHealth = v,
            () => "Ragdoll chance at low health",
            () => "Probability of ragdoll knockback at ≤30 HP. 0 = never, 1 = always.", 0f, 1f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollKnockbackStrength, v => this.config.RagdollKnockbackStrength = v,
            () => "Knockback strength",
            () => "How far ragdoll pushes characters. 1.5 = default, 4 = very strong.", 0.5f, 4f, 0.1f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.NpcSwordKnockdownChance, v => this.config.NpcSwordKnockdownChance = v,
            () => "NPC/pet knockdown chance",
            () => "Probability NPCs and pets react to nearby sword swings. 0 = never, 1 = always. Cosmetic only.", 0f, 1f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollHealthThreshold, v => this.config.RagdollHealthThreshold = v,
            () => "Ragdoll HP threshold",
            () => "Player must be at or below this HP for ragdoll knockback to activate. Default: 30.", 1f, 100f, 1f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollClothingScatterChance, v => this.config.RagdollClothingScatterChance = v,
            () => "Clothing scatter chance",
            () => "Per-slot chance (0–1) that hat, shirt, pants, or shoes fly off during ragdoll. Walk over to pick back up. 0 = never, 1 = always.", 0f, 1f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollItemDropChance, v => this.config.RagdollItemDropChance = v,
            () => "Inventory item drop chance",
            () => "Chance (0–1) that one inventory item is knocked out during ragdoll. 0 = never, 1 = always.", 0f, 1f, 0.05f);

        // ── Monster archetype physics ─────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Monster Physics");
        api.AddParagraph(this.ModManifest, () =>
            "All monsters get archetype-specific physics automatically. " +
            "Slime/Jelly = bouncy jello. Bat/Ghost = floppy wings. Serpent/Grub/Duggy = squishy stretch. " +
            "Fly/Bug/Moth = wing+leg vibration. Wolf/Bear/Furry = fur ripple. Skeleton/Mummy = bone clatter. " +
            "Female monster mods (beast girls, slime girls) additionally get humanoid body physics overlaid. " +
            "Compatible with Creatures and Cuties, Pokemon mods, and all custom creature mods.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.MonsterArchetypeStrength, v => this.config.MonsterArchetypeStrength = v,
            () => "Monster physics strength",
            () => "Overall intensity for all monster archetype physics. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Farm animals & pets ───────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Farm Animals & Pets");
        api.AddParagraph(this.ModManifest, () =>
            "All farm animals get body jiggle: chickens, ducks, rabbits (bouncy), cows, goats, sheep, pigs, ostriches (heavy/slower). " +
            "Mod-added animals in Coops and Barns are automatically included. " +
            "Pets (cats and dogs) are always included in humanoid NPC physics. " +
            "Sword and tool swings near animals cause a cosmetic startle reaction — no damage ever.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.FarmAnimalPhysicsStrength, v => this.config.FarmAnimalPhysicsStrength = v,
            () => "Farm animal strength",
            () => "Physics intensity for all farm animals. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Environmental physics ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Environmental Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Grass bends as you walk through it and gets flattened when ragdolled bodies crash through it. " +
            "Tool swings disturb environment based on weight and shape: " +
            "pickaxe = heavy smash (rocks fly short but may roll being round), " +
            "scythe = wide light sweep (sticks tumble), sword = lateral knock. " +
            "Wind mods are automatically detected and boost grass physics outdoors.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.EnvironmentalPhysicsStrength, v => this.config.EnvironmentalPhysicsStrength = v,
            () => "Environmental strength",
            () => "Intensity of grass bend, debris wobble, and rock roll physics. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Gender overrides ──────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Gender Overrides");
        api.AddParagraph(this.ModManifest, () =>
            "Edit config.json to add manual gender overrides under \"GenderOverrides\". " +
            "Example: \"Krobus\": \"Feminine\". Values: Feminine, Masculine, Androgynous. " +
            "Overrides take priority over all automatic detection including live sprite texture names.");
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private int GetCharacterKey(Character character)
    {
        return RuntimeHelpers.GetHashCode(character);
    }
}
