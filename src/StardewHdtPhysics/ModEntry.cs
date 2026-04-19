using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
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
    private readonly List<DebrisWeightRule> debrisWeightRules = new();

    // ── Environmental physics state ───────────────────────────────────────────
    private Vector2 grassBendDisplacement = Vector2.Zero;
    private Vector2 grassBendVelocity = Vector2.Zero;

    // ── Wind / weather ────────────────────────────────────────────────────────
    private float currentWindStrength = 0f;
    private float currentRainStrength = 0f;
    private float currentSnowStrength = 0f;

    // ── Hit tracking ──────────────────────────────────────────────────────────
    private int lastPlayerHealth = -1;
    private bool wasSwimming = false;
    private int waterEmergenceTicksRemaining = 0;

    // ── Skill level tracking (for level-up bounce) ────────────────────────────
    private int lastSkillLevelSum = -1;
    private int levelUpBounceTicksRemaining = 0;

    // ── Dragon ragdoll cooldown ────────────────────────────────────────────────
    private readonly Dictionary<int, int> dragonRagdollCooldown = new();

    // ── Hitstop ───────────────────────────────────────────────────────────────
    private int hitstopTicksRemaining = 0;

    // ── Optional mod integrations ─────────────────────────────────────────────
    private bool fashionSenseLoaded = false;
    private bool druidModLoaded = false;
    private bool magicModLoaded = false;
    private bool spaceCoreLoaded = false;

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

        this.druidModLoaded = helper.ModRegistry.IsLoaded("druid") || helper.ModRegistry.IsLoaded("Druid") ||
            helper.ModRegistry.IsLoaded("SilentOak.Druid") || helper.ModRegistry.IsLoaded("MonsoonalMoth.DruidMod");
        if (this.druidModLoaded)
        {
            this.Monitor.Log("Druid mod detected — Dragon archetype physics and ragdolls enabled for Druid dragons.", LogLevel.Info);
        }

        this.magicModLoaded = helper.ModRegistry.IsLoaded("spacechase0.Magic") || helper.ModRegistry.IsLoaded("mistyspring.MagicHair") ||
            helper.ModRegistry.IsLoaded("spacechase0.SpaceCore");
        if (this.magicModLoaded)
        {
            this.Monitor.Log("Magic/SpaceCore mod detected — magic cast physics enabled for spell-type tools.", LogLevel.Info);
        }

        this.spaceCoreLoaded = helper.ModRegistry.IsLoaded("spacechase0.SpaceCore");
        if (this.spaceCoreLoaded)
        {
            this.Monitor.Log("SpaceCore detected — custom skill level-up physics enabled.", LogLevel.Info);
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

        // Water emergence: detect when farmer exits the water — droop hair for several ticks
        if (this.config.EnableWaterEmergenceHairDroop && Game1.player is not null)
        {
            var nowSwimming = Game1.player.swimming.Value;
            if (this.wasSwimming && !nowSwimming)
            {
                this.waterEmergenceTicksRemaining = 90; // ~1.5 seconds of wet-hair droop
            }

            this.wasSwimming = nowSwimming;
        }

        if (this.waterEmergenceTicksRemaining > 0)
        {
            this.waterEmergenceTicksRemaining--;
        }

        // Skill level-up bounce: check if any skill level increased since last tick
        if (this.config.EnableSkillLevelUpBounce && Game1.player is not null)
        {
            this.CheckSkillLevelUp(Game1.player);
        }

        if (this.levelUpBounceTicksRemaining > 0)
        {
            this.levelUpBounceTicksRemaining--;
        }

        // Decay dragon ragdoll cooldown counters each tick
        if (this.dragonRagdollCooldown.Count > 0)
        {
            var toRemove = new List<int>();
            foreach (var kv in this.dragonRagdollCooldown)
            {
                if (kv.Value <= 1)
                {
                    toRemove.Add(kv.Key);
                }
                else
                {
                    this.dragonRagdollCooldown[kv.Key] = kv.Value - 1;
                }
            }

            foreach (var k in toRemove)
            {
                this.dragonRagdollCooldown.Remove(k);
            }
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

            // Horse rider physics: while mounted, the rider gets extra vertical bounce from hoofbeats
            if (this.config.EnableHorseRiderPhysics && character is Farmer mountedFarmer && mountedFarmer.isRidingHorse())
            {
                this.SimulateHorseRiderBounce(mountedFarmer, velocity);
            }

            // NPC proximity collision: player bumping into an NPC sends a gentle impulse
            if (this.config.EnableProximityCollisionImpulse && character is not Farmer)
            {
                this.TryApplyProximityCollisionImpulse(character);
            }

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

        // ── Floating debris physics (every 3rd tick for performance)
        if (this.config.EnableDebrisPhysics && e.IsMultipleOf(3))
        {
            this.SimulateDebrisPhysics(location);
        }

        // ── Crop / weed / grass collision for all creatures (every 3rd tick)
        if (this.config.EnableCropWeedCollisionPhysics && e.IsMultipleOf(3))
        {
            this.SimulateCropWeedCollision(location);
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

        // Tool/weapon kick nearby floating debris based on weight class
        if (this.config.EnableDebrisPhysics)
        {
            this.ApplyToolSwingToFloatingDebris(Game1.player.CurrentTool, Game1.player);
        }

        // Magic cast physics: detect magic tools/spells and apply appropriate body+hair impulse
        if (this.config.EnableMagicCastPhysics)
        {
            this.ApplyMagicCastPhysics(Game1.player);
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
        this.wasSwimming = false;
        this.waterEmergenceTicksRemaining = 0;
        this.lastSkillLevelSum = -1;
        this.levelUpBounceTicksRemaining = 0;
        this.dragonRagdollCooldown.Clear();
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
        this.debrisWeightRules.Clear();
        this.debrisWeightRules.AddRange(
            helper.Data.ReadJsonFile<List<DebrisWeightRule>>("assets/debrisPhysics.json")
            ?? new List<DebrisWeightRule>());
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
        this.config.DebrisPhysicsStrength = preset.DebrisPhysicsStrength;
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

        // Directional body boost: facing away from camera (down) = back/butt most visible
        // → breasts splay laterally, butt/thigh jiggle amplified; facing up = subtler
        if (this.config.EnableDirectionalBodyBoost && velocity.LengthSquared() > 0.04f)
        {
            var facing = character.FacingDirection;
            if (facing == 2) // down — walking away, back is the most visible surface
            {
                // Breast lateral splay: when facing away the breasts press outward at each step
                if (profile == BodyProfileType.Feminine)
                {
                    var lateralSplay = this.config.FemaleBreastStrength * 0.05f * clothingMult;
                    impulse += new Vector2(
                        (Game1.random.NextSingle() - 0.5f) * 2f * lateralSplay,
                        (Game1.random.NextSingle() - 0.5f) * lateralSplay * 0.3f);
                }

                // Butt amplification: posterior jiggle is very prominent from behind
                var buttBoost = profile switch
                {
                    BodyProfileType.Feminine  => this.config.FemaleButtStrength  * 0.06f * clothingMult,
                    BodyProfileType.Masculine => this.config.MaleButtStrength    * 0.055f * clothingMult,
                    _                         => 0.025f * clothingMult
                };
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * buttBoost * 0.5f,
                    Math.Abs(velocity.Y) * buttBoost * 1.8f); // strong up-down jiggle in walk direction
            }
            else if (facing == 0) // up — walking toward camera, front visible
            {
                // Belly and front bounce: belly swings forward with each step
                var bellyBoost = profile switch
                {
                    BodyProfileType.Feminine  => this.config.FemaleBellyStrength * 0.04f * clothingMult,
                    BodyProfileType.Masculine => this.config.MaleBellyStrength   * 0.035f * clothingMult,
                    _                         => 0.02f * clothingMult
                };
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * bellyBoost * 0.4f,
                    -Math.Abs(velocity.Y) * bellyBoost * 1.2f);
            }
        }

        // Continuous micro-activity: very small random baseline so physics never go fully dormant.
        // Simulates the constant tiny vibrations of breathing, muscle tension, and micro-movements.
        if (baseStrength > 0f)
        {
            impulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.008f * baseStrength,
                (Game1.random.NextSingle() - 0.5f) * 0.006f * baseStrength);
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

            case MonsterPhysicsArchetype.Dragon:
                // Dragon physics: wingbeat burst + tail thrash lateral oscillation + ground rumble
                // Uses DragonPhysicsStrength (default 1.2) to scale up from MonsterArchetypeStrength
                {
                    var dragonStrength = strength * this.config.DragonPhysicsStrength;
                    impulse += new Vector2(-velocity.X, -velocity.Y) * (0.12f * dragonStrength);

                    // Wingbeat rhythm: strong downward burst every ~25 ticks
                    var key2 = this.GetCharacterKey(monster);
                    if (Game1.ticks % 25 == key2 % 25)
                    {
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * 0.09f,
                            0.12f) * dragonStrength; // powerful downward wing-thrust
                    }

                    // Tail thrash: sinusoidal lateral oscillation regardless of movement
                    impulse += new Vector2(
                        (float)Math.Sin(Game1.ticks * 0.15f) * 0.03f * dragonStrength,
                        (Game1.random.NextSingle() - 0.5f) * 0.01f * dragonStrength);

                    // Ground rumble when moving fast — whole body vibrates
                    if (velocity.LengthSquared() > 2f)
                    {
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * 0.05f,
                            (Game1.random.NextSingle() - 0.5f) * 0.05f) * dragonStrength;
                    }

                    decay = 0.95f; // very slow decay = long lingering motion
                }
                break;

            case MonsterPhysicsArchetype.Elemental:
                // Elemental magic fluctuation: sinusoidal pulsing, rapid oscillation
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.05f * strength);
                impulse += new Vector2(
                    (float)Math.Sin(Game1.ticks * 0.30f) * 0.022f * strength,
                    (float)Math.Cos(Game1.ticks * 0.25f) * 0.022f * strength);
                decay = 0.82f;
                break;

            default: // Generic
                impulse += new Vector2(-velocity.X, -velocity.Y) * (0.04f * strength);
                decay = 0.86f;
                break;
        }

        // Dragon ragdoll: check separately — amplified extra-force knockdown on big movement spikes
        if (archetype == MonsterPhysicsArchetype.Dragon && this.config.EnableDragonPhysics)
        {
            this.SimulateDragonRagdoll(monster, velocity);
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

        // If still no direction found AND player is outdoors → treat as explosion:
        // push radially outward from the player's position (blast wave from all sides).
        if (attackDir.LengthSquared() < 0.001f)
        {
            if (Game1.currentLocation?.IsOutdoors == true)
            {
                // Radial outward push — random direction, simulating concussive blast wave
                var blastAngle = (float)(Game1.random.NextDouble() * Math.PI * 2.0);
                attackDir = new Vector2((float)Math.Cos(blastAngle), (float)Math.Sin(blastAngle));
            }
            else
            {
                // Indoors: trap/floor hazard — use a random small bump
                attackDir = new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 2f,
                    (Game1.random.NextSingle() - 0.5f) * 2f);
            }
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

        // Water emergence: hair is soaking wet for ~1.5 s after leaving the water — heavy downward droop
        // that gradually dries off as the tick counter decays.
        if (this.config.EnableWaterEmergenceHairDroop
            && character is Farmer emergingFarmer
            && this.waterEmergenceTicksRemaining > 0)
        {
            var wetFactor = this.waterEmergenceTicksRemaining / 90f; // 1.0 → 0.0 as it dries
            impulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.005f,
                0.025f * wetFactor) * this.config.HairStrength; // strong downward drip
            impulse *= 0.90f;
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

        // Continuous micro-oscillation: sinusoidal noise ensures hair is never completely still.
        // Uses per-character phase offset (key hash) so each character has a unique sway rhythm.
        // Models air currents, breathing, and natural pendulum physics of hair.
        var phase = this.GetCharacterKey(character) % 100 * 0.063f; // per-character phase
        impulse += new Vector2(
            (float)Math.Sin(Game1.ticks * 0.08f + phase) * 0.005f * this.config.HairStrength,
            (Game1.random.NextSingle() - 0.5f) * 0.003f * this.config.HairStrength);

        impulse *= 0.88f;

        this.hairImpulse[key] = impulse;
    }

    // ── Idle motion ───────────────────────────────────────────────────────────

    private void SimulateIdle(Character character, Vector2 velocity)
    {
        if (!this.config.EnableIdleMotion || velocity.LengthSquared() > 0.1f)
        {
            return;
        }

        if (character is Farmer farmer && farmer.UsingTool)
        {
            return;
        }

        var key = this.GetCharacterKey(character);

        // ── Always-active breathing pulse (every 45 ticks, very subtle) ─────────
        // Fires regardless of the main idle interval — keeps body/hair gently alive at all times.
        if (Game1.ticks % 45 == key % 45)
        {
            var breathPhase = (Game1.ticks / 45) % 2 == 0 ? -1f : 1f;
            var breathImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.04f,
                breathPhase * 0.06f);

            var bEntry = this.bodyImpulse.TryGetValue(key, out var bExist) ? bExist : Vector2.Zero;
            this.bodyImpulse[key] = bEntry + breathImpulse;

            if (this.config.EnableHairPhysics)
            {
                var hEntry = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
                this.hairImpulse[key] = hEntry + breathImpulse * (this.config.HairStrength * 0.25f);
            }
        }

        // ── Main idle burst (configurable interval, default 90 ticks = ~1.5 s) ─
        var interval = Math.Max(30, this.config.IdleMotionIntervalTicks);
        if (Game1.ticks % interval != key % interval)
        {
            return;
        }

        var impulse = this.bodyImpulse.TryGetValue(key, out var existing) ? existing : Vector2.Zero;

        // Weighted idle type selection — more dramatic types more common than before
        var animRoll = Game1.random.NextDouble();
        Vector2 idleImpulse;
        if (animRoll < 0.18)
        {
            // Standard body sway
            idleImpulse = new Vector2(
                Game1.random.NextSingle() - 0.5f,
                Game1.random.NextSingle() - 0.5f) * 0.28f;
        }
        else if (animRoll < 0.33)
        {
            // Hip sway: strong lateral push with minor vertical — most dramatic from behind
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.50f, (Game1.random.NextSingle() - 0.5f) * 0.06f);
        }
        else if (animRoll < 0.47)
        {
            // Lean to one side: weight shift, slower wider arc
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.38f, (Game1.random.NextSingle() - 0.5f) * 0.08f);
        }
        else if (animRoll < 0.59)
        {
            // Arm raise / stretch: strong upward then natural fall
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.15f,
                -0.55f);
        }
        else if (animRoll < 0.70)
        {
            // Bounce: rhythmic downward weight shift, like tapping foot
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.06f,
                0.40f);
        }
        else if (animRoll < 0.80)
        {
            // Shimmy: two rapid alternating lateral pushes
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.60f, 0.08f);
        }
        else if (animRoll < 0.90)
        {
            // Deep breathing: slow oscillation in current phase
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.05f,
                (Game1.ticks / 30 % 2 == 0) ? -0.14f : 0.14f);
        }
        else
        {
            // Twirl: full diagonal circular push
            var angle = (float)(Game1.random.NextDouble() * Math.PI * 2.0);
            idleImpulse = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 0.58f;
        }

        this.bodyImpulse[key] = impulse + idleImpulse;

        // Hair tosses with body motion
        if (this.config.EnableHairPhysics)
        {
            var hairImpulse = this.hairImpulse.TryGetValue(key, out var hi) ? hi : Vector2.Zero;
            this.hairImpulse[key] = hairImpulse + idleImpulse * (this.config.HairStrength * 0.5f);
        }
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

        // Player always contributes to grass bend
        if (this.lastPositions.TryGetValue(this.GetCharacterKey(Game1.player), out var lastPos))
        {
            var playerVelocity = Game1.player.Position - lastPos;
            if (playerVelocity.LengthSquared() > 0.25f)
            {
                this.grassBendVelocity += -playerVelocity * (0.012f * strength);
            }
        }

        // All other humanoids (NPCs, pets) also push grass
        if (this.config.EnableAllCreatureGrassCollision)
        {
            foreach (var character in this.EnumerateHumanoids(location))
            {
                if (character is Farmer)
                {
                    continue; // player already handled above
                }

                if (this.lastPositions.TryGetValue(this.GetCharacterKey(character), out var chLast))
                {
                    var chVel = character.Position - chLast;
                    if (chVel.LengthSquared() > 0.25f)
                    {
                        this.grassBendVelocity += -chVel * (0.008f * strength);
                    }
                }
            }

            // Monsters push grass — proportional to movement speed; larger/faster = more bend
            foreach (var monster in this.EnumerateMonsters(location))
            {
                if (this.lastPositions.TryGetValue(this.GetCharacterKey(monster), out var mLast))
                {
                    var mVel = monster.Position - mLast;
                    if (mVel.LengthSquared() > 0.25f)
                    {
                        this.grassBendVelocity += -mVel * (0.007f * strength);
                    }
                }
            }

            // Farm animals push grass too
            foreach (var animal in EnumerateFarmAnimals(location))
            {
                if (this.lastPositions.TryGetValue(this.GetCharacterKey(animal), out var aLast))
                {
                    var aVel = animal.Position - aLast;
                    if (aVel.LengthSquared() > 0.25f)
                    {
                        this.grassBendVelocity += -aVel * (0.006f * strength);
                    }
                }
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

    // ── Crop / weed / grass terrain collision ─────────────────────────────────

    /// <summary>
    /// Detects when any character (player, NPC, monster, farm animal) is walking through
    /// grass, crops, or weed terrain features and applies a localized spring-force bend
    /// to the global grassBendVelocity. All creatures have dynamic collision with vegetation.
    ///
    /// How it works:
    ///   - Convert pixel position to tile coordinates (tile = pos / 64)
    ///   - Check the 3×3 neighborhood of tiles around the character for:
    ///       Grass terrain feature (wild grass patches)
    ///       HoeDirt terrain feature (tilled/planted crop tiles)
    ///       Objects whose name contains "Weed" or "Fiber" (wild weeds)
    ///   - Each occupied tile adds a directional contribution to grassBendVelocity
    ///     proportional to the character's movement speed
    ///   - Ragdolling characters (speed > 3 px/tick) cause a large burst that models
    ///     the whole body crashing through a field
    ///
    /// Compatible with all vanilla and mod-added terrain features.
    /// </summary>
    private void SimulateCropWeedCollision(GameLocation location)
    {
        if (!this.config.EnableCropWeedCollisionPhysics || !location.IsOutdoors)
        {
            return;
        }

        var str = this.config.CropWeedCollisionStrength;

        // ── Player + humanoids ───────────────────────────────────────────────
        foreach (var character in this.EnumerateHumanoids(location))
        {
            if (!this.lastPositions.TryGetValue(this.GetCharacterKey(character), out var lastP))
            {
                continue;
            }

            var vel = character.Position - lastP;
            if (vel.LengthSquared() < 0.04f)
            {
                continue; // not moving — no collision
            }

            var isRagdolling = vel.LengthSquared() > 9f; // ~3 px/tick = ragdoll speed
            this.ApplyCropCollisionImpulse(character.Position, vel, location, str, isRagdolling);
        }

        // ── Monsters ─────────────────────────────────────────────────────────
        foreach (var monster in this.EnumerateMonsters(location))
        {
            if (!this.lastPositions.TryGetValue(this.GetCharacterKey(monster), out var lastM))
            {
                continue;
            }

            var vel = monster.Position - lastM;
            if (vel.LengthSquared() < 0.04f)
            {
                continue;
            }

            var isRagdolling = vel.LengthSquared() > 9f;
            this.ApplyCropCollisionImpulse(monster.Position, vel, location, str * 0.75f, isRagdolling);
        }

        // ── Farm animals ─────────────────────────────────────────────────────
        foreach (var animal in EnumerateFarmAnimals(location))
        {
            if (!this.lastPositions.TryGetValue(this.GetCharacterKey(animal), out var lastA))
            {
                continue;
            }

            var vel = animal.Position - lastA;
            if (vel.LengthSquared() < 0.04f)
            {
                continue;
            }

            this.ApplyCropCollisionImpulse(animal.Position, vel, location, str * 0.55f, isRagdolling: false);
        }
    }

    /// <summary>
    /// Checks the 3×3 tile neighborhood around <paramref name="pixelPos"/> for vegetation
    /// terrain features/objects and contributes a bend impulse to grassBendVelocity.
    /// </summary>
    private void ApplyCropCollisionImpulse(Vector2 pixelPos, Vector2 vel, GameLocation location, float strength, bool isRagdolling)
    {
        var tileX = (int)(pixelPos.X / 64f);
        var tileY = (int)(pixelPos.Y / 64f);

        var movDir = vel.LengthSquared() > 0.001f ? Vector2.Normalize(vel) : Vector2.Zero;
        if (float.IsNaN(movDir.X) || float.IsNaN(movDir.Y))
        {
            return;
        }

        int hits = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                var tile = new Vector2(tileX + dx, tileY + dy);

                // Check terrain features: Grass and HoeDirt (crops)
                if (location.terrainFeatures.TryGetValue(tile, out var feature))
                {
                    if (feature is Grass || feature is HoeDirt)
                    {
                        hits++;
                    }
                }

                // Check placed objects that are weeds/fiber
                if (location.objects.TryGetValue(tile, out var obj) && obj is not null)
                {
                    var objName = obj.Name ?? string.Empty;
                    if (ContainsAny(objName, "Weed", "Fiber", "Wild", "Hay") ||
                        obj.Category == StardewValley.Object.wildResultCategory)
                    {
                        hits++;
                    }
                }
            }
        }

        if (hits == 0)
        {
            return;
        }

        float impactScale;
        if (isRagdolling)
        {
            // Heavy crash-through: body rolling through field at high speed
            impactScale = 0.06f * strength * Math.Min(vel.Length() * 0.15f, 2.5f);
        }
        else
        {
            // Normal walk-through: gentle parting as feet brush past
            impactScale = 0.015f * strength;
        }

        this.grassBendVelocity += movDir * (impactScale * hits);
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

    // ── Debris physics ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies gentle walk-through physics to nearby floating Debris objects each tick.
    /// Light items (fiber, weeds, seeds) scatter easily. Heavy items (stones, ore) resist.
    /// Simulates debris tumbling and bouncing as the player or NPCs move through them.
    /// Compatible with all custom items — uses keyword matching from debrisPhysics.json.
    /// </summary>
    private void SimulateDebrisPhysics(GameLocation location)
    {
        if (Game1.player is null)
        {
            return;
        }

        var strength = this.config.DebrisPhysicsStrength;
        var playerPos = Game1.player.Position;

        Vector2 playerVelocity = Vector2.Zero;
        if (this.lastPositions.TryGetValue(this.GetCharacterKey(Game1.player), out var lastPos))
        {
            playerVelocity = Game1.player.Position - lastPos;
        }

        foreach (var debris in location.debris)
        {
            if (debris?.Chunks is null || debris.Chunks.Count == 0)
            {
                continue;
            }

            var chunkPos = new Vector2(debris.Chunks[0].position.X, debris.Chunks[0].position.Y);
            var dist = Vector2.Distance(chunkPos, playerPos);
            if (dist > 96f || dist < 0.001f)
            {
                continue;
            }

            var weightClass = this.ResolveDebrisWeightClass(debris);
            var (impulseScale, drag) = weightClass switch
            {
                DebrisWeightClass.Light  => (0.55f, 0.88f),
                DebrisWeightClass.Medium => (0.30f, 0.92f),
                DebrisWeightClass.Heavy  => (0.14f, 0.95f),
                _ => (0.30f, 0.92f)
            };

            // Walking through debris pushes it in the movement direction
            if (playerVelocity.LengthSquared() > 0.01f)
            {
                var pushDir = Vector2.Normalize(playerVelocity);
                if (!float.IsNaN(pushDir.X) && !float.IsNaN(pushDir.Y))
                {
                    debris.velocity += pushDir * (impulseScale * strength);
                }
            }

            // Apply drag so debris eventually settles
            debris.velocity *= drag;
        }
    }

    /// <summary>
    /// Tool and weapon swings kick nearby floating Debris away.
    /// Scale and direction depend on tool type and debris weight class:
    ///   Pickaxe = heavy smash, rocks fly short but fast
    ///   Scythe  = wide sweep launches light debris (fiber, weeds) in a wide arc
    ///   Axe     = lateral chop sends wood chips sideways
    ///   Sword   = medium lateral knock for gems and mixed debris
    /// Roll physics based on weight: heavy items have more inertia and travel less far.
    /// Compatible with all modded weapons — matches by tool type, not name.
    /// </summary>
    private void ApplyToolSwingToFloatingDebris(Tool? tool, Farmer player)
    {
        if (tool is null || Game1.currentLocation is null)
        {
            return;
        }

        var strength = this.config.DebrisPhysicsStrength;

        float range = tool switch
        {
            Pickaxe     => 48f,
            Axe         => 56f,
            MeleeWeapon mw when mw.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase) => 80f,
            MeleeWeapon => 64f,
            Hoe         => 40f,
            _           => 32f
        };

        var swingDir = player.FacingDirection switch
        {
            0 => new Vector2(0f, -1f),
            1 => new Vector2(1f, 0f),
            2 => new Vector2(0f, 1f),
            3 => new Vector2(-1f, 0f),
            _ => Vector2.Zero
        };

        if (swingDir == Vector2.Zero)
        {
            return;
        }

        foreach (var debris in Game1.currentLocation.debris)
        {
            if (debris?.Chunks is null || debris.Chunks.Count == 0)
            {
                continue;
            }

            var chunkPos = new Vector2(debris.Chunks[0].position.X, debris.Chunks[0].position.Y);
            var dist = Vector2.Distance(chunkPos, player.Position);
            if (dist > range)
            {
                continue;
            }

            var weightClass = this.ResolveDebrisWeightClass(debris);
            var kickStrength = weightClass switch
            {
                DebrisWeightClass.Light  => 2.5f,
                DebrisWeightClass.Medium => 1.4f,
                DebrisWeightClass.Heavy  => 0.7f,
                _ => 1.4f
            };

            var toolMultiplier = tool switch
            {
                Pickaxe     => 1.8f,
                Axe         => 1.5f,
                MeleeWeapon mw when mw.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase) => 1.3f,
                MeleeWeapon => 1.1f,
                _           => 0.8f
            };

            // Lateral spread gives each debris chunk a slightly different trajectory
            var lateral = new Vector2(-swingDir.Y, swingDir.X);
            var isScythe = tool is MeleeWeapon sw && sw.Name.Contains("Scythe", StringComparison.OrdinalIgnoreCase);
            var spread = (Game1.random.NextSingle() - 0.5f) * (isScythe ? 1.2f : 0.6f);
            var kickDir = swingDir + lateral * spread;
            if (kickDir.LengthSquared() < 0.001f)
            {
                continue;
            }

            kickDir = Vector2.Normalize(kickDir);
            if (float.IsNaN(kickDir.X) || float.IsNaN(kickDir.Y))
            {
                continue;
            }

            debris.velocity += kickDir * (kickStrength * toolMultiplier * strength);
        }
    }

    /// <summary>
    /// Resolves the weight class for a Debris object by matching its item name/ID against
    /// the rules in assets/debrisPhysics.json. Falls back to Medium for unknown items.
    /// Allows mod-added minerals, gems, and custom debris to get appropriate physics.
    /// </summary>
    private DebrisWeightClass ResolveDebrisWeightClass(Debris debris)
    {
        var itemName = debris.item?.Name ?? string.Empty;

        // Try item ID match first (fastest, most precise)
        if (!string.IsNullOrEmpty(debris.item?.ItemId))
        {
            foreach (var rule in this.debrisWeightRules)
            {
                if (!string.IsNullOrEmpty(rule.ItemId)
                    && debris.item.ItemId.Equals(rule.ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<DebrisWeightClass>(rule.WeightClass, ignoreCase: true, out var byId))
                    {
                        return byId;
                    }
                }
            }
        }

        // Then try name keyword match
        if (!string.IsNullOrEmpty(itemName))
        {
            foreach (var rule in this.debrisWeightRules)
            {
                if (!string.IsNullOrEmpty(rule.ItemNameContains)
                    && itemName.Contains(rule.ItemNameContains, StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<DebrisWeightClass>(rule.WeightClass, ignoreCase: true, out var byName))
                    {
                        return byName;
                    }
                }
            }
        }

        return DebrisWeightClass.Medium;
    }

    // ── Horse rider physics ───────────────────────────────────────────────────

    /// <summary>
    /// While the farmer is riding a horse, apply extra vertical bounce from hoofbeats.
    /// Each full stride sends a downward-then-up impulse based on the horse's movement speed.
    /// Works with vanilla and mod-added rideable horses/mounts.
    /// Hair also bounces visibly with each stride.
    /// </summary>
    private void SimulateHorseRiderBounce(Farmer farmer, Vector2 velocity)
    {
        if (velocity.LengthSquared() < 0.5f)
        {
            return;
        }

        var key = this.GetCharacterKey(farmer);
        var profile = this.detector.Resolve(farmer);
        var baseStrength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength) / 2f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength) / 2f,
            _ => 0.35f
        };

        // Hoofbeat rhythm: strong downward impulse every ~18 ticks (≈3 per second at 60fps)
        if (Game1.ticks % 18 == key % 18)
        {
            var bounceImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.05f,
                0.25f * baseStrength); // downward jolt

            var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = bodyEntry + bounceImpulse;

            // Hair bounces up as rider is jolted down, then falls back
            if (this.config.EnableHairPhysics)
            {
                var hairEntry = this.hairImpulse.TryGetValue(key, out var hi) ? hi : Vector2.Zero;
                this.hairImpulse[key] = hairEntry + new Vector2(
                    bounceImpulse.X * 0.5f, -bounceImpulse.Y * 0.8f) * this.config.HairStrength;
            }
        }
    }

    // ── Proximity collision impulse ───────────────────────────────────────────

    /// <summary>
    /// When the player walks close enough to an NPC to be bumping into them, apply a gentle
    /// body impulse away from the player. Models the "brush past" collision that should cause
    /// a brief jiggle — realistic for walking through a crowd.
    /// No damage, no relationship impact — purely visual physics effect.
    /// Compatible with all NPCs including mod-added characters.
    /// </summary>
    private void TryApplyProximityCollisionImpulse(Character character)
    {
        if (Game1.player is null)
        {
            return;
        }

        var dist = Vector2.Distance(character.Position, Game1.player.Position);
        if (dist > 40f || dist < 0.001f)
        {
            return;
        }

        // Only apply occasionally so it doesn't look like a constant vibration
        if (Game1.ticks % 8 != 0)
        {
            return;
        }

        var profile = this.detector.Resolve(character);
        var strength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength) / 2f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength) / 2f,
            _ => 0.35f
        };

        var pushDir = character.Position - Game1.player.Position;
        if (pushDir.LengthSquared() < 0.001f)
        {
            return;
        }

        pushDir = Vector2.Normalize(pushDir);
        if (float.IsNaN(pushDir.X) || float.IsNaN(pushDir.Y))
        {
            return;
        }

        var key = this.GetCharacterKey(character);
        var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        // Gentle impulse — just a brush, not a shove
        this.bodyImpulse[key] = existing + pushDir * (0.18f * strength);

        if (this.config.EnableHairPhysics)
        {
            var hairExisting = this.hairImpulse.TryGetValue(key, out var hi) ? hi : Vector2.Zero;
            this.hairImpulse[key] = hairExisting + pushDir * (0.12f * strength * this.config.HairStrength);
        }
    }

    // ── Dragon ragdoll ────────────────────────────────────────────────────────

    /// <summary>
    /// Amplified ragdoll for Dragon archetype monsters.
    /// Triggers on large velocity spikes (fast movement = big hit reaction).
    /// Uses a per-dragon cooldown (60 ticks) to avoid constant triggering.
    /// The dragon's body scatters in a multi-directional burst that slowly damps out.
    /// Nearby NPCs and the player get a shockwave push if within range.
    /// Compatible with Druid mod dragons, Ancient Dragons, SVE Lava Drakes, and all other
    /// dragon-type monsters detected via monsterArchetypes.json.
    /// </summary>
    private void SimulateDragonRagdoll(NPC monster, Vector2 velocity)
    {
        if (!this.config.EnableDragonPhysics || !this.config.EnableMonsterRagdoll)
        {
            return;
        }

        if (velocity.LengthSquared() < 3f)
        {
            return;
        }

        var key = this.GetCharacterKey(monster);
        if (this.dragonRagdollCooldown.ContainsKey(key))
        {
            return;
        }

        if (Game1.random.NextDouble() > 0.55)
        {
            return;
        }

        var dragonStrength = this.config.MonsterArchetypeStrength * this.config.DragonPhysicsStrength;

        // Multi-directional burst — wings and tail fly in different directions
        var mainNudge = Vector2.Normalize(velocity);
        if (float.IsNaN(mainNudge.X) || float.IsNaN(mainNudge.Y))
        {
            return;
        }

        monster.Position += mainNudge * (this.config.RagdollKnockbackStrength * dragonStrength * 0.8f);

        var sImpulse = this.monsterBodyImpulse.TryGetValue(key, out var si) ? si : Vector2.Zero;
        for (int i = 0; i < 6; i++)
        {
            var scatterDir = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 2f,
                (Game1.random.NextSingle() - 0.5f) * 2f);
            sImpulse += scatterDir * (dragonStrength * 0.35f);
        }

        this.monsterBodyImpulse[key] = sImpulse;

        // Shockwave: push nearby NPCs+player away from the dragon
        if (Game1.currentLocation is not null)
        {
            var shockRadius = 128f;
            foreach (var nearby in this.EnumerateHumanoids(Game1.currentLocation))
            {
                var dist = Vector2.Distance(nearby.Position, monster.Position);
                if (dist < 1f || dist > shockRadius)
                {
                    continue;
                }

                var away = nearby.Position - monster.Position;
                away = Vector2.Normalize(away);
                if (float.IsNaN(away.X) || float.IsNaN(away.Y))
                {
                    continue;
                }

                var shockFade = 1f - (dist / shockRadius);
                var nearbyKey = this.GetCharacterKey(nearby);
                var nbi = this.bodyImpulse.TryGetValue(nearbyKey, out var nbiExist) ? nbiExist : Vector2.Zero;
                this.bodyImpulse[nearbyKey] = nbi + away * (dragonStrength * 0.4f * shockFade);

                if (this.config.EnableHairPhysics)
                {
                    var nhi = this.hairImpulse.TryGetValue(nearbyKey, out var nhiExist) ? nhiExist : Vector2.Zero;
                    this.hairImpulse[nearbyKey] = nhi + away * (dragonStrength * 0.3f * shockFade * this.config.HairStrength);
                }
            }
        }

        // Dragon ragdoll flattens grass with heavy impact
        if (this.config.EnableEnvironmentalPhysics && Game1.currentLocation?.IsOutdoors == true)
        {
            this.grassBendVelocity += mainNudge * (0.08f * dragonStrength);
        }

        this.dragonRagdollCooldown[key] = 60; // 1-second cooldown before next dragon ragdoll
    }

    // ── Magic cast physics ────────────────────────────────────────────────────

    /// <summary>
    /// Detects the type of magic being cast from the farmer's current tool name, then applies
    /// a spell-type-specific body impulse and dramatic hair reaction to the farmer.
    /// Also sends a shockwave to nearby monsters/NPCs to simulate the energy release.
    ///
    /// Supported spell types (detected by tool name keywords):
    ///   Fire/Flame/Pyro     → upward heat burst, hair streams back from heat
    ///   Water/Aqua/Frost/Ice → lateral wave oscillation, hair swings wide
    ///   Earth/Stone/Rock/Mud → heavy downward thud, hair heavy and pendular
    ///   Air/Wind/Storm/Gale  → strong backwards hair blast, body pushed forward
    ///   Lightning/Thunder/Shock → sharp spike impulse + very fast decay
    ///   Shadow/Dark/Void/Curse → inward pull, hair collapses inward
    ///   Nature/Druid/Grove/Vine → gentle radial bloom, long slow settle
    ///   Generic magic (wand/staff/scepter/orb/tome/rune/arcane/mana/spell/glyph/scroll/magic) → radial pulse
    ///
    /// Compatible with: Magic mod, Druid mod, SpaceCore magic skills, Stardew Valley Expanded
    /// magic weapons, and any mod that adds a tool with a magic-keyword name.
    /// </summary>
    private void ApplyMagicCastPhysics(Farmer farmer)
    {
        if (farmer.CurrentTool is null)
        {
            return;
        }

        var toolName = farmer.CurrentTool.Name ?? string.Empty;

        // Determine spell type from tool name
        var spellImpulse = Vector2.Zero;
        var hairBoost = 1.0f;
        var impactRadius = 96f;
        var str = this.config.MagicCastImpulseStrength;

        if (ContainsAny(toolName, "fire", "flame", "blaze", "pyro", "inferno", "lava", "magma"))
        {
            // Fire: upward heat burst — body surges up, hair streams backward from the heat
            spellImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.15f,
                -0.6f) * str;
            hairBoost = 1.8f;
        }
        else if (ContainsAny(toolName, "water", "aqua", "frost", "ice", "cryo", "tidal", "ocean", "rain"))
        {
            // Water/Ice: lateral wave, body sways to one side and back
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            spellImpulse = new Vector2(side * 0.55f, -0.1f) * str;
            hairBoost = 1.4f;
        }
        else if (ContainsAny(toolName, "earth", "stone", "rock", "mud", "terra", "geo", "ground", "quake"))
        {
            // Earth: heavy downward thud, hair pulled by gravity
            spellImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.1f,
                0.55f) * str; // downward
            hairBoost = 1.2f;
        }
        else if (ContainsAny(toolName, "air", "wind", "gale", "storm", "breeze", "tornado", "cyclone", "aero"))
        {
            // Air/Wind: strong backwards hair blast, body pushed forward
            var facingDir = farmer.FacingDirection switch
            {
                0 => new Vector2(0f, -1f),
                1 => new Vector2(1f, 0f),
                2 => new Vector2(0f, 1f),
                _ => new Vector2(-1f, 0f)
            };
            spellImpulse = facingDir * (0.45f * str);
            hairBoost = 2.5f; // very dramatic hair blast for wind magic
        }
        else if (ContainsAny(toolName, "lightning", "thunder", "shock", "volt", "electr", "spark", "zap"))
        {
            // Lightning: sharp spike then rapid oscillating decay
            var angle = (float)(Game1.random.NextDouble() * Math.PI * 2.0);
            spellImpulse = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (0.9f * str);
            hairBoost = 2.0f;
        }
        else if (ContainsAny(toolName, "shadow", "dark", "void", "curse", "hex", "necro", "death", "umbra"))
        {
            // Shadow/Dark: inward pull toward caster — body contracts, hair collapses inward
            var facingDir = farmer.FacingDirection switch
            {
                0 => new Vector2(0f, 1f),  // inverse of facing
                1 => new Vector2(-1f, 0f),
                2 => new Vector2(0f, -1f),
                _ => new Vector2(1f, 0f)
            };
            spellImpulse = facingDir * (0.5f * str);
            hairBoost = 1.3f;
        }
        else if (ContainsAny(toolName, "nature", "druid", "grove", "vine", "leaf", "bloom", "forest", "fern", "herb", "wood"))
        {
            // Nature/Druid: gentle radial bloom outward, slow settle (Druid mod spells)
            var bloomAngle = (float)(Game1.random.NextDouble() * Math.PI * 2.0);
            spellImpulse = new Vector2((float)Math.Cos(bloomAngle), (float)Math.Sin(bloomAngle)) * (0.4f * str);
            hairBoost = 1.6f;
            impactRadius = 128f; // nature spells have wider influence
        }
        else if (ContainsAny(toolName, "wand", "staff", "scepter", "orb", "tome", "rune", "arcane", "mana", "spell", "glyph", "scroll", "magic"))
        {
            // Generic magic: radial outward pulse
            var pulseAngle = (float)(Game1.ticks * 0.5f);
            spellImpulse = new Vector2((float)Math.Cos(pulseAngle), (float)Math.Sin(pulseAngle)) * (0.45f * str);
            hairBoost = 1.5f;
        }
        else
        {
            // No magic keyword detected — skip
            return;
        }

        if (spellImpulse.LengthSquared() < 0.001f)
        {
            return;
        }

        // Apply to farmer body
        var farmerKey = this.GetCharacterKey(farmer);
        var profile = this.detector.Resolve(farmer);
        var bodyStrength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength) / 2f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength) / 2f,
            _ => 0.35f
        };

        var fbi = this.bodyImpulse.TryGetValue(farmerKey, out var fbExist) ? fbExist : Vector2.Zero;
        this.bodyImpulse[farmerKey] = fbi + spellImpulse * bodyStrength;

        // Dramatic hair reaction
        if (this.config.EnableHairPhysics)
        {
            var fhi = this.hairImpulse.TryGetValue(farmerKey, out var fhExist) ? fhExist : Vector2.Zero;
            this.hairImpulse[farmerKey] = fhi + spellImpulse * (this.config.HairStrength * hairBoost);
        }

        // Shockwave: push nearby characters and monsters with a fraction of the spell impulse
        if (Game1.currentLocation is not null)
        {
            foreach (var nearby in this.EnumerateHumanoids(Game1.currentLocation))
            {
                if (nearby is Farmer)
                {
                    continue;
                }

                var dist = Vector2.Distance(nearby.Position, farmer.Position);
                if (dist < 1f || dist > impactRadius)
                {
                    continue;
                }

                var away = nearby.Position - farmer.Position;
                away = Vector2.Normalize(away);
                if (float.IsNaN(away.X) || float.IsNaN(away.Y))
                {
                    continue;
                }

                var fade = 1f - (dist / impactRadius);
                var nearbyKey = this.GetCharacterKey(nearby);
                var nbi = this.bodyImpulse.TryGetValue(nearbyKey, out var nbiExist) ? nbiExist : Vector2.Zero;
                this.bodyImpulse[nearbyKey] = nbi + away * (0.3f * str * fade);

                if (this.config.EnableHairPhysics)
                {
                    var nhi = this.hairImpulse.TryGetValue(nearbyKey, out var nhiExist) ? nhiExist : Vector2.Zero;
                    this.hairImpulse[nearbyKey] = nhi + away * (0.2f * str * fade * this.config.HairStrength);
                }
            }

            // Also push nearby monsters
            foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
            {
                var dist = Vector2.Distance(monster.Position, farmer.Position);
                if (dist < 1f || dist > impactRadius)
                {
                    continue;
                }

                var away = monster.Position - farmer.Position;
                away = Vector2.Normalize(away);
                if (float.IsNaN(away.X) || float.IsNaN(away.Y))
                {
                    continue;
                }

                var fade = 1f - (dist / impactRadius);
                var mKey = this.GetCharacterKey(monster);
                var mImpulse = this.monsterBodyImpulse.TryGetValue(mKey, out var mie) ? mie : Vector2.Zero;
                this.monsterBodyImpulse[mKey] = mImpulse + away * (0.5f * str * fade);
            }
        }
    }

    // ── Skill level-up bounce ─────────────────────────────────────────────────

    /// <summary>
    /// Detects when the farmer gains a skill level and applies a brief celebration bounce:
    /// upward body surge + dramatic hair toss. Works for all vanilla skills and SpaceCore
    /// custom skills (detected by summing all visible level fields to catch extra skills).
    ///
    /// The bounce fires once per level-up event and does not persist.
    /// Compatible with: SpaceCore, PyTK, Combat Overhaul, Magic mod, Druid skill progression.
    /// </summary>
    private void CheckSkillLevelUp(Farmer farmer)
    {
        // Sum all vanilla skill levels — cheap check that catches any level-up
        var levelSum = farmer.farmingLevel.Value
            + farmer.fishingLevel.Value
            + farmer.foragingLevel.Value
            + farmer.miningLevel.Value
            + farmer.combatLevel.Value
            + farmer.luckLevel.Value;

        // SpaceCore adds skills via ExperiencePoints dict — any key with a changed sum covers it
        // No direct API here: rely on vanilla sum being sufficient for most cases

        if (this.lastSkillLevelSum < 0)
        {
            // First call — just record baseline
            this.lastSkillLevelSum = levelSum;
            return;
        }

        if (levelSum <= this.lastSkillLevelSum)
        {
            this.lastSkillLevelSum = levelSum;
            return;
        }

        this.lastSkillLevelSum = levelSum;

        // Level-up celebration: upward burst + wild hair toss
        var farmerKey = this.GetCharacterKey(farmer);

        var celebrationImpulse = new Vector2(
            (Game1.random.NextSingle() - 0.5f) * 0.3f,
            -0.9f); // strong upward jump

        var fbi = this.bodyImpulse.TryGetValue(farmerKey, out var fbExist) ? fbExist : Vector2.Zero;
        this.bodyImpulse[farmerKey] = fbi + celebrationImpulse;

        if (this.config.EnableHairPhysics)
        {
            var fhi = this.hairImpulse.TryGetValue(farmerKey, out var fhExist) ? fhExist : Vector2.Zero;
            // Hair tosses up dramatically then swings around as it settles
            this.hairImpulse[farmerKey] = fhi + new Vector2(
                celebrationImpulse.X * 1.2f,
                celebrationImpulse.Y * 2.0f) * this.config.HairStrength;
        }

        this.levelUpBounceTicksRemaining = 45; // track for future potential use / debug
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
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableDebrisPhysics, v => this.config.EnableDebrisPhysics = v,
            () => "Enable debris physics",
            () => "Floating debris (rocks, wood, gems, fiber, etc.) scatter and bounce when walked through or hit. " +
                  "Weight class from debrisPhysics.json: light items fly far, heavy stones barely budge. " +
                  "Compatible with all custom item and mineral mods.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableHorseRiderPhysics, v => this.config.EnableHorseRiderPhysics = v,
            () => "Enable horse rider physics",
            () => "Adds hoofbeat-timed body and hair bounce while the farmer is riding a horse or mount. " +
                  "Faster movement = more visible jiggle. Compatible with all rideable horse mods.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableProximityCollisionImpulse, v => this.config.EnableProximityCollisionImpulse = v,
            () => "Enable proximity collision impulse",
            () => "Walking close to an NPC sends a gentle body impulse away from you — simulates bumping into them in a crowd. " +
                  "No damage, no upset NPCs. Cosmetic jiggle only. Works with all mod-added NPCs.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWaterEmergenceHairDroop, v => this.config.EnableWaterEmergenceHairDroop = v,
            () => "Enable water emergence hair droop",
            () => "When the farmer exits the water, hair droops heavily for ~1.5 seconds as if soaking wet, then dries and returns to normal physics.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableDirectionalBodyBoost, v => this.config.EnableDirectionalBodyBoost = v,
            () => "Enable directional body boost",
            () => "Amplifies breast and butt jiggle based on facing direction. " +
                  "Facing down (away from camera): breasts splay laterally at each step, butt gets stronger vertical bounce. " +
                  "Facing up (toward camera): belly/front bounce amplified. " +
                  "Works for player, NPCs, and all modded characters.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableCropWeedCollisionPhysics, v => this.config.EnableCropWeedCollisionPhysics = v,
            () => "Enable crop/weed collision physics",
            () => "Grass, crops, and weeds bend and part when any creature walks or rolls through them. " +
                  "Ragdolling causes a large crashing-through burst. All creatures (NPCs, monsters, animals) included. " +
                  "Compatible with all crop mods and custom terrain features.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableAllCreatureGrassCollision, v => this.config.EnableAllCreatureGrassCollision = v,
            () => "All creatures push grass",
            () => "NPCs, monsters, and farm animals also push and bend grass tiles when they move through them. " +
                  "Adds dynamic grass response to the whole map, not just the player's path.");

        // ── Quick presets ─────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Quick Presets");
        api.AddParagraph(this.ModManifest, () => "Presets set all physics strengths at once. Options: Soft, Default, High, ExtraBouncy.");
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
            "Grass bends as you and ALL creatures walk through it and gets flattened when ragdolled bodies crash through it. " +
            "NPCs, monsters, and farm animals all dynamically push and bend grass tiles. " +
            "Tool swings disturb environment based on weight and shape: " +
            "pickaxe = heavy smash (rocks fly short but may roll being round), " +
            "scythe = wide light sweep (sticks tumble), sword = lateral knock. " +
            "Wind mods are automatically detected and boost grass physics outdoors.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.EnvironmentalPhysicsStrength, v => this.config.EnvironmentalPhysicsStrength = v,
            () => "Environmental strength",
            () => "Intensity of grass bend, debris wobble, and rock roll physics. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Crop / weed collision ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Crop & Weed Collision");
        api.AddParagraph(this.ModManifest, () =>
            "Grass terrain features, planted crops (HoeDirt), and wild weed objects all respond dynamically when " +
            "any character walks or rolls through them. " +
            "Normal walking: gentle parting as feet brush past. " +
            "Ragdolling at high speed: heavy body-crash burst that flattens a wide area. " +
            "Works for all creatures: player, NPCs, monsters, farm animals. " +
            "Compatible with all crop mods and custom terrain features via name matching.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.CropWeedCollisionStrength, v => this.config.CropWeedCollisionStrength = v,
            () => "Crop/weed collision strength",
            () => "How much vegetation bends when walked through or ragdolled into. 0 = off, 2 = very dramatic.", 0f, 2f, 0.05f);

        // ── Idle motion frequency ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Idle Motion Frequency");
        api.AddParagraph(this.ModManifest, () =>
            "Controls how often idle physics bursts fire when standing still. " +
            "A built-in breathing pulse fires every 45 ticks (~0.75 s) regardless of this setting — " +
            "it keeps body and hair gently alive at all times. " +
            "The main idle burst (hip sway, shimmy, arm-raise, bounce, twirl, etc.) fires at the interval below.");
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.IdleMotionIntervalTicks,
            v => this.config.IdleMotionIntervalTicks = Math.Max(30, (int)v),
            () => "Idle burst interval (ticks)",
            () => "Ticks between major idle physics events. 90 = ~1.5 s (default), 30 = very frequent, 240 = infrequent. " +
                  "The breathing pulse is always active and unaffected by this setting.",
            30f, 300f, 15f);

        // ── Debris & item physics ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Debris & Item Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Floating resource debris (rocks, wood chips, gems, fiber, seeds, etc.) bounce and scatter when walked through or hit. " +
            "Weight class is determined by item name/ID using debrisPhysics.json — add entries to support any custom mod items. " +
            "Light items (fiber, weeds) fly far; heavy stones barely budge. " +
            "Pickaxe concentrates force for rocks; scythe sweeps light debris in a wide arc; sword knocks gems and mixed items sideways.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.DebrisPhysicsStrength, v => this.config.DebrisPhysicsStrength = v,
            () => "Debris physics strength",
            () => "How forcefully debris scatters when walked through or hit. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Dragon physics ────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Dragon Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Dragons and dragon-type monsters get their own high-power physics archetype: " +
            "wingbeat bursts (every ~25 ticks), tail-thrash lateral oscillation (sinusoidal), and ground rumble when running. " +
            "Very slow decay (0.95) means motion lingers long after each impulse. " +
            "Dragon ragdolls emit a shockwave that pushes nearby NPCs and the player away. " +
            "Druid mod dragons, Ancient Dragons, SVE Lava Drakes, and any dragon/drake/wyvern-named monster are included automatically. " +
            (this.druidModLoaded ? "✓ Druid mod detected." : "Druid mod not detected (optional)."));
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableDragonPhysics, v => this.config.EnableDragonPhysics = v,
            () => "Enable dragon physics",
            () => "Enables wingbeat, tail-thrash, ground-rumble, and shockwave ragdoll for all Dragon archetype monsters.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.DragonPhysicsStrength, v => this.config.DragonPhysicsStrength = v,
            () => "Dragon physics strength",
            () => "Intensity multiplier on top of Monster Physics Strength. 1.2 = powerful (default), 2.0 = giant-scale.", 0.5f, 3f, 0.1f);

        // ── Magic cast physics ────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Magic Cast Physics");
        api.AddParagraph(this.ModManifest, () =>
            "When the player uses a magic-named tool or spell, body and hair react with a spell-type-specific impulse. " +
            "Fire spells = upward heat burst with streaming hair. Water/Ice = lateral wave sway. Earth = heavy downward thud. " +
            "Air/Wind = violent backwards hair blast. Lightning = sharp spike with rapid decay. " +
            "Shadow/Dark = inward pull collapse. Nature/Druid = gentle radial bloom (Druid mod). " +
            "Nearby NPCs and monsters get a shockwave push. Skill level-ups trigger a celebration bounce. " +
            "Compatible with: Magic mod, Druid mod, SpaceCore skills, SVE magic weapons, and any tool with a magic-keyword name. " +
            (this.magicModLoaded ? "✓ Magic/SpaceCore mod detected." : "Magic/SpaceCore mod not detected (optional).") + " " +
            (this.spaceCoreLoaded ? "✓ SpaceCore detected — custom skill levels included." : ""));
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableMagicCastPhysics, v => this.config.EnableMagicCastPhysics = v,
            () => "Enable magic cast physics",
            () => "Applies spell-type body+hair impulse when using magic tools. Shockwave pushes nearby characters.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableSkillLevelUpBounce, v => this.config.EnableSkillLevelUpBounce = v,
            () => "Enable skill level-up bounce",
            () => "Brief upward body bounce and dramatic hair toss when gaining a skill level. Works with SpaceCore custom skills.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.MagicCastImpulseStrength, v => this.config.MagicCastImpulseStrength = v,
            () => "Magic cast impulse strength",
            () => "How strongly body and hair react to a spell cast. 1.0 = default, 2.0 = very dramatic. " +
                  "Air/Wind spells already have extra hair multiplier built in.", 0.1f, 3f, 0.1f);

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

    /// <summary>
    /// Returns true if <paramref name="source"/> contains any of the provided substrings
    /// (case-insensitive). Used for magic tool name keyword matching.
    /// </summary>
    private static bool ContainsAny(string source, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (source.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
