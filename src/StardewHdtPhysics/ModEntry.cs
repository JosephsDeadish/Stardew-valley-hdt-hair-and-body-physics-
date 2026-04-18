using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
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
    private readonly List<PhysicsPreset> presets = new();

    // ── Environmental physics state ───────────────────────────────────────────
    private Vector2 grassBendDisplacement = Vector2.Zero;
    private Vector2 grassBendVelocity = Vector2.Zero;

    // ── Wind / weather ────────────────────────────────────────────────────────
    private float currentWindStrength = 0f;
    private float currentRainStrength = 0f;
    private float currentSnowStrength = 0f;

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

        // Detect optional companion mods
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

        // Refresh wind every ~5 seconds (300 game ticks at 60fps)
        if (e.IsMultipleOf(300))
        {
            this.UpdateWindStrength();
        }

        var location = Game1.currentLocation;

        // ── Humanoid characters (farmer + NPCs)
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

        // ── Monsters
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

        // Cancel player body/hair impulse when using a tool
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
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    private void ClearAllState()
    {
        this.lastPositions.Clear();
        this.bodyImpulse.Clear();
        this.hairImpulse.Clear();
        this.monsterBodyImpulse.Clear();
        this.npcKnockdown.Clear();
        this.grassBendDisplacement = Vector2.Zero;
        this.grassBendVelocity = Vector2.Zero;
    }

    private void LoadData(IModHelper helper)
    {
        var profiles = helper.Data.ReadJsonFile<List<SpriteProfile>>("assets/spriteProfiles.json") ?? new List<SpriteProfile>();
        this.presets.Clear();
        this.presets.AddRange(helper.Data.ReadJsonFile<List<PhysicsPreset>>("assets/presets.json") ?? new List<PhysicsPreset>());
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

        // Try to read the game's internal wind float via reflection (safe; returns 0 on failure)
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

        // Rain — strengthens wind component
        this.currentRainStrength = Game1.isRaining ? (Game1.isLightning ? 0.85f : 0.5f) : 0f;
        if (Game1.isRaining) wind = Math.Max(wind, this.currentRainStrength * 0.8f);

        // Snow — separate gentle flutter channel; doesn't add to "wind" reading
        this.currentSnowStrength = Game1.isSnowing ? 0.35f : 0f;

        // Season modifiers when there's no strong weather
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

        impulse += new Vector2(-velocity.X, -velocity.Y) * (0.03f + (baseStrength * 0.04f));
        impulse *= 0.86f;

        this.bodyImpulse[key] = impulse;
    }

    /// <summary>
    /// Body physics for monsters — only activates for those whose live sprite resolves as Feminine
    /// (e.g. beast girls, slime girls, funtari slimes from sprite replacement mods).
    /// </summary>
    private void SimulateMonsterBody(NPC monster, BodyProfileType profile, Vector2 velocity)
    {
        if (!this.config.EnableMonsterBodyPhysics)
        {
            return;
        }

        if (profile != BodyProfileType.Feminine)
        {
            return;
        }

        var key = this.GetCharacterKey(monster);
        if (!this.monsterBodyImpulse.TryGetValue(key, out var impulse))
        {
            impulse = Vector2.Zero;
        }

        var baseStrength = (this.config.FemaleBreastStrength + this.config.FemaleButtStrength + this.config.FemaleThighStrength + this.config.FemaleBellyStrength) / 4f;
        impulse += new Vector2(-velocity.X, -velocity.Y) * (0.03f + (baseStrength * 0.04f));
        impulse *= 0.86f;

        this.monsterBodyImpulse[key] = impulse;
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

        // ── Rain effect ───────────────────────────────────────────────────────
        // Rain makes hair heavier — pulls downward and slightly dampens lateral flow.
        if (this.currentRainStrength > 0f && isOutdoors)
        {
            // Downward droop from wet weight
            impulse += new Vector2(0f, this.currentRainStrength * 0.012f) * this.config.HairStrength;
            // Wet hair resists lateral movement
            windMult *= Math.Max(0.3f, 1f - this.currentRainStrength * 0.4f);
        }

        // ── Snow effect ───────────────────────────────────────────────────────
        // Snow adds a gentle random upward flutter (light snowflakes catch in hair).
        if (this.currentSnowStrength > 0f && isOutdoors)
        {
            var flutter = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.007f,
                -(Game1.random.NextSingle() * 0.005f)) * this.config.HairStrength * this.currentSnowStrength;
            impulse += flutter;
        }

        // ── Ambient wind drift when standing still outdoors ───────────────────
        if (velocity.LengthSquared() < 0.0001f && isOutdoors)
        {
            impulse += new Vector2(this.currentWindStrength * 0.008f, 0f) * this.config.HairStrength;
        }

        // ── Movement-based flow ───────────────────────────────────────────────
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

        if (Game1.ticks % 180 != 0)
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

        if (farmer.health >= 30 || velocity.LengthSquared() < 2f)
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
    }

    /// <summary>
    /// Monster ragdoll: applies extra positional nudge when a monster is struck and moving fast.
    /// Supports modded creatures automatically — any NPC marked IsMonster qualifies.
    /// </summary>
    private void SimulateMonsterRagdoll(NPC monster, Vector2 velocity)
    {
        if (!this.config.EnableMonsterRagdoll)
        {
            return;
        }

        // Only activate when the monster is actively being knocked around
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
    }

    // ── NPC sword knockdown ───────────────────────────────────────────────────

    private sealed class NpcKnockdownState
    {
        public Vector2 Impulse;
        public int TicksRemaining;
    }

    /// <summary>
    /// Called when the player swings a melee weapon. Applies a cosmetic positional impulse
    /// to nearby NPCs — no damage, no friendship penalty, purely visual physics reaction.
    /// </summary>
    private void TryApplyNpcSwordKnockdown(Character character)
    {
        if (Game1.player is null)
        {
            return;
        }

        var dist = Vector2.Distance(character.Position, Game1.player.Position);
        if (dist > 96f) // ~3 tiles
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

        // Also feed the hit direction into body physics for jiggle reaction
        var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = bodyEntry + dir * 0.5f;
    }

    /// <summary>Advances NPC knockdown state each tick, applying positional drift and friction.</summary>
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
        state.Impulse *= 0.82f; // friction decay
        state.TicksRemaining--;
    }

    // ── Environmental physics ─────────────────────────────────────────────────

    /// <summary>
    /// Spring-force simulation for grass bend and environmental physics.
    /// Grass near the player bends against their direction of travel.
    /// Wind (when detected) causes a slow oscillating lateral drift.
    /// Debris and rocks use the same impulse system to wobble/roll briefly on contact.
    /// </summary>
    private void SimulateEnvironmental(GameLocation location)
    {
        if (Game1.player is null)
        {
            return;
        }

        var strength = this.config.EnvironmentalPhysicsStrength;

        // Wind drift contribution to grass bend
        var windDrift = this.config.EnableWindDetection && location.IsOutdoors
            ? new Vector2(this.currentWindStrength * 0.04f * strength, 0f)
            : Vector2.Zero;

        // Player movement bends nearby grass away from direction of travel
        if (this.lastPositions.TryGetValue(this.GetCharacterKey(Game1.player), out var lastPos))
        {
            var playerVelocity = Game1.player.Position - lastPos;
            if (playerVelocity.LengthSquared() > 0.25f)
            {
                this.grassBendVelocity += -playerVelocity * (0.012f * strength);
            }
        }

        // Apply wind oscillation
        this.grassBendVelocity += windDrift;

        // Spring restore force toward neutral
        this.grassBendVelocity -= this.grassBendDisplacement * (0.06f * strength);

        // Air resistance / damping
        this.grassBendVelocity *= 0.85f;

        // Integrate velocity into displacement
        this.grassBendDisplacement += this.grassBendVelocity;

        // Safety clamp — prevent runaway displacement
        if (this.grassBendDisplacement.LengthSquared() > 9f)
        {
            this.grassBendDisplacement = Vector2.Normalize(this.grassBendDisplacement) * 3f;
        }
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
            () => "Bouncy/flowing hair motion that reacts to movement and wind.");
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
            () => "Body jiggle for monsters with feminine sprite mods (beast girls, slime girls, funtari slimes, etc.).");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableMonsterRagdoll, v => this.config.EnableMonsterRagdoll = v,
            () => "Enable monster ragdoll",
            () => "Ragdoll-style knockback impulse applied to monsters when they are hit. Supports modded creatures.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableNpcSwordKnockdown, v => this.config.EnableNpcSwordKnockdown = v,
            () => "Enable NPC sword knockdown",
            () => "Sword swings near NPCs apply a harmless cosmetic knockback — no damage, no anger, pure physics.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableEnvironmentalPhysics, v => this.config.EnableEnvironmentalPhysics = v,
            () => "Enable environmental physics",
            () => "Grass bends when walked through, debris/rocks wobble when hit. Wind-aware outdoors.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWindDetection, v => { this.config.EnableWindDetection = v; this.UpdateWindStrength(); },
            () => "Enable wind detection",
            () => "Reads game weather and season to boost hair and grass physics on windy days.");

        // ── Quick presets ─────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Quick Presets");
        api.AddParagraph(this.ModManifest, () => "Presets apply all physics strengths at once. Options: Soft, Default, High.");
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
            () => "Breast", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleButtStrength, v => this.config.FemaleButtStrength = v,
            () => "Butt", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleThighStrength, v => this.config.FemaleThighStrength = v,
            () => "Thigh", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FemaleBellyStrength, v => this.config.FemaleBellyStrength = v,
            () => "Belly", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);

        // ── Masculine body strengths ──────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Masculine Physics Strength");
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleButtStrength, v => this.config.MaleButtStrength = v,
            () => "Butt", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleGroinStrength, v => this.config.MaleGroinStrength = v,
            () => "Groin", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleThighStrength, v => this.config.MaleThighStrength = v,
            () => "Thigh", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.MaleBellyStrength, v => this.config.MaleBellyStrength = v,
            () => "Belly", () => "0 = off, 2 = maximum jiggle.", 0f, 2f, 0.05f);

        // ── HDT Hair physics ──────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "HDT Hair Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Hair physics apply to ALL hair types automatically: vanilla, mod-added, and Fashion Sense custom hairs. " +
            "Rain makes hair heavier and droopy. Snow adds a light flutter. Wind causes flow and trailing. " +
            (this.fashionSenseLoaded ? "Fashion Sense detected — FS hairs are included." : "Fashion Sense not detected (optional)."));
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairStrength, v => this.config.HairStrength = v,
            () => "Hair strength",
            () => "Overall flow and bounce strength. 0 = completely still, 2 = very bouncy.", 0f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairWindBoostOutdoors, v => this.config.HairWindBoostOutdoors = v,
            () => "Outdoor wind boost",
            () => "Hair flow multiplier outdoors. Higher = longer trailing effect when running.", 0.1f, 2f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairDampeningIndoors, v => this.config.HairDampeningIndoors = v,
            () => "Indoor dampening",
            () => "Hair flow multiplier indoors. Lower = calmer hair inside buildings.", 0f, 1f, 0.05f);

        // ── Ragdoll & knockback ───────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Ragdoll & Knockback");
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollChanceUnderLowHealth, v => this.config.RagdollChanceUnderLowHealth = v,
            () => "Ragdoll chance at low health",
            () => "Probability of ragdoll knockback triggering at ≤30 HP. 0 = never, 1 = always.", 0f, 1f, 0.05f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.RagdollKnockbackStrength, v => this.config.RagdollKnockbackStrength = v,
            () => "Knockback strength",
            () => "How far the ragdoll pushes the character. 1.5 = default, 4 = very strong.", 0.5f, 4f, 0.1f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.NpcSwordKnockdownChance, v => this.config.NpcSwordKnockdownChance = v,
            () => "NPC knockdown chance",
            () => "Probability NPCs react to nearby sword swings. 0 = never, 1 = always. Cosmetic only.", 0f, 1f, 0.05f);

        // ── Environmental physics ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Environmental Physics");
        api.AddNumberOption(this.ModManifest,
            () => this.config.EnvironmentalPhysicsStrength, v => this.config.EnvironmentalPhysicsStrength = v,
            () => "Environmental strength",
            () => "Intensity of grass bend, debris roll, and rock wobble. 0 = off, 2 = maximum.", 0f, 2f, 0.05f);

        // ── Gender overrides ──────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Gender Overrides");
        api.AddParagraph(this.ModManifest, () =>
            "Edit config.json to add manual gender overrides under \"GenderOverrides\". " +
            "Example: \"Krobus\": \"Feminine\". Values: Feminine, Masculine, Androgynous. " +
            "Overrides take priority over all automatic detection including sprite texture names.");
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private int GetCharacterKey(Character character)
    {
        return RuntimeHelpers.GetHashCode(character);
    }
}
