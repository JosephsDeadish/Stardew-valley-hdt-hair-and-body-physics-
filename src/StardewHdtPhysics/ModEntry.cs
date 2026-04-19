using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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
    private readonly Dictionary<int, Vector2> bodyImpulse = new();        // external force accumulator (body center)
    private readonly Dictionary<int, Vector2> hairImpulse = new();        // hair root external force
    private readonly Dictionary<int, Vector2> clothingImpulse = new();
    private readonly Dictionary<int, Vector2> monsterBodyImpulse = new();
    private readonly Dictionary<int, NpcKnockdownState> npcKnockdown = new();
    private readonly Dictionary<int, NpcKnockdownState> farmAnimalKnockdown = new();
    private readonly List<PhysicsPreset> presets = new();
    private readonly List<MonsterArchetypeRule> monsterArchetypeRules = new();
    private readonly List<DebrisWeightRule> debrisWeightRules = new();

    // ── Per-bone spring state (real spring-damper per body part) ──────────────
    private readonly Dictionary<int, BoneGroup>       boneGroups   = new();
    private readonly Dictionary<int, HairChain>       hairChains   = new();
    // ── Wing / Fur / Tail / Animal bones ──────────────────────────────────────
    private readonly Dictionary<int, WingPair>        wingPairs    = new();
    private readonly Dictionary<int, FurChain>        furChains    = new();
    private readonly Dictionary<int, TailChain>       tailChains   = new();
    private readonly Dictionary<int, AnimalBoneGroup> animalBones  = new();

    // ── Environmental physics state ───────────────────────────────────────────
    private Vector2 grassBendDisplacement = Vector2.Zero;
    private Vector2 grassBendVelocity = Vector2.Zero;

    // ── Visual render wobble (applied during RenderingWorld, restored after) ──
    private readonly Dictionary<Character, Vector2> savedPhysicsPositions = new();
    private const float PhysicsVisualScale = 10f;  // px multiplier: impulse 0.4 → 4 px wobble
    private const float PhysicsVisualMaxPx  = 10f; // hard cap so characters never teleport

    // ── Wind / weather ────────────────────────────────────────────────────────
    private float currentWindStrength = 0f;
    private float currentRainStrength = 0f;
    private float currentSnowStrength = 0f;

    // ── Hit tracking ──────────────────────────────────────────────────────────
    private int lastPlayerHealth = -1;
    private bool wasSwimming = false;
    private int waterEmergenceTicksRemaining = 0;
    private bool wasEating = false;
    private bool wasLightning = false;

    // ── Skill level tracking (for level-up bounce) ────────────────────────────
    private int lastSkillLevelSum = -1;
    private int levelUpBounceTicksRemaining = 0;

    // ── Fishing physics state ──────────────────────────────────────────────────
    private bool wasBobberInAir = false;        // was the bobber still flying last tick?
    private bool wasFishBiting = false;         // was a fish on the hook last tick?
    private bool wasFishCaught = false;         // was a fish caught last tick?

    // ── Dragon ragdoll cooldown ────────────────────────────────────────────────
    private readonly Dictionary<int, int> dragonRagdollCooldown = new();

    // ── Typed physics debris particles ────────────────────────────────────────
    /// Arcing, scatter-on-walk, material-typed debris particles (wood splinters, sawdust, stone chunks).
    private readonly List<TypedPhysicsParticle> typedParticles = new();
    private const int MaxTypedParticles = 300;  // hard cap to prevent lag on large maps

    /// Tile positions of trees present last tick — used to detect tree-fell events.
    private readonly HashSet<Point> prevTreeTiles = new();

    /// 1×1 white Texture2D for particle rendering (lazily created on first render).
    private Texture2D? pixelTexture;

    // ── Hitstop ───────────────────────────────────────────────────────────────
    private int hitstopTicksRemaining = 0;

    // ── Per-character idle cycling state ──────────────────────────────────────
    // Tracks which step (0-7) each character is on in their per-profile idle cycle.
    // Different characters start at different offsets so they don't all do the same move.
    private readonly Dictionary<int, int> idleCycleStep = new();

    // ── Optional mod integrations ─────────────────────────────────────────────
    private bool fashionSenseLoaded = false;
    private bool druidModLoaded = false;
    private bool magicModLoaded = false;
    private bool spaceCoreLoaded = false;
    // ── Monster content mods ──────────────────────────────────────────────────
    private bool nudeMonsterModLoaded    = false;  // naked/nude monster sprite replacers
    private bool milkyOfMythsLoaded      = false;  // Milky of Myths beast-girl/monster mods
    private bool monstersAnonymousLoaded = false;  // Monsters Anonymous creature content
    private bool extraMonsterModLoaded   = false;  // generic "more monsters" / creature packs
    private string detectedMonsterMods   = "none";

    // Weather mod integrations (detected in Entry, used in ReadWeatherModBoosts)
    private bool moreRainLoaded = false;
    private bool climateOfFerngillLoaded = false;
    private bool windEffectsLoaded = false;
    private bool cloudySkiesLoaded = false;
    private bool sveWeatherLoaded = false;
    private bool extremeWeatherLoaded = false;
    private string detectedWeatherMods = "none";

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

        this.DetectWeatherMods(helper);
        this.DetectMonsterMods(helper);

        // GMCM registration happens in OnGameLaunched (after all mods have loaded) for correct API availability

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Display.RenderingWorld += this.OnRenderingWorld;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Player.Warped += this.OnPlayerWarped;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.ClearAllState();
        this.config = this.Helper.ReadConfig<ModConfig>();
        this.detector.SetConfigOverrides(this.config.GenderOverrides);
        this.UpdateWindStrength();

        // Show in-game HUD message so the player knows the mod is active
        Game1.addHUDMessage(new HUDMessage("SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More — active", HUDMessage.achievement_type));
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.UpdateWindStrength();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.Monitor.Log("╔══════════════════════════════════════════════════════════════╗", LogLevel.Info);
        this.Monitor.Log("║  SVP Physics, Collisions, Hitstops, Idles, Ragdolls — v0.8.0 ║", LogLevel.Info);
        this.Monitor.Log("║  Typed debris (stone/wood/sawdust), per-bone clothing mult.   ║", LogLevel.Info);
        this.Monitor.Log("╠══════════════════════════════════════════════════════════════╣", LogLevel.Info);
        this.Monitor.Log($"║  Body physics:           {(this.config.EnableBodyPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Hair physics (chain):   {(this.config.EnableHairPhysics ? "ON " : "OFF")} ({this.config.HairChainSegments} segments)", LogLevel.Info);
        this.Monitor.Log($"║  Wing physics (4-bone):  {(this.config.EnableWingPhysics ? "ON " : "OFF")} stiff={this.config.WingChainStiffness:F2}", LogLevel.Info);
        this.Monitor.Log($"║  Fur chain physics:      {(this.config.EnableFurPhysics ? "ON " : "OFF")} ({this.config.FurChainSegments} segments)", LogLevel.Info);
        this.Monitor.Log($"║  Tail chain physics:     {(this.config.EnableTailPhysics ? "ON " : "OFF")} ({this.config.TailChainSegments} segments)", LogLevel.Info);
        this.Monitor.Log($"║  Animal bone physics:    {(this.config.EnableAnimalBonePhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Wood shatter VFX:       {(this.config.EnableWoodShatterEffects ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Bone stiffness:         {this.config.BoneStiffness:F2}  damping: {this.config.BoneDamping:F2}", LogLevel.Info);
        this.Monitor.Log($"║  Clothing flow physics:  {(this.config.EnableClothingFlowPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Idle motion:            {(this.config.EnableIdleMotion ? "ON " : "OFF")} (interval: {this.config.IdleMotionIntervalTicks} ticks)", LogLevel.Info);
        this.Monitor.Log($"║  Hitstop + hit flash:    {(this.config.EnableHitstopEffect ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Ragdoll knockback:      {(this.config.EnableRagdollKnockback ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Monster physics:        {(this.config.EnableMonsterBodyPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Monster ragdoll:        {(this.config.EnableMonsterRagdoll ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Farm animal physics:    {(this.config.EnableFarmAnimalPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Environmental physics:  {(this.config.EnableEnvironmentalPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Debris physics:         {(this.config.EnableDebrisPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Typed debris particles: {(this.config.EnableTypedPhysicsDebris ? "ON " : "OFF")} (lifetime: {this.config.TypedDebrisLifetimeTicks} ticks)", LogLevel.Info);
        this.Monitor.Log($"║  Dragon physics:         {(this.config.EnableDragonPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Magic cast physics:     {(this.config.EnableMagicCastPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Wind detection:         {(this.config.EnableWindDetection ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Warp-step impulse:      {(this.config.EnableWarpStepImpulse ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Eating bounce:          {(this.config.EnableEatingBounce ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Lightning flinch:       {(this.config.EnableLightningFlinch ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Fishing physics:        {(this.config.EnableFishingPhysics ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Bobber bonk:            {(this.config.EnableBobberBonk ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Blood splatter VFX:     {(this.config.EnableBloodSplatterEffects ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Spark VFX:              {(this.config.EnableSparkEffects ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Slime spray VFX:        {(this.config.EnableSlimeSprayEffects ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log($"║  Tool collision hitstop: {(this.config.EnableToolCollisionHitstop ? "ON " : "OFF")}", LogLevel.Info);
        this.Monitor.Log("╠══════════════════════════════════════════════════════════════╣", LogLevel.Info);
        this.Monitor.Log($"║  Fashion Sense:   {(this.fashionSenseLoaded ? "✓ detected" : "not detected (optional)")}", LogLevel.Info);
        this.Monitor.Log($"║  Druid mod:       {(this.druidModLoaded ? "✓ detected (dragon physics active)" : "not detected (optional)")}", LogLevel.Info);
        this.Monitor.Log($"║  Magic/SpaceCore: {(this.magicModLoaded ? "✓ detected (spell physics active)" : "not detected (optional)")}", LogLevel.Info);
        this.Monitor.Log($"║  Weather mods:    {this.detectedWeatherMods}", LogLevel.Info);
        this.Monitor.Log($"║  Monster mods:    {this.detectedMonsterMods}", LogLevel.Info);
        this.Monitor.Log($"║  Preset loaded:   {this.config.Preset}", LogLevel.Info);
        this.Monitor.Log("╚══════════════════════════════════════════════════════════════╝", LogLevel.Info);

        // Re-register GMCM now that all mods have loaded so the page picks up the correct name.
        this.RegisterConfigMenu();
    }

    /// <summary>
    /// Before the world is drawn: apply a scaled bodyImpulse as a temporary visual position offset
    /// so the character sprite physically wobbles/jiggles on screen. The offset is capped to
    /// PhysicsVisualMaxPx pixels so characters never visibly teleport.
    /// </summary>
    private void OnRenderingWorld(object? sender, RenderingWorldEventArgs e)
    {
        if (!Context.IsWorldReady || !this.config.EnableBodyPhysics || Game1.currentLocation is null)
        {
            return;
        }

        this.savedPhysicsPositions.Clear();

        foreach (var character in this.EnumerateHumanoids(Game1.currentLocation))
        {
            var key     = this.GetCharacterKey(character);
            var profile = this.detector.Resolve(character);

            // ── Per-bone spring displacement (primary physics signal) ───────
            Vector2 boneOffset = Vector2.Zero;
            if (this.boneGroups.TryGetValue(key, out var group) && !group.IsAllNearRest())
            {
                boneOffset = group.ComputeVisualDisplacement(profile);
            }

            // ── Hair chain tip drives additional body-sway contribution ─────
            // Hair being flung around pulls the body a tiny bit in the same direction.
            Vector2 hairOffset = Vector2.Zero;
            if (this.config.EnableHairPhysics
                && this.hairChains.TryGetValue(key, out var chain)
                && !chain.IsNearRest())
            {
                hairOffset = chain.GetTipDisplacement() * 0.18f * this.config.HairStrength;
            }

            // ── Clothing flow ────────────────────────────────────────────────
            var clothingOffset = (this.config.EnableClothingFlowPhysics && this.clothingImpulse.TryGetValue(key, out var ci))
                ? ci * 0.42f
                : Vector2.Zero;

            // ── Fallback: legacy body impulse (for any code paths not yet ───
            //    driving the spring engine, e.g. monster body, clothing-only)
            var legacyImpulse = this.bodyImpulse.TryGetValue(key, out var li) ? li : Vector2.Zero;

            // Blend: bone spring is primary; legacy impulse fills the gap if bones are at rest
            var primaryOffset = (boneOffset.LengthSquared() > legacyImpulse.LengthSquared())
                ? boneOffset
                : legacyImpulse;

            var blended = primaryOffset + hairOffset + clothingOffset;

            if (blended.LengthSquared() < 0.015f * 0.015f)
            {
                continue; // skip negligible wobble
            }

            // Scale to screen pixels and hard-cap
            var visualOffset = new Vector2(
                Math.Clamp(blended.X * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx),
                Math.Clamp(blended.Y * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx));

            this.savedPhysicsPositions[character] = character.Position;
            character.Position += visualOffset;
        }

        // ── Monster wing/fur/tail render displacement ─────────────────────────
        if (this.config.EnableMonsterBodyPhysics)
        {
            foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
            {
                var key     = this.GetCharacterKey(monster);
                var impulse = this.monsterBodyImpulse.TryGetValue(key, out var mi) ? mi : Vector2.Zero;

                Vector2 wingOff = Vector2.Zero;
                if (this.wingPairs.TryGetValue(key, out var wp) && !wp.IsNearRest())
                    wingOff = wp.GetVisualDisplacement() * 0.55f;

                Vector2 furOff = Vector2.Zero;
                if (this.furChains.TryGetValue(key, out var fc) && !fc.IsNearRest())
                    furOff = fc.GetDisplacement() * 0.35f;

                Vector2 tailOff = Vector2.Zero;
                if (this.tailChains.TryGetValue(key, out var tc) && !tc.IsNearRest())
                    tailOff = tc.GetTipDisplacement() * 0.30f;

                var blendedM = impulse + wingOff + furOff + tailOff;
                if (blendedM.LengthSquared() < 0.015f * 0.015f) continue;

                var visOffM = new Vector2(
                    Math.Clamp(blendedM.X * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx),
                    Math.Clamp(blendedM.Y * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx));

                this.savedPhysicsPositions[monster] = monster.Position;
                monster.Position += visOffM;
            }
        }

        // ── Farm animal bone/fur/tail render displacement ─────────────────────
        if (this.config.EnableFarmAnimalPhysics)
        {
            foreach (var animal in EnumerateFarmAnimals(Game1.currentLocation))
            {
                var key     = this.GetCharacterKey(animal);
                var impulse = this.bodyImpulse.TryGetValue(key, out var biA) ? biA : Vector2.Zero;

                Vector2 boneOff = Vector2.Zero;
                if (this.animalBones.TryGetValue(key, out var ab) && !ab.IsAllNearRest())
                    boneOff = ab.GetVisualDisplacement() * this.config.AnimalBoneStrength;

                Vector2 furOff = Vector2.Zero;
                if (this.furChains.TryGetValue(key, out var fc2) && !fc2.IsNearRest())
                    furOff = fc2.GetDisplacement() * 0.28f;

                Vector2 tailOff = Vector2.Zero;
                if (this.tailChains.TryGetValue(key, out var tc2) && !tc2.IsNearRest())
                    tailOff = tc2.GetTipDisplacement() * 0.25f;

                var blendedA = impulse + boneOff + furOff + tailOff;
                if (blendedA.LengthSquared() < 0.015f * 0.015f) continue;

                var visOffA = new Vector2(
                    Math.Clamp(blendedA.X * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx),
                    Math.Clamp(blendedA.Y * PhysicsVisualScale, -PhysicsVisualMaxPx, PhysicsVisualMaxPx));

                if (animal is Character animalChar)
                {
                    this.savedPhysicsPositions[animalChar] = animalChar.Position;
                    animalChar.Position += visOffA;
                }
            }
        }
    }

    /// <summary>
    /// After the world is drawn: restore all character positions so the visual offset
    /// does not affect game logic (collision, pathfinding, proximity checks, etc.).
    /// </summary>
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (this.savedPhysicsPositions.Count == 0)
        {
            return;
        }

        foreach (var kv in this.savedPhysicsPositions)
        {
            kv.Key.Position = kv.Value;
        }

        this.savedPhysicsPositions.Clear();

        // Draw typed physics debris particles on top of restored world geometry.
        if (this.config.EnableTypedPhysicsDebris && this.typedParticles.Count > 0)
        {
            this.RenderTypedParticles(e.SpriteBatch);
        }
    }

    /// <summary>
    /// When the farmer steps through a door, warp point, or teleport, apply a brief body bounce
    /// and hair toss to simulate the physical momentum of passing through a threshold.
    /// Also resets the clothing impulse so flowy fabrics "settle" after the transition.
    /// </summary>
    private void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.config.EnableWarpStepImpulse || Game1.player is null)
        {
            return;
        }

        var playerKey = this.GetCharacterKey(Game1.player);

        // Brief upward-then-forward bounce as the farmer steps through
        var warpImpulse = new Vector2(
            (Game1.random.NextSingle() - 0.5f) * 0.22f,
            -0.35f); // slight upward pop as they cross the threshold

        if (this.config.EnableBodyPhysics)
        {
            var existing = this.bodyImpulse.TryGetValue(playerKey, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[playerKey] = existing + warpImpulse;
        }

        if (this.config.EnableHairPhysics)
        {
            var hairExisting = this.hairImpulse.TryGetValue(playerKey, out var hi) ? hi : Vector2.Zero;
            // Hair tosses upward and slightly backward on entry
            this.hairImpulse[playerKey] = hairExisting + new Vector2(warpImpulse.X * 0.8f, warpImpulse.Y * 1.4f) * this.config.HairStrength;
        }

        // Settle clothing physics — flowy items need a small resettling impulse after transition
        if (this.config.EnableClothingFlowPhysics)
        {
            var ci = this.clothingImpulse.TryGetValue(playerKey, out var cExist) ? cExist : Vector2.Zero;
            this.clothingImpulse[playerKey] = ci * 0.4f + warpImpulse * 0.5f * this.config.ClothingFlowStrength;
        }

        this.Monitor.Log($"[SVP] Warp to '{e.NewLocation?.Name ?? "unknown"}' — door-step physics impulse applied.", LogLevel.Trace);

        // Clear tree tracking so we don't false-trigger tree-fell events on arrival at new location.
        this.prevTreeTiles.Clear();
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

        // Eating/drinking bounce: when the farmer starts eating, apply a chin-dip body impulse
        if (this.config.EnableEatingBounce && this.config.EnableBodyPhysics && Game1.player is not null)
        {
            var isEating = Game1.player.isEating;
            if (isEating && !this.wasEating)
            {
                this.ApplyEatingBounce(Game1.player);
            }

            this.wasEating = isEating;
        }

        // Lightning flinch: brief body/hair flinch when lightning strikes
        if (this.config.EnableLightningFlinch && this.config.EnableBodyPhysics && Game1.player is not null)
        {
            var isLightning = Game1.isLightning;
            if (isLightning && !this.wasLightning)
            {
                this.ApplyLightningFlinch(Game1.player);
            }

            this.wasLightning = isLightning;
        }

        // Fishing physics: state-change body/hair impulses for cast, nibble, catch
        if (this.config.EnableFishingPhysics && this.config.EnableBodyPhysics && Game1.player is not null)
        {
            this.TickFishingPhysics(Game1.player);
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
            this.SimulateClothing(character, velocity, profile);
            this.SimulateRunStepImpulse(character, velocity, profile);
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
                var archetype = this.DetectMonsterArchetype(monster);
                this.SimulateMonsterBody(monster, profile, velocity);
                this.SimulateMonsterRagdoll(monster, velocity);
                // Idle physics when the monster is roughly stationary
                if (velocity.LengthSquared() < 0.5f)
                {
                    this.SimulateMonsterIdle(monster, archetype);
                }

                // Wing physics for flying archetypes (4-bone per-wing chain)
                if (archetype == MonsterPhysicsArchetype.Bat
                    || archetype == MonsterPhysicsArchetype.Dragon
                    || archetype == MonsterPhysicsArchetype.FlyingBug)
                {
                    this.SimulateWingPhysics(monster, velocity);
                }

                // Fur physics for furry archetypes
                if (archetype == MonsterPhysicsArchetype.Furry)
                {
                    this.SimulateFurPhysics(key, velocity);
                }

                // Tail physics for creatures that have tails
                if (archetype == MonsterPhysicsArchetype.Furry
                    || archetype == MonsterPhysicsArchetype.Dragon
                    || archetype == MonsterPhysicsArchetype.Worm)
                {
                    this.SimulateTailPhysics(key, velocity);
                }

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
                // Idle physics when the animal is roughly stationary
                if (velocity.LengthSquared() < 0.3f)
                {
                    this.SimulateFarmAnimalIdle(animal);
                }

                // Per-bone animal physics (ears, snout, body)
                var typeName = animal.type.Value ?? string.Empty;
                var isHeavyAnimal = typeName.Contains("Cow", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Goat", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Sheep", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Pig", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Bull", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Ostrich", StringComparison.OrdinalIgnoreCase);
                this.SimulateAnimalBonePhysics(key, velocity, isHeavyAnimal);

                // Fur physics for woolly/furry animals
                var hasFur = typeName.Contains("Sheep", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Rabbit", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Dog", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Cat", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Wolf", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Fur", StringComparison.OrdinalIgnoreCase);
                if (hasFur)
                {
                    this.SimulateFurPhysics(key, velocity);
                }

                // Tail physics for tailed animals
                var hasTail = !typeName.Contains("Chicken", StringComparison.OrdinalIgnoreCase)
                    && !typeName.Contains("Duck", StringComparison.OrdinalIgnoreCase);
                if (hasTail)
                {
                    this.SimulateTailPhysics(key, velocity);
                }

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

        // ── Typed physics debris particles — step every tick for smooth arcs
        if (this.config.EnableTypedPhysicsDebris && this.typedParticles.Count > 0)
        {
            this.StepTypedParticles(location);
        }

        // ── Tree-fell detection — every 3 ticks (trees don't fall sub-tick)
        if (this.config.EnableTypedPhysicsDebris && e.IsMultipleOf(3))
        {
            this.TryDetectTreeFell(location);
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

        // Tool use recoil: body recoils backward (opposite to facing direction) and hair swings forward.
        // Heavier tools produce stronger recoil. Previously this block incorrectly zeroed out the impulse.
        if (this.config.EnableBodyPhysics && Game1.player.CurrentTool is not null)
        {
            var recoilStrength = Game1.player.CurrentTool switch
            {
                Pickaxe     => 0.28f,
                Axe         => 0.24f,
                MeleeWeapon => 0.18f,
                Hoe         => 0.12f,
                _           => 0.10f
            };

            // Recoil direction = opposite of facing direction
            var recoilDir = Game1.player.FacingDirection switch
            {
                0 => new Vector2(0f,  1f),   // facing up    → recoil downward
                1 => new Vector2(-1f, 0f),   // facing right → recoil left
                2 => new Vector2(0f, -1f),   // facing down  → recoil upward
                3 => new Vector2(1f,  0f),   // facing left  → recoil right
                _ => Vector2.Zero
            };

            var existing = this.bodyImpulse.TryGetValue(playerKey, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[playerKey] = existing + recoilDir * recoilStrength;

            if (this.config.EnableHairPhysics)
            {
                // Hair swings forward (in facing direction) as the arm swings out
                var hairSwingDir = -recoilDir;
                var hairExisting = this.hairImpulse.TryGetValue(playerKey, out var hi) ? hi : Vector2.Zero;
                this.hairImpulse[playerKey] = hairExisting + hairSwingDir * (recoilStrength * this.config.HairStrength * 0.7f);
            }
        }

        // NPC knockdown — works for any combat/heavy tool: sword, axe, scythe, pickaxe, modded weapons.
        // A tool is "combat capable" if it is not a non-combat utility (watering can, fishing rod, hoe,
        // milk pail, shears).  This covers vanilla weapons AND any weapon mod that adds a MeleeWeapon-
        // derived item or a Tool with a combat-sounding name.
        if (this.config.EnableNpcSwordKnockdown
            && Game1.player.CurrentTool is not null
            && this.IsCombatCapableTool(Game1.player.CurrentTool)
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

            // Also knock monsters back (they already get ragdoll, but knockdown adds cosmetic stumble)
            foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
            {
                this.TryApplyNpcSwordKnockdown(monster);
            }
        }

        // Farm animal tool collision reaction — any non-utility tool causes a startle bounce
        if (this.config.EnableFarmAnimalPhysics
            && Game1.player.CurrentTool is not null
            && this.IsCombatCapableTool(Game1.player.CurrentTool)
            && Game1.currentLocation is not null)
        {
            foreach (var animal in EnumerateFarmAnimals(Game1.currentLocation))
            {
                this.TryApplyFarmAnimalCollision(animal);
            }
        }

        // Bobber bonk: fishing rod cast aimed at nearby NPC/animal
        if (this.config.EnableBobberBonk
            && Game1.player.CurrentTool is FishingRod
            && Game1.currentLocation is not null)
        {
            this.TryBobberBonk(Game1.player);
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

        // Combat hit VFX: spark on stone/metal, slime spray, blood splatter
        this.ApplyCombatHitVfx(Game1.player.CurrentTool, Game1.player);
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    private void ClearAllState()
    {
        this.lastPositions.Clear();
        this.bodyImpulse.Clear();
        this.hairImpulse.Clear();
        this.clothingImpulse.Clear();
        this.monsterBodyImpulse.Clear();
        this.npcKnockdown.Clear();
        this.farmAnimalKnockdown.Clear();
        // Reset per-bone spring state
        foreach (var bg in this.boneGroups.Values) bg.Reset();
        this.boneGroups.Clear();
        foreach (var hc in this.hairChains.Values) hc.Reset();
        this.hairChains.Clear();
        // Reset wing/fur/tail/animal bone state
        foreach (var wp in this.wingPairs.Values)   wp.Reset();
        this.wingPairs.Clear();
        foreach (var fc in this.furChains.Values)    fc.Reset();
        this.furChains.Clear();
        foreach (var tc in this.tailChains.Values)   tc.Reset();
        this.tailChains.Clear();
        foreach (var ab in this.animalBones.Values)  ab.Reset();
        this.animalBones.Clear();
        this.grassBendDisplacement = Vector2.Zero;
        this.grassBendVelocity = Vector2.Zero;
        this.hitstopTicksRemaining = 0;
        this.lastPlayerHealth = -1;
        this.wasSwimming = false;
        this.waterEmergenceTicksRemaining = 0;
        this.wasEating = false;
        this.wasLightning = false;
        this.lastSkillLevelSum = -1;
        this.levelUpBounceTicksRemaining = 0;
        this.dragonRagdollCooldown.Clear();
        this.idleCycleStep.Clear();
        this.wasBobberInAir = false;
        this.wasFishBiting = false;
        this.wasFishCaught = false;
        this.typedParticles.Clear();
        this.prevTreeTiles.Clear();
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

        // Boost from any detected weather mods
        this.ReadWeatherModBoosts(ref wind);
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

    // ── Weather mod detection ─────────────────────────────────────────────────

    /// <summary>
    /// Detects which weather mods are present and logs them.
    /// Called once during Entry() after all individual mod detections.
    /// Results stored in bool fields used by ReadWeatherModBoosts().
    /// </summary>
    private void DetectWeatherMods(IModHelper helper)
    {
        var detected = new List<string>();

        this.moreRainLoaded = helper.ModRegistry.IsLoaded("cat.morerain")
            || helper.ModRegistry.IsLoaded("MoreRain")
            || helper.ModRegistry.IsLoaded("Satozaki.MoreRain");
        if (this.moreRainLoaded) detected.Add("More Rain");

        this.climateOfFerngillLoaded = helper.ModRegistry.IsLoaded("KoihimeNakamura.ClimateOfFerngill")
            || helper.ModRegistry.IsLoaded("KoihimeNakamura.WeatherControl")
            || helper.ModRegistry.IsLoaded("Satozaki.ClimateControl")
            || helper.ModRegistry.IsLoaded("ClimateOfFerngill");
        if (this.climateOfFerngillLoaded) detected.Add("Climate of Ferngill");

        this.windEffectsLoaded = helper.ModRegistry.IsLoaded("aedenthorn.WindEffects")
            || helper.ModRegistry.IsLoaded("windeffects")
            || helper.ModRegistry.IsLoaded("aedenthorn.wind");
        if (this.windEffectsLoaded) detected.Add("Wind Effects");

        this.cloudySkiesLoaded = helper.ModRegistry.IsLoaded("tlitookilakin.CloudySkies")
            || helper.ModRegistry.IsLoaded("CloudySkies")
            || helper.ModRegistry.IsLoaded("tlitookilakin.skies");
        if (this.cloudySkiesLoaded) detected.Add("Cloudy Skies");

        this.sveWeatherLoaded = helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
            || helper.ModRegistry.IsLoaded("FlashShifter.SVECode")
            || helper.ModRegistry.IsLoaded("MonsoonalMoth.DruidMod");
        if (this.sveWeatherLoaded) detected.Add("SVE / Druid weather");

        this.extremeWeatherLoaded = helper.ModRegistry.IsLoaded("shekurika.ExtremeWeather")
            || helper.ModRegistry.IsLoaded("ExtremeWeather")
            || helper.ModRegistry.IsLoaded("Satozaki.weathervane")
            || helper.ModRegistry.IsLoaded("weathervane");
        if (this.extremeWeatherLoaded) detected.Add("Extreme Weather");

        this.detectedWeatherMods = detected.Count > 0 ? string.Join(", ", detected) : "none";

        if (detected.Count > 0)
        {
            this.Monitor.Log($"Weather mods detected: {this.detectedWeatherMods} — physics will react to their weather states.", LogLevel.Info);
        }
    }

    /// <summary>
    /// Detect installed monster / creature content mods and log a summary.
    /// The flags are used by SimulateMonsterBody to apply humanoid body-physics overlays
    /// to creature-replacement sprites from those mods (nude monster variants, beast-girls,
    /// exotic creature packs, etc.).
    /// </summary>
    private void DetectMonsterMods(IModHelper helper)
    {
        var detected = new List<string>();

        // ── Nude / exposed monster sprite replacers ───────────────────────────
        this.nudeMonsterModLoaded =
            helper.ModRegistry.IsLoaded("NudeMonsters") ||
            helper.ModRegistry.IsLoaded("Nude.Monsters") ||
            helper.ModRegistry.IsLoaded("DustBeauty.NudeMonsters") ||
            helper.ModRegistry.IsLoaded("naked.monsters") ||
            helper.ModRegistry.IsLoaded("monster.nude") ||
            helper.ModRegistry.IsLoaded("SV.NudeMonsters") ||
            helper.ModRegistry.IsLoaded("nsfw.monsters");
        if (this.nudeMonsterModLoaded) detected.Add("Nude Monsters");

        // ── Milky of Myths — beast-girl/succubus monster replacers ───────────
        this.milkyOfMythsLoaded =
            helper.ModRegistry.IsLoaded("MilkyOfMyths") ||
            helper.ModRegistry.IsLoaded("Milky.Myths") ||
            helper.ModRegistry.IsLoaded("milky.of.myths") ||
            helper.ModRegistry.IsLoaded("MilkyMythsMod") ||
            helper.ModRegistry.IsLoaded("milkymyths");
        if (this.milkyOfMythsLoaded) detected.Add("Milky of Myths");

        // ── Monsters Anonymous & creature expansion packs ─────────────────────
        this.monstersAnonymousLoaded =
            helper.ModRegistry.IsLoaded("MonstersAnonymous") ||
            helper.ModRegistry.IsLoaded("monsters.anonymous") ||
            helper.ModRegistry.IsLoaded("MonsterExpansion") ||
            helper.ModRegistry.IsLoaded("CreatureExpansion");
        if (this.monstersAnonymousLoaded) detected.Add("Monsters Anonymous");

        // ── Generic extra-monster packs (Creatures and Cuties, etc.) ─────────
        this.extraMonsterModLoaded =
            helper.ModRegistry.IsLoaded("CreaturesAndCuties") ||
            helper.ModRegistry.IsLoaded("creatures.and.cuties") ||
            helper.ModRegistry.IsLoaded("MoreMonsters") ||
            helper.ModRegistry.IsLoaded("more.monsters") ||
            helper.ModRegistry.IsLoaded("ExtraCreatures") ||
            helper.ModRegistry.IsLoaded("PokemonMod") ||
            helper.ModRegistry.IsLoaded("StardewPokemon");
        if (this.extraMonsterModLoaded) detected.Add("Extra Creatures/Cuties");

        this.detectedMonsterMods = detected.Count > 0 ? string.Join(", ", detected) : "none";
        if (detected.Count > 0)
        {
            this.Monitor.Log(
                $"Monster content mods detected: {this.detectedMonsterMods} — " +
                "body/fur/wing physics overlays will apply to modded creature sprites.",
                LogLevel.Info);
        }
    }

    /// <summary>
    /// After vanilla wind/rain/snow has been set, apply boosts from any active weather mods.
    /// Uses safe reflection for 1.5/1.6 cross-version fields (e.g. hasGreenRain).
    /// </summary>
    private void ReadWeatherModBoosts(ref float wind)
    {
        // More Rain / Climate of Ferngill: heavier rain on rainy days
        if ((this.moreRainLoaded || this.climateOfFerngillLoaded) && Game1.isRaining)
        {
            wind = Math.Max(wind, 0.75f);
            this.currentRainStrength = Math.Max(this.currentRainStrength, 0.7f);
        }

        // Wind Effects mod: try to read its wind strength value via reflection
        if (this.windEffectsLoaded)
        {
            this.TryReadWindEffectsStrength(ref wind);
        }

        // SVE / Druid: thundersnow → max storm physics
        if (this.sveWeatherLoaded && Game1.isSnowing && Game1.isLightning)
        {
            wind = Math.Max(wind, 1.2f);
            this.currentSnowStrength = Math.Max(this.currentSnowStrength, 0.8f);
        }

        // Extreme weather: amplify any existing storm conditions
        if (this.extremeWeatherLoaded && Game1.isRaining && Game1.isLightning)
        {
            wind = Math.Max(wind, 1.3f);
            this.currentRainStrength = Math.Max(this.currentRainStrength, 0.95f);
        }

        // Cloudy Skies: moderate wind boost on overcast days (no rain detection needed)
        if (this.cloudySkiesLoaded && !Game1.isRaining && !Game1.isSnowing)
        {
            wind = Math.Max(wind, 0.30f);
        }

        // Green rain (Stardew 1.6 only — hasGreenRain field via safe reflection)
        this.TryApplyGreenRainBoost(ref wind);
    }

    /// <summary>
    /// Tries to read aedenthorn.WindEffects current wind strength via reflection.
    /// Safe to call on any game version — returns without effect if field not found.
    /// </summary>
    private void TryReadWindEffectsStrength(ref float wind)
    {
        try
        {
            // WindEffects stores wind in a static field named "windStrength" or "wind"
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Contains("WindEffects", StringComparison.OrdinalIgnoreCase) == true);
            if (assembly is null) return;

            var modType = assembly.GetTypes()
                .FirstOrDefault(t => t.Name.Contains("WindEffects") || t.Name.Contains("ModEntry"));
            if (modType is null) return;

            var field = modType.GetField("windStrength",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?? modType.GetField("wind",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (field?.GetValue(null) is float w && w > wind)
            {
                wind = Math.Clamp(w, 0f, 1.5f);
            }
        }
        catch
        {
            // Reflection may fail — silently fall back to vanilla wind
        }
    }

    /// <summary>
    /// Green rain is a Stardew 1.6-only feature. Reads Game1.locationContext or the
    /// location's hasGreenRain property via reflection so the mod stays compatible
    /// with both 1.5.6 (field absent) and 1.6.x (field present).
    /// </summary>
    private void TryApplyGreenRainBoost(ref float wind)
    {
        try
        {
            var locType = Game1.currentLocation?.GetType();
            if (locType is null) return;

            var prop = locType.GetProperty("hasGreenRain",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? locType.GetProperty("IsGreenRain",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (prop?.GetValue(Game1.currentLocation) is bool greenRain && greenRain)
            {
                // Green rain: magical heavy downpour + gentle mystical wind
                wind = Math.Max(wind, 0.55f);
                this.currentRainStrength = Math.Max(this.currentRainStrength, 0.75f);
            }
        }
        catch
        {
            // 1.5.6 doesn't have this property — silently ignore
        }
    }

    // ── Per-profile cycling idle impulses ─────────────────────────────────────

    /// <summary>
    /// Returns the body impulse for step N of the 8-step feminine idle cycle.
    /// Each step is a distinct movement: hip roll, chest pop, sashay, curtsy dip,
    /// arm reach, weight shift, slow turn, shimmy. Scaled by strength.
    /// </summary>
    private static Vector2 GetFeminineIdleImpulse(int step, float strength)
    {
        return step switch
        {
            0 => new Vector2(-0.055f, 0.020f) * strength,  // hip roll right
            1 => new Vector2(0f,     -0.040f) * strength,  // chest pop up
            2 => new Vector2(0.060f,  0.015f) * strength,  // sashay left
            3 => new Vector2(-0.060f, 0.015f) * strength,  // sashay right (pair)
            4 => new Vector2(0f,      0.055f) * strength,  // curtsy dip: downward lean
            5 => new Vector2(-0.030f,-0.025f) * strength,  // arm reach left-up
            6 => new Vector2(0.030f,  0.030f) * strength,  // weight shift forward
            7 => new Vector2(0f,     -0.020f) * strength,  // subtle torso uplift / shimmy
            _ => Vector2.Zero
        };
    }

    /// <summary>
    /// Returns the body impulse for step N of the 8-step masculine idle cycle.
    /// Movements: chest puff, shoulder roll L, shoulder roll R, wide bounce, lean-back,
    /// power stomp, arm flex, side stretch. More vertical/grounded than feminine.
    /// </summary>
    private static Vector2 GetMasculineIdleImpulse(int step, float strength)
    {
        return step switch
        {
            0 => new Vector2(0f,     -0.050f) * strength,  // chest puff up
            1 => new Vector2(-0.045f, 0.015f) * strength,  // shoulder roll left
            2 => new Vector2(0.045f,  0.015f) * strength,  // shoulder roll right
            3 => new Vector2(0f,      0.060f) * strength,  // wide-stance bounce down
            4 => new Vector2(0f,      0.030f) * strength,  // lean-back weight settle
            5 => new Vector2(0f,      0.070f) * strength,  // power stomp (vertical)
            6 => new Vector2(-0.035f,-0.010f) * strength,  // arm flex left
            7 => new Vector2(0.025f, -0.015f) * strength,  // side stretch right
            _ => Vector2.Zero
        };
    }

    /// <summary>
    /// Returns the body impulse for step N of the 8-step androgynous idle cycle.
    /// Mixed moves: neutral sway, gentle lean, slow rock, arm stretch, bob.
    /// </summary>
    private static Vector2 GetAndrogynousIdleImpulse(int step, float strength)
    {
        return step switch
        {
            0 => new Vector2(-0.030f, 0.020f) * strength,  // gentle sway left
            1 => new Vector2(0.030f,  0.020f) * strength,  // gentle sway right
            2 => new Vector2(0f,     -0.035f) * strength,  // soft upward lift
            3 => new Vector2(0f,      0.040f) * strength,  // soft settle down
            4 => new Vector2(-0.025f,-0.015f) * strength,  // diagonal lean left-up
            5 => new Vector2(0.025f, -0.015f) * strength,  // diagonal lean right-up
            6 => new Vector2(0f,      0.025f) * strength,  // slow downward rock
            7 => new Vector2(0f,     -0.018f) * strength,  // micro uplift / breathing
            _ => Vector2.Zero
        };
    }

    // ── Monster idle physics ──────────────────────────────────────────────────

    /// <summary>
    /// Applies archetype-specific idle impulses to a stationary monster.
    /// Each archetype has a unique ambient animation: Slime pulses, Bat wing-twitches,
    /// Worm peristaltic waves, Bug constant buzz, Furry tail wag, Skeleton bone rattle,
    /// Dragon breathing + wing flutter, Elemental sinusoidal energy pulse.
    /// Only fires when the monster is roughly stationary (velocity small).
    /// </summary>
    private void SimulateMonsterIdle(NPC monster, MonsterPhysicsArchetype archetype)
    {
        if (!this.config.EnableMonsterBodyPhysics) return;

        var key      = this.GetCharacterKey(monster);
        var strength = this.config.MonsterArchetypeStrength;
        var t        = Game1.ticks;

        var existing = this.monsterBodyImpulse.TryGetValue(key, out var mi) ? mi : Vector2.Zero;
        Vector2 idle;

        switch (archetype)
        {
            case MonsterPhysicsArchetype.Slime:
                // Rhythmic compression-expansion pulse: Y-dominant, slow period ~40 ticks
                idle = new Vector2(
                    (float)Math.Sin(t * 0.08f) * 0.018f,
                    (float)Math.Sin(t * 0.16f) * 0.030f) * strength;
                break;

            case MonsterPhysicsArchetype.Bat:
                // Wing-fold twitch: fast lateral micro-flap at ~15 tick intervals
                idle = (t % 15 < 3)
                    ? new Vector2((Game1.random.NextSingle() - 0.5f) * 0.04f, -0.015f) * strength
                    : Vector2.Zero;
                break;

            case MonsterPhysicsArchetype.Worm:
                // Peristaltic wave: sinusoidal Y-dominant with slight phase shift per key
                var phase = (key & 0x1F) * 0.2f;
                idle = new Vector2(
                    (float)Math.Sin(t * 0.12f + phase) * 0.012f,
                    (float)Math.Cos(t * 0.09f + phase) * 0.025f) * strength;
                break;

            case MonsterPhysicsArchetype.FlyingBug:
                // Constant high-frequency micro-oscillation (wing buzz)
                idle = new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.022f,
                    (Game1.random.NextSingle() - 0.5f) * 0.018f) * strength;
                break;

            case MonsterPhysicsArchetype.Furry:
                // Slow tail-wag side-to-side + occasional head toss
                idle = new Vector2((float)Math.Sin(t * 0.06f) * 0.020f, 0f) * strength;
                if (t % 80 < 5) idle += new Vector2(0f, -0.025f) * strength; // head toss
                break;

            case MonsterPhysicsArchetype.Skeleton:
                // Bone rattle: random sharp micro-jitter every ~25 ticks
                idle = (t % 25 < 3)
                    ? new Vector2((Game1.random.NextSingle() - 0.5f) * 0.05f,
                                  (Game1.random.NextSingle() - 0.5f) * 0.05f) * strength
                    : Vector2.Zero;
                break;

            case MonsterPhysicsArchetype.Dragon:
            {
                // Deep breathing (slow Y) + wing flutter (lateral ripple at ~35 ticks)
                var dStr = strength * this.config.DragonPhysicsStrength;
                idle = new Vector2(0f, (float)Math.Sin(t * 0.04f) * 0.020f) * dStr;
                if (t % 35 < 4)
                {
                    idle += new Vector2((Game1.random.NextSingle() - 0.5f) * 0.035f, -0.010f) * dStr;
                }
                break;
            }

            case MonsterPhysicsArchetype.Elemental:
                // Sinusoidal energy pulse: Lissajous figure for magical shimmering effect
                idle = new Vector2(
                    (float)Math.Sin(t * 0.20f) * 0.020f,
                    (float)Math.Cos(t * 0.17f) * 0.020f) * strength;
                break;

            default: // Generic
                // Ambient sway: slow gentle rock
                idle = new Vector2((float)Math.Sin(t * 0.05f) * 0.010f, 0f) * strength;
                break;
        }

        if (idle != Vector2.Zero)
        {
            this.monsterBodyImpulse[key] = (existing + idle) * 0.90f;
        }
    }

    // ── Farm animal idle physics ──────────────────────────────────────────────

    /// <summary>
    /// Applies idle physics to a stationary farm animal based on animal type.
    /// Chicken/Duck = head peck, Rabbit = ear twitch, Cow/Goat/Sheep = tail swish + head bob,
    /// Pig = sniff-bob, Ostrich = neck sway. All animals ambient-sway when no type matches.
    /// Only fires when nearly stationary (low velocity).
    /// </summary>
    private void SimulateFarmAnimalIdle(StardewValley.Characters.FarmAnimal animal)
    {
        if (!this.config.EnableFarmAnimalPhysics) return;

        var key      = RuntimeHelpers.GetHashCode(animal);
        var strength = this.config.FarmAnimalPhysicsStrength;
        var t        = Game1.ticks + (key & 0x3F); // per-animal phase offset
        var name     = animal.type.Value ?? string.Empty;

        // Farm animal idle impulses feed into bodyImpulse (same dict that SimulateFarmAnimalBody uses)
        // so they are actually visible through the render path.
        var existing = this.bodyImpulse.TryGetValue(key, out var bi2) ? bi2 : Vector2.Zero;
        Vector2 idle;

        if (ContainsAny(name, "chicken", "hen", "rooster", "duck", "dinosaur"))
        {
            // Head peck: sharp downward dip every ~40 ticks
            idle = (t % 40 < 4) ? new Vector2(0f, 0.030f) * strength : Vector2.Zero;
        }
        else if (ContainsAny(name, "rabbit", "bunny"))
        {
            // Ear twitch: fast lateral micro-flick every ~55 ticks
            idle = (t % 55 < 3)
                ? new Vector2((Game1.random.NextSingle() - 0.5f) * 0.035f, -0.008f) * strength
                : Vector2.Zero;
        }
        else if (ContainsAny(name, "cow", "goat", "sheep", "yak"))
        {
            // Tail swish (lateral sine) + occasional slow head bob
            idle = new Vector2((float)Math.Sin(t * 0.07f) * 0.015f, 0f) * strength;
            if (t % 90 < 8) idle += new Vector2(0f, (float)Math.Sin(t * 0.3f) * 0.020f) * strength;
        }
        else if (ContainsAny(name, "pig", "boar"))
        {
            // Sniff-bob: small Y-oscillation like rooting in dirt
            idle = new Vector2(0f, (float)Math.Sin(t * 0.18f) * 0.018f) * strength;
        }
        else if (ContainsAny(name, "ostrich", "emu"))
        {
            // Neck sway: exaggerated lateral sine for long neck
            idle = new Vector2((float)Math.Sin(t * 0.10f) * 0.030f, 0f) * strength;
        }
        else
        {
            // Generic ambient sway for any mod-added animal
            idle = new Vector2((float)Math.Sin(t * 0.06f) * 0.010f, 0f) * strength;
        }

        if (idle != Vector2.Zero)
        {
            this.bodyImpulse[key] = (existing + idle) * 0.88f;
        }
    }

    // ── Run-step impulse ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies step-rhythm body, hair, and clothing impulses during running to keep
    /// physics visibly active even when moving fast.
    /// Feminine: hip-led lateral bounce every other step.
    /// Masculine: heel-strike vertical bounce.
    /// Androgynous: mixed small bounce.
    /// Fires when speed > 2.5 and on step cadence (ticks mod step period).
    /// </summary>
    private void SimulateRunStepImpulse(Character character, Vector2 velocity, BodyProfileType profile)
    {
        if (!this.config.EnableBodyPhysics) return;

        var speed = velocity.Length();
        if (speed < 2.5f) return;

        var key      = this.GetCharacterKey(character);
        var t        = Game1.ticks;
        // Step period scales inversely with speed: faster = more frequent steps
        var stepPeriod = Math.Max(8, (int)(22f / speed));

        if (t % stepPeriod != key % stepPeriod) return;

        var baseStr = profile switch
        {
            BodyProfileType.Feminine   => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength) * 0.5f,
            BodyProfileType.Masculine  => (this.config.MaleButtStrength     + this.config.MaleThighStrength)  * 0.5f,
            _                          => 0.35f
        };

        // Alternate step side (left/right) by checking tick parity offset per character
        var leftStep = ((t / stepPeriod) + (key & 1)) % 2 == 0;

        Vector2 stepImpulse = profile switch
        {
            BodyProfileType.Feminine => new Vector2(leftStep ? -0.045f : 0.045f, -0.025f) * baseStr,
            BodyProfileType.Masculine => new Vector2(0f, 0.060f) * baseStr,  // heel-strike: downward
            _ => new Vector2(leftStep ? -0.020f : 0.020f, 0.020f) * baseStr
        };

        // Scale by speed factor — sprinting produces more jiggle than walking
        var speedFactor = Math.Clamp(speed / 5f, 0.5f, 1.5f);
        stepImpulse *= speedFactor;

        var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = existing + stepImpulse;

        if (this.config.EnableHairPhysics)
        {
            var hExist = this.hairImpulse.TryGetValue(key, out var hi) ? hi : Vector2.Zero;
            this.hairImpulse[key] = hExist + stepImpulse * (this.config.HairStrength * 0.55f);
        }

        if (this.config.EnableClothingFlowPhysics)
        {
            var flowType  = this.GetClothingFlowType(character);
            var clothScale = flowType == ClothingFlowType.Flowy ? 0.65f : 0.30f;
            var cExist    = this.clothingImpulse.TryGetValue(key, out var ci) ? ci : Vector2.Zero;
            this.clothingImpulse[key] = cExist + stepImpulse * (clothScale * this.config.ClothingFlowStrength);
        }
    }

    // ── Combat hit VFX ────────────────────────────────────────────────────────

    /// <summary>
    /// Detects what material was hit by the current tool swing and applies appropriate VFX:
    ///   Stone/metal nearby → spark particle burst + swing-back hitstop
    ///   Slime monster nearby → green spray particles + extra slime jiggle impulse
    ///   Humanoid / animal nearby → blood splatter particles
    /// Called from OnButtonPressed when the player uses a tool/weapon.
    /// All VFX are purely cosmetic — no damage, no gameplay state changes.
    /// </summary>
    private void ApplyCombatHitVfx(Tool? tool, Farmer player)
    {
        if (Game1.currentLocation is null || tool is null) return;

        // Only apply combat VFX for actual combat/mining tools — skip watering cans, rods, hoes etc.
        if (tool is not MeleeWeapon && tool is not Pickaxe && tool is not Axe)
        {
            return;
        }

        var playerPos  = player.Position;
        var swingRange = 128f; // ~2 tiles

        // ── Slime spray ───────────────────────────────────────────────────────
        if (this.config.EnableSlimeSprayEffects)
        {
            foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
            {
                if (Vector2.Distance(monster.Position, playerPos) > swingRange) continue;
                var archetype = this.DetectMonsterArchetype(monster);
                if (archetype != MonsterPhysicsArchetype.Slime) continue;

                // Spray 4–8 green debris particles outward from the slime
                var sprayCount = (int)(4 + Game1.random.Next(5));
                for (int i = 0; i < sprayCount; i++)
                {
                    var sprayAngle = Game1.random.NextSingle() * MathF.PI * 2f;
                    var sprayDist  = 20f + Game1.random.NextSingle() * 40f;
                    var sprayPos   = monster.Position + new Vector2(
                        MathF.Cos(sprayAngle) * sprayDist,
                        MathF.Sin(sprayAngle) * sprayDist);
                    Game1.createRadialDebris(Game1.currentLocation, 6, (int)(sprayPos.X / 64), (int)(sprayPos.Y / 64), 1, false);
                }

                // Extra jiggle on the slime so it goes extra wobbly from the hit
                var slimeKey = this.GetCharacterKey(monster);
                var slimeImpulse = this.monsterBodyImpulse.TryGetValue(slimeKey, out var si) ? si : Vector2.Zero;
                slimeImpulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.30f,
                    (Game1.random.NextSingle() - 0.5f) * 0.30f) * this.config.MonsterArchetypeStrength;
                this.monsterBodyImpulse[slimeKey] = slimeImpulse;
            }
        }

        // ── Blood splatter ────────────────────────────────────────────────────
        if (this.config.EnableBloodSplatterEffects)
        {
            // Only monsters take blood splatter — never friendly NPCs
            foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
            {
                if (Vector2.Distance(monster.Position, playerPos) > swingRange) continue;
                this.SpawnBloodParticles(monster.Position, this.config.BloodSplatterIntensity);
            }

            // Farm animals in range
            foreach (var animal in EnumerateFarmAnimals(Game1.currentLocation))
            {
                if (Vector2.Distance(animal.Position, playerPos) > swingRange) continue;
                this.SpawnBloodParticles(animal.Position, this.config.BloodSplatterIntensity * 0.6f);
            }
        }

        // ── Spark VFX + tool swing-back hitstop ───────────────────────────────
        if (this.config.EnableSparkEffects || this.config.EnableToolCollisionHitstop)
        {
            var hitHard = this.DetectHardSurfaceNear(player);
            if (hitHard)
            {
                if (this.config.EnableSparkEffects)
                {
                    var sparkCount = tool is Pickaxe ? 8 : (tool is MeleeWeapon ? 5 : 4);
                    for (int i = 0; i < sparkCount; i++)
                    {
                        var sAngle = Game1.random.NextSingle() * MathF.PI * 2f;
                        var sDist  = 15f + Game1.random.NextSingle() * 25f;
                        var sPos   = playerPos + new Vector2(MathF.Cos(sAngle) * sDist, MathF.Sin(sAngle) * sDist);
                        // Use debris type 10 (yellow/orange sparkle) for sparks
                        Game1.createRadialDebris(Game1.currentLocation, 10, (int)(sPos.X / 64), (int)(sPos.Y / 64), 1, false);
                    }
                }

                if (this.config.EnableToolCollisionHitstop)
                {
                    // Swing-back: strong reverse impulse in facing direction (tool bounces back)
                    var swingBackDir = Game1.player.FacingDirection switch
                    {
                        0 => new Vector2(0f,  1f),   // facing up    → swing-back downward
                        1 => new Vector2(-1f, 0f),   // facing right → swing-back left
                        2 => new Vector2(0f, -1f),   // facing down  → swing-back upward
                        3 => new Vector2(1f,  0f),   // facing left  → swing-back right
                        _ => Vector2.Zero
                    };

                    var playerKey = this.GetCharacterKey(player);
                    var existBody = this.bodyImpulse.TryGetValue(playerKey, out var pb) ? pb : Vector2.Zero;
                    this.bodyImpulse[playerKey] = existBody + swingBackDir * 0.35f;

                    // Extra hitstop frames on hard collision
                    if (this.config.EnableHitstopEffect)
                    {
                        this.hitstopTicksRemaining = Math.Max(this.hitstopTicksRemaining, 5);
                    }
                }

                // Typed stone/ore debris particles — material-matched to what was struck
                if (this.config.EnableTypedPhysicsDebris)
                {
                    var stoneKind = (tool is Pickaxe) ? PhysicsParticleKind.StoneChunk : PhysicsParticleKind.OreChunk;
                    this.SpawnTypedDebris(playerPos, stoneKind,        3 + Game1.random.Next(4), 1.5f);
                    this.SpawnTypedDebris(playerPos, PhysicsParticleKind.Sawdust, 4 + Game1.random.Next(5), 0.7f);
                }
            }
        }

        // ── Wood shatter VFX ──────────────────────────────────────────────────
        if (this.config.EnableWoodShatterEffects && (tool is Axe || tool is MeleeWeapon))
        {
            var hitWood = this.DetectWoodSurfaceNear(player);
            if (hitWood)
            {
                this.ApplyWoodShatterVfx(playerPos);

                // Swing-stop: tool hits wood = brief hitstop (wood absorbs impact)
                if (this.config.EnableToolCollisionHitstop && this.config.EnableHitstopEffect)
                {
                    this.hitstopTicksRemaining = Math.Max(this.hitstopTicksRemaining, 3);
                }

                // Reverse body impulse (tool bounces back slightly from wood impact)
                if (this.config.EnableToolCollisionHitstop)
                {
                    var woodBackDir = Game1.player.FacingDirection switch
                    {
                        0 => new Vector2(0f,  0.8f),
                        1 => new Vector2(-0.8f, 0f),
                        2 => new Vector2(0f, -0.8f),
                        3 => new Vector2(0.8f, 0f),
                        _ => Vector2.Zero
                    };
                    var pk    = this.GetCharacterKey(player);
                    var existB = this.bodyImpulse.TryGetValue(pk, out var eb2) ? eb2 : Vector2.Zero;
                    this.bodyImpulse[pk] = existB + woodBackDir * 0.22f;
                }

                // Typed wood debris — splinters and sawdust that arc outward and scatter
                if (this.config.EnableTypedPhysicsDebris)
                {
                    this.SpawnTypedDebris(playerPos, PhysicsParticleKind.WoodSplinter, 3 + Game1.random.Next(4), 1.3f);
                    this.SpawnTypedDebris(playerPos, PhysicsParticleKind.Sawdust,      5 + Game1.random.Next(5), 0.7f);
                }
            }
        }
    }

    /// <summary>
    /// Spawns red radial debris particles to simulate blood splatter at the given world position.
    /// Intensity 1.0 = 4–6 particles. Purely cosmetic.
    /// </summary>
    private void SpawnBloodParticles(Vector2 worldPos, float intensity)
    {
        if (Game1.currentLocation is null) return;
        var count = Math.Max(1, (int)(4 * intensity) + Game1.random.Next(3));
        for (int i = 0; i < count; i++)
        {
            var angle = Game1.random.NextSingle() * MathF.PI * 2f;
            var dist  = 10f + Game1.random.NextSingle() * 30f * intensity;
            var pos   = worldPos + new Vector2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            // Debris type 8 = red/berry-colored debris, best approximation for blood
            Game1.createRadialDebris(Game1.currentLocation, 8, (int)(pos.X / 64), (int)(pos.Y / 64), 1, false);
        }
    }

    /// <summary>
    /// Returns true if there is a hard surface (stone wall, metal furniture, large rock object,
    /// armored monster) within ~1.5 tiles of the player in their facing direction.
    /// Used to trigger spark VFX and tool swing-back hitstop.
    /// Checks: resource clumps, large stone/metal objects, armored monsters.
    /// </summary>
    private bool DetectHardSurfaceNear(Farmer player)
    {
        if (Game1.currentLocation is null) return false;

        var facingOffset = player.FacingDirection switch
        {
            0 => new Vector2(0f, -64f),
            1 => new Vector2(64f, 0f),
            2 => new Vector2(0f,  64f),
            3 => new Vector2(-64f, 0f),
            _ => Vector2.Zero
        };
        var checkPos = player.Position + facingOffset;
        var checkTile = checkPos / 64f;

        // Check resource clumps (boulders, stumps) in the location
        foreach (var clump in Game1.currentLocation.resourceClumps)
        {
            if (Vector2.Distance(clump.Tile * 64f, checkPos) < 96f)
            {
                return true;
            }
        }

        // Check objects at the facing tile (stones, metal furniture, large items)
        var tileKey = new Vector2((int)checkTile.X, (int)checkTile.Y);
        if (Game1.currentLocation.objects.TryGetValue(tileKey, out var obj))
        {
            var objName = obj.Name ?? string.Empty;
            if (ContainsAny(objName, "stone", "rock", "ore", "metal", "iron", "copper", "iridium", "coal",
                                     "boulder", "wall", "chest", "furnace", "anvil", "forge"))
            {
                return true;
            }
        }

        // Check Furniture objects at the facing tile (metal shelves, stone tiles, anvils, forges etc.)
        // Furniture is a separate collection in the location and is not covered by location.objects.
        foreach (var furniture in Game1.currentLocation.furniture)
        {
            if (furniture is null) continue;
            var furniturePos = furniture.TileLocation * 64f;
            if (Vector2.Distance(furniturePos, checkPos) > 128f) continue;
            var fname = furniture.Name ?? string.Empty;
            if (ContainsAny(fname, "stone", "metal", "iron", "forge", "anvil", "rock", "steel", "copper",
                                   "wall", "slate", "ore", "cobble", "brick", "granite"))
            {
                return true;
            }
        }

        // Check armored monsters nearby
        foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
        {
            if (Vector2.Distance(monster.Position, player.Position) > 100f) continue;
            var arch = this.DetectMonsterArchetype(monster);
            if (arch == MonsterPhysicsArchetype.Skeleton) return true; // armored/hard
        }

        return false;
    }

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

        // Clothing modifier: per-region dampening (hat/shoes do NOT reduce breast physics)
        float breastMult, lowerBodyMult;
        if (this.config.EnableClothingPhysicsModifier && character is Farmer cfm)
        {
            breastMult    = this.GetBreastClothingMult(cfm);
            lowerBodyMult = this.GetLowerBodyClothingMult(cfm);
        }
        else
        {
            breastMult = lowerBodyMult = 1f;
        }

        // Gender-swap detection: flip profile if sprite texture signals a swap
        if (this.config.EnableGenderSwapDetection)
        {
            var swapped = this.detector.TryGetGenderSwappedProfile(character, profile);
            if (swapped.HasValue) profile = swapped.Value;
        }

        // Base inertia lag: body resists direction change (mass effect).
        // Uses the average of both region mults — a neutral whole-body inertia effect.
        var inertiaClothingMult = (breastMult + lowerBodyMult) * 0.5f;
        impulse += new Vector2(-velocity.X, -velocity.Y) * ((0.03f + (baseStrength * 0.04f)) * inertiaClothingMult);

        // ── Directional body physics ──────────────────────────────────────────
        // Each facing direction produces a distinct jiggle signature for breast/butt/groin.
        // EnableDirectionalBodyBoost gates this whole block.
        if (this.config.EnableDirectionalBodyBoost && velocity.LengthSquared() > 0.03f)
        {
            var facing = character.FacingDirection;
            var speed  = velocity.Length();

            if (profile == BodyProfileType.Feminine)
            {
                var bStr  = this.config.FemaleBreastStrength * breastMult;     // per-region: breast only
                var buStr = this.config.FemaleButtStrength   * lowerBodyMult;  // per-region: lower body
                var thStr = this.config.FemaleThighStrength  * lowerBodyMult;

                switch (facing)
                {
                    case 0: // North — walking up/away: breasts sway outward to sides, inward snap
                        // Lateral outward sweep: each step pushes breasts to sides then they snap back
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * 2f * bStr * 0.075f,  // large lateral
                            (Game1.random.NextSingle() - 0.5f)       * bStr * 0.025f); // tiny Y jiggle
                        // Periodic outward snap at step cadence
                        if (Game1.ticks % 14 == key % 14)
                        {
                            var sign = ((Game1.ticks / 14) % 2 == 0) ? 1f : -1f;
                            impulse += new Vector2(sign * bStr * 0.06f * speed, 0f);
                        }
                        // Butt also prominent facing north (back of character visible)
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * buStr * 0.04f,
                            Math.Abs(velocity.Y) * buStr * 1.6f); // strong up-down butt jiggle
                        break;

                    case 2: // South — walking toward camera: breasts bounce up-down, outward-inward
                        // Strong vertical jello bounce: up on foot-down, outward flare, then spring back
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * bStr * 0.06f,   // slight lateral outward
                            -Math.Abs(velocity.Y) * bStr * 0.14f);               // strong upward on step
                        // Jello outward flare at step rhythm
                        if (Game1.ticks % 14 == key % 14)
                        {
                            impulse += new Vector2(
                                (Game1.random.NextSingle() - 0.5f) * bStr * 0.08f,  // outward flare
                                bStr * 0.05f * speed);  // downward settle
                        }
                        // Belly bounce toward camera — shirt-covered, use breastMult (not lowerBodyMult)
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * this.config.FemaleBellyStrength * breastMult * 0.04f,
                            -Math.Abs(velocity.Y) * this.config.FemaleBellyStrength * breastMult * 0.06f);
                        // Thigh jiggle
                        impulse += new Vector2(0f, Math.Abs(velocity.Y) * thStr * 0.04f);
                        break;

                    case 1: // East — walking right: breasts mostly up-down with slight outward-inward
                    case 3: // West — walking left
                        // Primary up-down jello bounce (visible profile)
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * bStr * 0.025f,  // tiny lateral
                            -Math.Abs(velocity.Y) * bStr * 0.12f);               // dominant up-down
                        // Periodic outward-inward at step cadence
                        if (Game1.ticks % 12 == key % 12)
                        {
                            var lateral = (facing == 1 ? 1f : -1f);
                            impulse += new Vector2(lateral * bStr * 0.03f * speed, bStr * 0.03f * speed);
                        }
                        // Thigh and butt jiggle (visible from side)
                        impulse += new Vector2(0f, Math.Abs(velocity.X) * buStr * 0.05f);
                        break;
                }

                // Always-on jello micro-randomness for feminine: ensures physics never go fully still
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * bStr * 0.022f,
                    (Game1.random.NextSingle() - 0.5f) * bStr * 0.018f);
            }
            else if (profile == BodyProfileType.Masculine)
            {
                var grStr = this.config.MaleGroinStrength * lowerBodyMult;
                var buStr = this.config.MaleButtStrength  * lowerBodyMult;

                // Male groin physics: slinky-style oscillation — side-to-side for N/S, front-back for E/W
                switch (facing)
                {
                    case 0: // North: lateral slinky side-to-side
                    case 2: // South: lateral slinky side-to-side
                        impulse += new Vector2(
                            (float)Math.Sin(Game1.ticks * 0.22f + key * 0.1f) * grStr * 0.035f,
                            (Game1.random.NextSingle() - 0.5f) * grStr * 0.010f);
                        break;

                    case 1: // East: forward-back slinky
                    case 3: // West: forward-back slinky
                        impulse += new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * grStr * 0.010f,
                            (float)Math.Sin(Game1.ticks * 0.22f + key * 0.1f) * grStr * 0.035f);
                        break;
                }

                // Butt bounce for masculine (all directions)
                impulse += new Vector2(0f, Math.Abs(velocity.Length()) * buStr * 0.04f);
            }
            else // Androgynous
            {
                impulse += new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * baseStrength * 0.02f,
                    (Game1.random.NextSingle() - 0.5f) * baseStrength * 0.02f);
            }
        }

        // Continuous micro-activity: very small random baseline so physics never go fully dormant.
        // Simulates the constant tiny vibrations of breathing, muscle tension, and micro-movements.
        // Feminine gets a larger baseline so jello quality is always present even when standing.
        if (baseStrength > 0f)
        {
            var microScale = profile == BodyProfileType.Feminine ? 0.013f : 0.008f;
            impulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * microScale * baseStrength,
                (Game1.random.NextSingle() - 0.5f) * microScale * 0.75f * baseStrength);
        }

        // Swimming: water resistance — stronger movement wave but rapid oscillations are damped
        if (character is Farmer swimmingFarmer && swimmingFarmer.swimming.Value)
        {
            impulse += new Vector2(-velocity.X, -velocity.Y) * (0.05f + baseStrength * 0.06f);
            impulse *= 0.82f;
            this.bodyImpulse[key] = impulse;
            // Also drive spring bones (water still moves body parts around)
            this.StepBoneGroup(key, profile, impulse * 0.5f, breastMult, lowerBodyMult);
            return;
        }

        // Feminine gets much slower decay = very jello-like, long lingering jiggles.
        // Masculine gets medium. Androgynous slightly bouncier than masculine.
        impulse *= profile switch
        {
            BodyProfileType.Feminine  => 0.80f, // very slow settle = ultra-jello
            BodyProfileType.Masculine => 0.86f,
            _                         => 0.84f
        };

        this.bodyImpulse[key] = impulse;

        // ── Drive per-bone spring simulation ──────────────────────────────────
        // Pass per-region mults so each bone is only dampened by clothing that covers it.
        // Hat and boots have zero effect on breast/belly (breastMult unaffected by them).
        // Shirt has zero effect on butt/thigh/groin (lowerBodyMult unaffected by it).
        this.StepBoneGroup(key, profile, impulse, breastMult, lowerBodyMult);
    }

    /// <summary>
    /// Advance (or lazily create) the BoneGroup for this character by one tick.
    /// The external force is the body-center impulse computed by SimulateBody.
    /// </summary>
    /// <summary>
    /// Advance (or lazily create) the BoneGroup for this character by one tick.
    /// The external force is the body-center impulse computed by SimulateBody.
    /// <paramref name="breastMult"/> and <paramref name="lowerBodyMult"/> are the per-region
    /// clothing dampening values — passed directly into BoneGroup.Step so each bone
    /// only feels dampening from the clothing slot that actually covers it:
    ///   breastMult    — from shirt only (hat/pants/boots have no effect on breast/belly)
    ///   lowerBodyMult — from pants+boots only (hat/shirt have no effect on butt/thigh/groin)
    /// </summary>
    private void StepBoneGroup(int key, BodyProfileType profile, Vector2 externalForce,
        float breastMult = 1f, float lowerBodyMult = 1f)
    {
        if (!this.boneGroups.TryGetValue(key, out var group))
        {
            group = new BoneGroup();
            this.boneGroups[key] = group;
        }

        group.Step(profile, externalForce,
            this.config.BoneStiffness, this.config.BoneDamping,
            this.config, breastMult, lowerBodyMult);
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

            // Visual hit flash: briefly lighten the screen to give impact feedback
            // Game1.flashAlpha is a built-in Stardew screen-flash value (0 = none, 1 = full white)
            Game1.flashAlpha = Math.Clamp(damageFactor * 0.35f, 0.1f, 0.55f);
        }
    }

    // ── Clothing physics helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns a multiplier (0.88–1.0) based on how many clothing slots the farmer has filled.
    /// More clothing = slightly dampened physics (cloth adds mass and restricts jiggle).
    /// Hat, shirt, pants, and shoes each contribute a small reduction.
    /// </summary>
    /// <summary>
    /// Per-region clothing physics multiplier for UPPER BODY (breasts, belly).
    /// Only shirt reduces breast/belly physics — hat, pants, and boots do NOT cover breasts.
    /// Returns [0.5, 1.0]: 1.0 = nude/no shirt (full physics), 0.5 = very thick shirt.
    /// When the sprite is detected as nude, always returns 1.0 regardless of equipped items.
    /// </summary>
    private float GetBreastClothingMult(Farmer farmer)
    {
        if (this.config.EnableNudePhysicsBoost && this.detector.IsNudeSprite(farmer))
        {
            return 1.0f;  // nude sprite — no dampening at all
        }

        var mult = 1.0f;
        var shirt = farmer.shirtItem.Value;
        if (shirt is not null)
        {
            // Tight/athletic clothing dampens more; loose/small tops dampen less
            var name = shirt.Name ?? string.Empty;
            if (ContainsAny(name, "tight", "sport", "athletic", "armor", "chainmail", "corset", "bodice", "plate"))
            {
                mult -= 0.18f;  // heavy shirt dampens breast physics significantly
            }
            else if (ContainsAny(name, "crop", "bikini", "swimsuit", "tube", "bra", "halter", "strapless"))
            {
                mult -= 0.06f;  // minimal coverage = minimal dampening
            }
            else
            {
                mult -= 0.10f;  // default shirt
            }
        }

        return Math.Clamp(mult, 0.5f, 1.0f);
    }

    /// <summary>
    /// Per-region clothing physics multiplier for LOWER BODY (butt, thighs, groin).
    /// Pants and boots reduce lower-body physics. Hat does NOT.
    /// Returns [0.5, 1.0].
    /// </summary>
    private float GetLowerBodyClothingMult(Farmer farmer)
    {
        if (this.config.EnableNudePhysicsBoost && this.detector.IsNudeSprite(farmer))
        {
            return 1.0f;
        }

        var mult = 1.0f;
        var pants = farmer.pantsItem.Value;
        if (pants is not null)
        {
            var name = pants.Name ?? string.Empty;
            if (ContainsAny(name, "tight", "jeans", "denim", "armor", "plate", "chainmail", "leggings", "tights"))
            {
                mult -= 0.14f;
            }
            else if (ContainsAny(name, "shorts", "bikini", "swimsuit", "thong", "briefs", "underwear"))
            {
                mult -= 0.04f;
            }
            else
            {
                mult -= 0.08f;
            }
        }
        if (farmer.boots.Value is not null)
        {
            mult -= 0.04f;  // boots reduce thigh movement very slightly
        }

        return Math.Clamp(mult, 0.5f, 1.0f);
    }

    /// <summary>Legacy single-value multiplier — kept for any code path that still needs it.</summary>
    private float GetClothingPhysicsMultiplier(Farmer farmer)
    {
        return (this.GetBreastClothingMult(farmer) + this.GetLowerBodyClothingMult(farmer)) * 0.5f;
    }

    /// <summary>
    /// Each clothing slot (hat, shirt, pants, shoes) has a configured chance to fly off during
    /// ragdoll. Removed items become pickable debris scattered near the farmer.
    /// Shirt and pants scatter close (cloth physics), boots may roll further (heavier).
    /// </summary>
    private void TryScatterClothing(Farmer farmer)
    {
        if (Game1.currentLocation is null)
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

    // ── Clothing flow physics ─────────────────────────────────────────────────

    /// <summary>
    /// Classifies whether a farmer's clothing is flowy/loose, tight/form-fitting, or default.
    /// Detection uses keyword matching on worn item names, compatible with all vanilla and
    /// modded clothing items. Flowy items trail and billow; tight items track the body closely.
    /// </summary>
    private enum ClothingFlowType { Default, Flowy, Tight }

    private ClothingFlowType GetClothingFlowType(Character character)
    {
        if (character is not Farmer farmer)
        {
            return ClothingFlowType.Default;
        }

        var shirt = farmer.shirtItem.Value?.Name ?? string.Empty;
        var pants = farmer.pantsItem.Value?.Name ?? string.Empty;
        var hat   = farmer.hat.Value?.Name       ?? string.Empty;

        var combined = shirt + " " + pants + " " + hat;

        // Flowy: dresses, robes, capes, cloaks, skirts, gowns, tunics, silk, satin, etc.
        if (ContainsAny(combined,
            "dress", "robe", "cape", "cloak", "skirt", "gown", "tunic", "flowing",
            "loose", "silk", "satin", "chiffon", "lace", "veil", "mantle", "wrap",
            "sarong", "kimono", "shawl", "poncho", "wizard", "witch", "mage", "druid",
            "noble", "scholar", "maid", "apron", "overcoat", "trench", "flowy"))
        {
            return ClothingFlowType.Flowy;
        }

        // Tight: shorts, tights, form-fitting, athletic, bikini, crop, etc.
        if (ContainsAny(combined,
            "tight", "tights", "shorts", "bikini", "crop", "sport", "athletic", "fitted",
            "leotard", "bodysuit", "spandex", "tank", "jeans", "denim", "leather",
            "armor", "chainmail", "swimsuit", "wetsuit", "compression"))
        {
            return ClothingFlowType.Tight;
        }

        return ClothingFlowType.Default;
    }

    /// <summary>
    /// Simulates a separate clothing physics layer on top of body physics.
    ///
    /// Behavior by clothing type:
    ///   Flowy (dress/robe/cape/skirt): large trailing lag, wind billowing, rain-heavy droop,
    ///     swims buoyant, longer settle time. Visually: fabric trails and swings wide.
    ///   Tight (shorts/tights/bikini): nearly tracks body, minimal extra sway, less wind effect.
    ///   Default: moderate lag and amplitude.
    ///
    /// The clothing impulse is blended into the visual offset at ~42% scale on top of the body
    /// impulse so clothing movement is visible but does not overpower or clip through body physics.
    /// All parameters scale with ClothingFlowStrength from config.
    /// Compatible with all vanilla and modded clothing — detection is name-keyword-based.
    /// </summary>
    private void SimulateClothing(Character character, Vector2 velocity, BodyProfileType profile)
    {
        if (!this.config.EnableClothingFlowPhysics || !this.config.EnableBodyPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(character);
        if (!this.clothingImpulse.TryGetValue(key, out var cImpulse))
        {
            cImpulse = Vector2.Zero;
        }

        var flowType   = this.GetClothingFlowType(character);
        var str        = this.config.ClothingFlowStrength;
        var bodyImp    = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        var isOutdoors = Game1.currentLocation?.IsOutdoors == true;

        // Per-type parameters: lag (0=slow to catch up, 1=instant), amplitude (fabric swing range)
        float lagFactor, flowAmp, decayFactor;
        switch (flowType)
        {
            case ClothingFlowType.Flowy:
                lagFactor   = 0.10f;  // clothing slowly drifts to match body impulse (heavy trail)
                flowAmp     = 1.45f;  // fabric swings wider than body
                decayFactor = 0.90f;  // slow settle = lingering billow
                break;
            case ClothingFlowType.Tight:
                lagFactor   = 0.50f;  // nearly instant tracking
                flowAmp     = 0.65f;  // barely moves beyond body
                decayFactor = 0.80f;  // fast settle
                break;
            default:
                lagFactor   = 0.25f;
                flowAmp     = 1.00f;
                decayFactor = 0.85f;
                break;
        }

        // Movement-driven trail: clothing resists direction change and trails behind
        cImpulse += new Vector2(-velocity.X, -velocity.Y) * (0.022f * str * flowAmp);

        // Clothing drifts toward body impulse with per-type lag (soft follow)
        cImpulse += (bodyImp * flowAmp - cImpulse) * (lagFactor * str);

        // Wind: flowy fabrics billow in the breeze outdoors
        if (this.config.EnableWindDetection && isOutdoors && this.currentWindStrength > 0f)
        {
            var windFactor = flowType == ClothingFlowType.Flowy ? 2.0f : 0.55f;
            cImpulse += new Vector2(this.currentWindStrength * 0.012f * windFactor * str, 0f);

            // Extra flutter for flowy clothing: small oscillation from wind catching fabric
            if (flowType == ClothingFlowType.Flowy)
            {
                var phase = key % 100 * 0.071f;
                cImpulse += new Vector2(
                    (float)Math.Sin(Game1.ticks * 0.11f + phase) * 0.006f * str * this.currentWindStrength,
                    (float)Math.Cos(Game1.ticks * 0.07f + phase) * 0.003f * str * this.currentWindStrength);
            }
        }

        // Rain: wet clothing becomes heavy and droops — the heavier the fabric, the more it droops
        if (this.currentRainStrength > 0f && isOutdoors)
        {
            var rainDroop = flowType switch
            {
                ClothingFlowType.Flowy  => this.currentRainStrength * 0.018f,  // soaked heavy droop
                ClothingFlowType.Tight  => this.currentRainStrength * 0.005f,  // barely affected
                _                       => this.currentRainStrength * 0.010f
            };
            cImpulse += new Vector2(0f, rainDroop * str);       // downward wet droop
            cImpulse.X *= (1f - this.currentRainStrength * 0.15f); // rain dampens lateral swing
        }

        // Swimming: clothing fans out and floats in water (buoyant uplift with gentle spread)
        if (character is Farmer swimmingFarmer && swimmingFarmer.swimming.Value)
        {
            cImpulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.014f,
                -(Game1.random.NextSingle() * 0.009f)) * flowAmp * str;
            cImpulse *= 0.88f; // water dampens oscillations
            this.clothingImpulse[key] = cImpulse;
            return;
        }

        // Speed amplification: running fast = more dramatic clothing trail
        var speed = velocity.Length();
        if (speed > 3.5f)
        {
            cImpulse += new Vector2(-velocity.X, -velocity.Y) * (0.010f * str * flowAmp * Math.Min(speed * 0.2f, 1.5f));
        }

        // Micro-flicker: tiny baseline activity so flowy clothing is never perfectly still
        if (flowType == ClothingFlowType.Flowy)
        {
            cImpulse += new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.004f * str,
                (Game1.random.NextSingle() - 0.5f) * 0.002f * str);
        }

        cImpulse *= decayFactor;
        this.clothingImpulse[key] = cImpulse;
    }

    /// <summary>
    /// Applies a brief "chin-dip" body bounce when the farmer begins eating or drinking.
    /// Models the slight forward lean and chewing motion typical of eating animations.
    /// Hair swings forward with the head movement then settles back.
    /// </summary>
    private void ApplyEatingBounce(Farmer farmer)
    {
        if (!this.config.EnableBodyPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(farmer);
        var profile = this.detector.Resolve(farmer);
        var baseStrength = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleBellyStrength) / 2f,
            BodyProfileType.Masculine => (this.config.MaleBellyStrength + this.config.MaleGroinStrength) / 2f,
            _ => 0.35f
        };

        // Forward lean: slight tilt in the facing direction + downward head bob
        var leanDir = farmer.FacingDirection switch
        {
            0 => new Vector2(0f, -0.5f),
            1 => new Vector2(0.5f, 0f),
            2 => new Vector2(0f, 0.5f),
            3 => new Vector2(-0.5f, 0f),
            _ => new Vector2(0f, 0.3f)
        };

        var eatImpulse = leanDir * (0.28f * baseStrength) + new Vector2(0f, 0.12f);
        var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = existing + eatImpulse;

        if (this.config.EnableHairPhysics)
        {
            var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
            this.hairImpulse[key] = hi + leanDir * (this.config.HairStrength * 0.5f);
        }

        this.Monitor.Log("[SVP] Eating started — chin-dip body impulse applied.", LogLevel.Trace);
    }

    /// <summary>
    /// Applies a full-body flinch when lightning strikes (Game1.isLightning transitions true).
    /// Models the instinctive startle/cringe reaction to a nearby lightning strike:
    /// sharp random directional impulse, dramatic hair whip, brief hitstop-style freeze.
    /// Works for the farmer and is strong enough to be visible even indoors (through walls).
    /// </summary>
    private void ApplyLightningFlinch(Farmer farmer)
    {
        if (!this.config.EnableBodyPhysics)
        {
            return;
        }

        var key     = this.GetCharacterKey(farmer);
        var profile = this.detector.Resolve(farmer);
        var baseStr = profile switch
        {
            BodyProfileType.Feminine => (this.config.FemaleBreastStrength + this.config.FemaleButtStrength) / 2f,
            BodyProfileType.Masculine => (this.config.MaleButtStrength + this.config.MaleGroinStrength) / 2f,
            _ => 0.4f
        };

        // Random sharp spike in any direction — startled = no clear direction
        var flinchAngle = (float)(Game1.random.NextDouble() * Math.PI * 2.0);
        var flinchDir   = new Vector2((float)Math.Cos(flinchAngle), (float)Math.Sin(flinchAngle));
        var flinchImpulse = flinchDir * (0.7f * baseStr);

        var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = existing + flinchImpulse;

        if (this.config.EnableHairPhysics)
        {
            var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
            // Hair whips in the flinch direction — electric static makes it stand up a little
            this.hairImpulse[key] = hi + flinchDir * (this.config.HairStrength * 1.6f);
        }

        // Clothing billows outward from the electric shock
        if (this.config.EnableClothingFlowPhysics)
        {
            var ci = this.clothingImpulse.TryGetValue(key, out var cExist) ? cExist : Vector2.Zero;
            this.clothingImpulse[key] = ci + flinchImpulse * (this.config.ClothingFlowStrength * 1.2f);
        }

        // Brief hitstop for the startle reaction
        if (this.config.EnableHitstopEffect)
        {
            this.hitstopTicksRemaining = Math.Max(this.hitstopTicksRemaining, 2);
            Game1.flashAlpha = 0.45f; // white flash like actual lightning
        }

        this.Monitor.Log("[SVP] Lightning strike — full-body flinch impulse applied.", LogLevel.Trace);
    }

    // ── Fishing physics ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true for any Tool that functions as a combat or heavy physical weapon:
    /// MeleeWeapon, Axe, Pickaxe, or any custom tool whose name suggests a weapon.
    /// Excludes watering cans, fishing rods, hoes, milk pails, shears, and other utility tools.
    /// Covers ALL modded weapons as long as they are either MeleeWeapon derivatives or have
    /// a weapon-sounding name — broad intentional heuristic for full mod compatibility.
    /// </summary>
    private static bool IsCombatCapableTool(Tool tool)
    {
        if (tool is MeleeWeapon or Axe or Pickaxe) return true;
        if (tool is WateringCan or FishingRod or Hoe or MilkPail or Shears) return false;
        // Any other tool: check name for weapon keywords (covers mods)
        var name = tool.Name ?? string.Empty;
        return ContainsAny(name, "sword", "blade", "axe", "scythe", "dagger", "spear", "lance",
                                 "hammer", "club", "mace", "fist", "claw", "staff", "wand", "bow",
                                 "crossbow", "gun", "pistol", "weapon", "sickle", "whip", "flail");
    }

    /// <summary>
    /// Tracks the fishing rod state each tick and fires appropriate body/hair impulses:
    ///   Cast start (bobber enters air): body leans forward, hair sweeps forward
    ///   Bobber lands: body settles with small jolt
    ///   Fish biting (isFishing → hook response): subtle body twitch
    ///   Fish caught (fishCaught transition): body snaps back from release of tension + hair whip
    /// Compatible with all fishing rods including mod-added ones.
    /// </summary>
    private void TickFishingPhysics(Farmer farmer)
    {
        var rod = farmer.CurrentTool as FishingRod;
        var rodActive = rod is not null;

        bool bobberInAir   = rod?.castedButBobberStillInAir ?? false;
        bool fishBiting    = rod?.isFishing ?? false;
        bool fishCaught    = rod?.fishCaught ?? false;

        var key = this.GetCharacterKey(farmer);

        // Cast start: bobber just entered the air → body leans forward, hair sweeps out
        if (bobberInAir && !this.wasBobberInAir)
        {
            var castDir = farmer.FacingDirection switch
            {
                0 => new Vector2(0f, -0.40f),
                1 => new Vector2( 0.40f, 0f),
                2 => new Vector2(0f,  0.40f),
                3 => new Vector2(-0.40f, 0f),
                _ => Vector2.Zero
            };

            var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = existing + castDir * 0.55f;

            if (this.config.EnableHairPhysics)
            {
                var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
                this.hairImpulse[key] = hi + castDir * (this.config.HairStrength * 1.2f);
            }

            if (this.config.EnableClothingFlowPhysics)
            {
                var ci = this.clothingImpulse.TryGetValue(key, out var cExist) ? cExist : Vector2.Zero;
                this.clothingImpulse[key] = ci + castDir * (this.config.ClothingFlowStrength * 0.55f);
            }
        }

        // Bobber just landed in water (stopped being in air) → body settles with small jolt
        if (!bobberInAir && this.wasBobberInAir && rodActive)
        {
            var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = existing + new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.08f,
                0.10f); // small downward settle
        }

        // Fish biting (isFishing just became true — fish is on the hook): body twitch
        if (fishBiting && !this.wasFishBiting)
        {
            var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = existing + new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.12f,
                -0.18f); // body jolts as fish pulls down
            if (this.config.EnableHairPhysics)
            {
                var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
                this.hairImpulse[key] = hi + new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 0.10f, -0.14f) * this.config.HairStrength;
            }
        }

        // Ongoing reel strain: tiny pulse every ~8 ticks while fishing
        if (fishBiting && !fishCaught && Game1.ticks % 8 == key % 8)
        {
            var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = existing + new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.04f,
                (Game1.random.NextSingle() - 0.5f) * 0.04f); // struggle micro-jitter
        }

        // Fish just caught: body snaps back from released tension + hair whips back
        if (fishCaught && !this.wasFishCaught && rodActive)
        {
            var snapDir = farmer.FacingDirection switch
            {
                0 => new Vector2(0f,  0.45f),  // was pulling forward → snap back downward
                1 => new Vector2(-0.45f, 0f),
                2 => new Vector2(0f, -0.45f),
                3 => new Vector2( 0.45f, 0f),
                _ => new Vector2(0f,  0.35f)
            };

            var existing = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = existing + snapDir * 0.65f;

            if (this.config.EnableHairPhysics)
            {
                var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
                this.hairImpulse[key] = hi + snapDir * (this.config.HairStrength * 1.4f);
            }

            if (this.config.EnableClothingFlowPhysics)
            {
                var ci = this.clothingImpulse.TryGetValue(key, out var cExist) ? cExist : Vector2.Zero;
                this.clothingImpulse[key] = ci + snapDir * (this.config.ClothingFlowStrength * 0.65f);
            }
        }

        this.wasBobberInAir = bobberInAir;
        this.wasFishBiting       = fishBiting;
        this.wasFishCaught       = fishCaught;
    }

    /// <summary>
    /// When the player casts a fishing rod, checks if any NPC, monster, or farm animal is
    /// standing in the cast path (ahead of the player within cast range ~4 tiles).
    /// If so, applies a harmless cosmetic knockdown: the target stumbles briefly.
    /// No damage, no relationship change, no gameplay side-effects — purely visual comedy.
    /// Works with all NPCs and modded characters within the cast range.
    /// </summary>
    private void TryBobberBonk(Farmer player)
    {
        if (Game1.currentLocation is null) return;

        // Cast direction based on facing
        var castDir = player.FacingDirection switch
        {
            0 => new Vector2(0f, -1f),
            1 => new Vector2(1f,  0f),
            2 => new Vector2(0f,  1f),
            3 => new Vector2(-1f, 0f),
            _ => Vector2.Zero
        };

        const float bobberRange = 256f; // ~4 tiles
        var ahead = player.Position + castDir * (bobberRange * 0.5f);

        // Check NPCs (including pets, mod characters)
        foreach (var character in this.EnumerateHumanoids(Game1.currentLocation))
        {
            if (character is Farmer) continue;
            var distAhead = Vector2.Distance(character.Position, ahead);
            if (distAhead > bobberRange * 0.6f) continue;

            // Make sure it's generally in the cast direction (dot product check)
            var toTarget = character.Position - player.Position;
            if (toTarget.LengthSquared() > 0.001f)
            {
                var dot = Vector2.Dot(Vector2.Normalize(toTarget), castDir);
                if (dot < 0.3f) continue; // not in front
            }

            this.ApplyBobberBonkKnockdown(character, castDir);
        }

        // Check monsters
        foreach (var monster in this.EnumerateMonsters(Game1.currentLocation))
        {
            if (Vector2.Distance(monster.Position, player.Position) > bobberRange) continue;
            var toTarget = monster.Position - player.Position;
            if (toTarget.LengthSquared() > 0.001f)
            {
                var dot = Vector2.Dot(Vector2.Normalize(toTarget), castDir);
                if (dot < 0.3f) continue;
            }

            this.ApplyBobberBonkKnockdown(monster, castDir);
        }

        // Check farm animals
        foreach (var animal in EnumerateFarmAnimals(Game1.currentLocation))
        {
            if (Vector2.Distance(animal.Position, player.Position) > bobberRange) continue;
            var key = this.GetCharacterKey(animal);
            var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
            this.bodyImpulse[key] = bodyEntry + castDir * 0.35f;
            this.farmAnimalKnockdown[key] = new NpcKnockdownState
            {
                Impulse = castDir * (this.config.RagdollKnockbackStrength * 0.35f),
                TicksRemaining = 8
            };
        }
    }

    /// <summary>
    /// Applies a "sit-down stumble" cosmetic knockdown to the given character from a bobber hit.
    /// The character is knocked in the cast direction and briefly stumbles.
    /// </summary>
    private void ApplyBobberBonkKnockdown(Character character, Vector2 castDir)
    {
        var key = this.GetCharacterKey(character);
        this.npcKnockdown[key] = new NpcKnockdownState
        {
            Impulse = castDir * (this.config.RagdollKnockbackStrength * 0.55f),
            TicksRemaining = 14
        };

        var bodyEntry = this.bodyImpulse.TryGetValue(key, out var bi) ? bi : Vector2.Zero;
        this.bodyImpulse[key] = bodyEntry + castDir * 0.5f;

        if (this.config.EnableHairPhysics)
        {
            var hi = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
            this.hairImpulse[key] = hi + castDir * (this.config.HairStrength * 0.8f);
        }
    } Heavy animals (cow, goat, sheep, pig, ostrich) get lower
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

        // ── Drive hair chain spring simulation ────────────────────────────────
        // The accumulated hair impulse is the external force applied to the chain root.
        // The chain propagates it down through segments with attenuation and spring lag.
        this.StepHairChain(key, impulse, velocity);
    }

    /// <summary>
    /// Advance (or lazily create) the HairChain for this character by one tick.
    /// Accumulates all sources that wrote to hairImpulse[key] plus movement-driven force.
    /// </summary>
    private void StepHairChain(int key, Vector2 rootForce, Vector2 characterVelocity)
    {
        if (!this.hairChains.TryGetValue(key, out var chain))
        {
            // Recreate chain if segment count changed in config
            chain = new HairChain(this.config.HairChainSegments);
            this.hairChains[key] = chain;
        }
        else if (chain.SegmentCount != this.config.HairChainSegments)
        {
            // Config changed at runtime — rebuild (GMCM slider moved)
            chain = new HairChain(this.config.HairChainSegments);
            this.hairChains[key] = chain;
        }

        // External force = accumulated hair impulse (scaled down so it fits the spring model)
        var chainForce = rootForce * 0.60f;
        chain.Step(chainForce, this.config.HairChainStiffness, this.config.HairChainDamping);
    }

    // ── Wing physics (HDT multi-bone wing chain) ──────────────────────────────

    /// <summary>
    /// Advance the wing pair simulation for a winged creature one tick.
    /// Wingbeat impulses, body velocity, and glide forces all feed into the wing chain.
    /// Models the Source Engine Jiggle Bones approach: 4 bones per wing, cascade-lag.
    /// Called for Bat, Dragon, FlyingBug, and any winged archetype.
    /// </summary>
    private void SimulateWingPhysics(NPC creature, Vector2 velocity)
    {
        if (!this.config.EnableWingPhysics)
        {
            return;
        }

        var key = this.GetCharacterKey(creature);
        if (!this.wingPairs.TryGetValue(key, out var wings))
        {
            wings = new WingPair();
            this.wingPairs[key] = wings;
        }

        var strength = this.config.MonsterArchetypeStrength;
        var t        = Game1.ticks;

        // Base force from creature body velocity (movement drives wing flutter)
        var baseForce = new Vector2(-velocity.X, -velocity.Y) * (0.028f * strength);

        // Automatic wingbeat oscillation: sinusoidal Y force at ~20-tick period
        // (mimics the rhythmic flap of flying creatures)
        var phase = key & 0x3F;
        var wingbeatForce = new Vector2(
            (float)Math.Sin(t * 0.18f + phase) * 0.020f * strength,
            (float)Math.Cos(t * 0.30f + phase) * 0.035f * strength);   // Y dominant = up/down flap

        var totalForce = baseForce + wingbeatForce;
        wings.Step(totalForce, this.config.WingChainStiffness, this.config.WingChainDamping);
    }

    /// <summary>
    /// Get the wing pair for a creature (for rendering or impulse application).
    /// Returns null if wing physics are off or the creature has no wing pair yet.
    /// </summary>
    private WingPair? GetOrCreateWingPair(int key)
    {
        if (!this.config.EnableWingPhysics) return null;
        if (!this.wingPairs.TryGetValue(key, out var wings))
        {
            wings = new WingPair();
            this.wingPairs[key] = wings;
        }
        return wings;
    }

    // ── Fur physics (HDT surface ripple chain) ────────────────────────────────

    /// <summary>
    /// Advance the fur ripple chain for a furry creature/animal one tick.
    /// Models surface fur motion: ripples propagate from root to tip as the creature moves.
    /// </summary>
    private void SimulateFurPhysics(int key, Vector2 velocity)
    {
        if (!this.config.EnableFurPhysics)
        {
            return;
        }

        if (!this.furChains.TryGetValue(key, out var fur)
            || fur.SegmentCount != this.config.FurChainSegments)
        {
            fur = new FurChain(this.config.FurChainSegments);
            this.furChains[key] = fur;
        }

        var baseForce = new Vector2(-velocity.X, -velocity.Y) * 0.022f;

        // Micro-oscillation: fur ripples even when stationary (breathing, ambient air)
        var phase = key & 0x3F;
        baseForce += new Vector2(
            (float)Math.Sin(Game1.ticks * 0.09f + phase) * 0.006f,
            (float)Math.Cos(Game1.ticks * 0.07f + phase) * 0.004f);

        fur.Step(baseForce, this.config.FurChainStiffness, this.config.FurChainDamping);
    }

    // ── Tail physics (HDT pendant tail chain) ────────────────────────────────

    /// <summary>
    /// Advance the tail chain for a tailed creature/animal one tick.
    /// The tail wags in response to velocity and adds idle sway when stationary.
    /// </summary>
    private void SimulateTailPhysics(int key, Vector2 velocity)
    {
        if (!this.config.EnableTailPhysics)
        {
            return;
        }

        if (!this.tailChains.TryGetValue(key, out var tail)
            || tail.SegmentCount != this.config.TailChainSegments)
        {
            tail = new TailChain(this.config.TailChainSegments);
            this.tailChains[key] = tail;
        }

        var baseForce = new Vector2(-velocity.X, -velocity.Y) * 0.030f;

        // Idle tail wag: slow sinusoidal lateral oscillation
        var phase = key & 0x3F;
        baseForce += new Vector2(
            (float)Math.Sin(Game1.ticks * 0.06f + phase) * 0.018f,
            0f);

        tail.Step(baseForce, this.config.TailChainStiffness, this.config.TailChainDamping);
    }

    // ── Animal bone physics ───────────────────────────────────────────────────

    /// <summary>
    /// Advance the animal bone group (ears, snout, body) for a farm animal one tick.
    /// </summary>
    private void SimulateAnimalBonePhysics(int key, Vector2 velocity, bool isHeavy)
    {
        if (!this.config.EnableAnimalBonePhysics)
        {
            return;
        }

        if (!this.animalBones.TryGetValue(key, out var bones))
        {
            bones = new AnimalBoneGroup();
            this.animalBones[key] = bones;
        }

        var force = new Vector2(-velocity.X, -velocity.Y) * (this.config.AnimalBoneStrength * 0.025f);

        // Add idle micro-oscillation for always-active ear/snout animation
        var phase = key & 0x3F;
        force += new Vector2(
            (float)Math.Sin(Game1.ticks * 0.08f + phase) * 0.008f * this.config.AnimalBoneStrength,
            (float)Math.Cos(Game1.ticks * 0.10f + phase) * 0.005f * this.config.AnimalBoneStrength);

        bones.Step(force, this.config.BoneStiffness, this.config.BoneDamping, isHeavy);
    }

    // ── Wood shatter VFX ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if there is a wooden surface (wood fence, stump, tree trunk, wood crate,
    /// wood furniture) within ~1.5 tiles of the player in their facing direction.
    /// Used to trigger wood shatter particle VFX.
    /// </summary>
    private bool DetectWoodSurfaceNear(Farmer player)
    {
        if (Game1.currentLocation is null) return false;

        var facingOffset = player.FacingDirection switch
        {
            0 => new Vector2(0f, -64f),
            1 => new Vector2(64f,  0f),
            2 => new Vector2(0f,  64f),
            3 => new Vector2(-64f, 0f),
            _ => Vector2.Zero
        };
        var checkPos  = player.Position + facingOffset;
        var checkTile = checkPos / 64f;

        // Resource clumps: stumps, log stumps (debris type 600/602)
        foreach (var clump in Game1.currentLocation.resourceClumps)
        {
            if (Vector2.Distance(clump.Tile * 64f, checkPos) < 96f)
            {
                var clumpName = clump.parentSheetIndex.Value;
                // 600 = large stump, 602 = large log, 46/47 = hardwood stumps
                if (clumpName == 600 || clumpName == 602 || clumpName == 46 || clumpName == 47)
                {
                    return true;
                }
            }
        }

        // Location objects at the facing tile
        var tileKey = new Vector2((int)checkTile.X, (int)checkTile.Y);
        if (Game1.currentLocation.objects.TryGetValue(tileKey, out var obj))
        {
            var objName = obj.Name ?? string.Empty;
            if (ContainsAny(objName, "wood", "twig", "branch", "log", "stick",
                                     "barrel", "crate", "box", "fence", "plank",
                                     "table", "chair", "desk", "shelf", "trunk"))
            {
                return true;
            }
        }

        // Furniture objects
        foreach (var furniture in Game1.currentLocation.furniture)
        {
            if (furniture is null) continue;
            if (Vector2.Distance(furniture.TileLocation * 64f, checkPos) > 128f) continue;
            var fname = furniture.Name ?? string.Empty;
            if (ContainsAny(fname, "wood", "pine", "oak", "table", "chair", "desk",
                                   "shelf", "wardrobe", "cabinet", "log", "barrel",
                                   "crate", "fence", "plank", "bench"))
            {
                return true;
            }
        }

        // Terrain features: trees (wild trees + fruit trees)
        foreach (var kv in Game1.currentLocation.terrainFeatures.Pairs)
        {
            if (kv.Value is Tree or FruitTree)
            {
                if (Vector2.Distance(kv.Key * 64f, checkPos) < 96f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Spawns brown/tan wood splinter debris particles to simulate a wood shatter effect.
    /// Particle count scales with intensity. Purely cosmetic.
    /// </summary>
    private void ApplyWoodShatterVfx(Vector2 worldPos)
    {
        if (Game1.currentLocation is null) return;
        var count = Math.Max(2, (int)(5 * this.config.WoodShatterIntensity) + Game1.random.Next(4));
        for (int i = 0; i < count; i++)
        {
            var angle = Game1.random.NextSingle() * MathF.PI * 2f;
            var dist  = 12f + Game1.random.NextSingle() * 35f * this.config.WoodShatterIntensity;
            var pos   = worldPos + new Vector2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            // Debris type 12 = brown/woody colored debris (closest to wood splinters)
            Game1.createRadialDebris(Game1.currentLocation, 12, (int)(pos.X / 64), (int)(pos.Y / 64), 1, false);
        }
    }



    private void SimulateIdle(Character character, Vector2 velocity)
    {
        if (!this.config.EnableIdleMotion)
        {
            return;
        }

        if (character is Farmer farmer && farmer.UsingTool)
        {
            return;
        }

        var key      = this.GetCharacterKey(character);
        var speed    = velocity.LengthSquared();
        var idleStr  = Math.Max(0.05f, this.config.IdleMotionStrength);

        // ── Slow-walk micro-sway (fires at low velocity, keeps physics active during meandering)
        // Very subtle step-rhythm sway so body physics never go completely dormant while walking.
        if (speed > 0.001f && speed < 2.5f)
        {
            // Phase-locked to character key so each character has a unique walk rhythm
            if (Game1.ticks % 22 == key % 22)
            {
                var walkPhase    = (Game1.ticks / 22 % 2 == 0) ? 1f : -1f;
                var walkMicro    = new Vector2(walkPhase * 0.06f * idleStr, 0.03f * idleStr);
                var bMicro       = this.bodyImpulse.TryGetValue(key, out var bm) ? bm : Vector2.Zero;
                this.bodyImpulse[key] = bMicro + walkMicro;

                if (this.config.EnableHairPhysics)
                {
                    var hMicro = this.hairImpulse.TryGetValue(key, out var hm) ? hm : Vector2.Zero;
                    this.hairImpulse[key] = hMicro + walkMicro * (this.config.HairStrength * 0.3f);
                }
            }
        }

        // Only run breathing and full idle bursts when standing still or nearly so
        if (speed > 0.1f)
        {
            return;
        }

        // ── Always-active breathing pulse (every 45 ticks, very subtle) ─────────
        // Fires regardless of the main idle interval — keeps body/hair gently alive at all times.
        if (Game1.ticks % 45 == key % 45)
        {
            var breathPhase = (Game1.ticks / 45) % 2 == 0 ? -1f : 1f;
            var breathImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.04f * idleStr,
                breathPhase * 0.06f * idleStr);

            var bEntry = this.bodyImpulse.TryGetValue(key, out var bExist) ? bExist : Vector2.Zero;
            this.bodyImpulse[key] = bEntry + breathImpulse;

            if (this.config.EnableHairPhysics)
            {
                var hEntry = this.hairImpulse.TryGetValue(key, out var hExist) ? hExist : Vector2.Zero;
                this.hairImpulse[key] = hEntry + breathImpulse * (this.config.HairStrength * 0.25f);
            }
        }

        // ── Weather-reactive micro-pulse: shiver in cold/rain, sway in wind ─────
        if (Game1.ticks % 30 == (key + 7) % 30)
        {
            if (this.currentRainStrength > 0.1f && Game1.currentLocation?.IsOutdoors == true)
            {
                // Shiver: rapid small left-right jitter (cold wet body)
                var shiverDir = (Game1.ticks / 30 % 2 == 0) ? 1f : -1f;
                var shiver = new Vector2(shiverDir * 0.08f * idleStr, 0f);
                var bShiver = this.bodyImpulse.TryGetValue(key, out var bs) ? bs : Vector2.Zero;
                this.bodyImpulse[key] = bShiver + shiver;
            }
            else if (this.currentWindStrength > 0.15f && Game1.currentLocation?.IsOutdoors == true)
            {
                // Wind buffet: whole body sways slightly in wind direction
                var buffet = new Vector2(this.currentWindStrength * 0.06f * idleStr, 0f);
                var bBuffet = this.bodyImpulse.TryGetValue(key, out var bb) ? bb : Vector2.Zero;
                this.bodyImpulse[key] = bBuffet + buffet;
            }
        }

        // ── Main idle burst (configurable interval, default 90 ticks = ~1.5 s) ─
        var interval = Math.Max(30, this.config.IdleMotionIntervalTicks);
        if (Game1.ticks % interval != key % interval)
        {
            return;
        }

        var impulse = this.bodyImpulse.TryGetValue(key, out var existing) ? existing : Vector2.Zero;

        // ── Per-profile 8-step cycling idle (primary, fires ~50% of idle bursts) ─
        // Each character cycles deterministically through 8 profile-specific moves.
        // The profile-specific cycles are checked first so female/male motions dominate;
        // contextual weather idles can still override when conditions are active.
        var profile   = this.detector.Resolve(character);
        var animRoll  = Game1.random.NextDouble();
        Vector2 idleImpulse;

        // Weather/season-contextual idles (checked first at low probability so they
        // can interrupt the cycling sequence during extreme weather/seasons)
        if (animRoll < 0.07 && this.currentRainStrength > 0.2f && Game1.currentLocation?.IsOutdoors == true)
        {
            // Shudder: full-body shiver from being cold and wet
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.30f * idleStr,
                (Game1.random.NextSingle() - 0.5f) * 0.12f * idleStr);
        }
        else if (animRoll < 0.12 && this.currentSnowStrength > 0f && Game1.currentLocation?.IsOutdoors == true)
        {
            // Rub arms: strong lateral then settle inward (cold)
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.40f * idleStr, 0.05f * idleStr);
        }
        else if (animRoll < 0.16 && Game1.IsSummer && !Game1.isRaining && Game1.currentLocation?.IsOutdoors == true)
        {
            // Fan/cool self: one arm raised with a tilting lean (hot day)
            idleImpulse = new Vector2(0.10f * idleStr, -0.28f * idleStr);
        }
        else if (animRoll < 0.20 && this.currentWindStrength > 0.2f && Game1.currentLocation?.IsOutdoors == true)
        {
            // Wind-blown: body pushed laterally as a gust hits
            idleImpulse = new Vector2(this.currentWindStrength * 0.6f * idleStr, 0.05f * idleStr);
        }
        else if (animRoll < 0.28)
        {
            // Standard body sway
            idleImpulse = new Vector2(
                Game1.random.NextSingle() - 0.5f,
                Game1.random.NextSingle() - 0.5f) * 0.28f * idleStr;
        }
        else if (animRoll < 0.37)
        {
            // Hip sway: strong lateral push with minor vertical — most dramatic from behind
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.50f * idleStr, (Game1.random.NextSingle() - 0.5f) * 0.06f * idleStr);
        }
        else if (animRoll < 0.44)
        {
            // Lean to one side: weight shift, slower wider arc
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.38f * idleStr, (Game1.random.NextSingle() - 0.5f) * 0.08f * idleStr);
        }
        else if (animRoll < 0.51)
        {
            // Arm raise / stretch: strong upward then natural fall
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.15f * idleStr,
                -0.55f * idleStr);
        }
        else if (animRoll < 0.58)
        {
            // Bounce: rhythmic downward weight shift, like tapping foot
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.06f * idleStr,
                0.40f * idleStr);
        }
        else if (animRoll < 0.65)
        {
            // Shimmy: strong rapid lateral push
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.60f * idleStr, 0.08f * idleStr);
        }
        else if (animRoll < 0.71)
        {
            // Deep breathing: slow oscillation in current phase
            idleImpulse = new Vector2(
                (Game1.random.NextSingle() - 0.5f) * 0.05f * idleStr,
                ((Game1.ticks / 30 % 2 == 0) ? -0.14f : 0.14f) * idleStr);
        }
        else if (animRoll < 0.76)
        {
            // Look-around tilt: slight head-check lean to one side
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.22f * idleStr, -0.10f * idleStr);
        }
        else if (animRoll < 0.81)
        {
            // Subtle back-and-forth rock: small depth-axis simulation
            var dir = (Game1.ticks / interval % 2 == 0) ? 1f : -1f;
            idleImpulse = new Vector2(0f, dir * 0.20f * idleStr);
        }
        else if (animRoll < 0.87)
        {
            // Shoulder roll: diagonal push then settle
            var angle = (float)(Game1.random.NextDouble() * Math.PI * 0.5) - (float)(Math.PI * 0.25);
            idleImpulse = new Vector2((float)Math.Cos(angle) * 0.45f * idleStr, (float)Math.Sin(angle) * 0.35f * idleStr);
        }
        else if (animRoll < 0.93)
        {
            // Foot-tap + weight shift: quick downward then lateral
            var side = Game1.random.NextDouble() < 0.5 ? 1f : -1f;
            idleImpulse = new Vector2(side * 0.18f * idleStr, 0.32f * idleStr);
        }
        else
        {
            // ── Per-profile 8-step cycling idle ──────────────────────────────
            // Advance this character's cycle step (seeded from key for unique offset)
            if (!this.idleCycleStep.TryGetValue(key, out var step))
            {
                step = key & 0x07; // unique start offset 0–7 per character
            }
            this.idleCycleStep[key] = (step + 1) & 0x07;

            idleImpulse = profile switch
            {
                BodyProfileType.Feminine  => GetFeminineIdleImpulse(step, idleStr),
                BodyProfileType.Masculine => GetMasculineIdleImpulse(step, idleStr),
                _                         => GetAndrogynousIdleImpulse(step, idleStr)
            };
        }

        this.bodyImpulse[key] = impulse + idleImpulse;

        // Hair tosses with body motion
        if (this.config.EnableHairPhysics)
        {
            var hairImpulse = this.hairImpulse.TryGetValue(key, out var hi) ? hi : Vector2.Zero;
            this.hairImpulse[key] = hairImpulse + idleImpulse * (this.config.HairStrength * 0.5f);
        }

        // Clothing also reacts to idle burst — flowy clothes swing with the movement
        if (this.config.EnableClothingFlowPhysics)
        {
            var flowType  = this.GetClothingFlowType(character);
            var clothScale = flowType == ClothingFlowType.Flowy ? 0.55f : 0.25f;
            var ci        = this.clothingImpulse.TryGetValue(key, out var cExist) ? cExist : Vector2.Zero;
            this.clothingImpulse[key] = ci + idleImpulse * (clothScale * this.config.ClothingFlowStrength);
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

    // ── Typed physics debris particles ────────────────────────────────────────

    /// <summary>
    /// Spawn <paramref name="count"/> typed physics particles at <paramref name="worldPos"/>
    /// with randomised outward velocities and an upward bias to create a natural burst arc.
    /// Sawdust particles are capped at 40 % of the configured lifetime (fast fade).
    /// The particle list is capped at <see cref="MaxTypedParticles"/>; oldest entries are
    /// evicted when the cap is hit so performance is bounded.
    /// </summary>
    private void SpawnTypedDebris(Vector2 worldPos, PhysicsParticleKind kind, int count, float speed = 1f)
    {
        var strength = this.config.DebrisPhysicsStrength;

        for (int i = 0; i < count; i++)
        {
            if (this.typedParticles.Count >= MaxTypedParticles)
            {
                this.typedParticles.RemoveAt(0); // evict oldest
            }

            var angle = Game1.random.NextSingle() * MathF.PI * 2f;

            // Per-kind physics constants
            var (minSpd, maxSpd, upBias) = kind switch
            {
                PhysicsParticleKind.WoodSplinter => (1.5f,  3.5f, 0.5f),
                PhysicsParticleKind.Sawdust      => (0.4f,  1.2f, 0.3f),
                PhysicsParticleKind.StoneChunk   => (2.0f,  5.0f, 0.5f),
                PhysicsParticleKind.OreChunk     => (1.8f,  4.5f, 0.5f),
                PhysicsParticleKind.GemChunk     => (2.5f,  6.0f, 0.4f),
                _                                => (1.5f,  3.5f, 0.5f),
            };

            var magnitude = (minSpd + Game1.random.NextSingle() * (maxSpd - minSpd)) * speed * strength;
            var vel = new Vector2(MathF.Cos(angle) * magnitude, MathF.Sin(angle) * magnitude);
            // Bias upward (negative Y) to produce a burst-outward arc
            vel.Y -= (MathF.Abs(vel.Y) * upBias + magnitude * 0.3f);

            var lifetime = this.config.TypedDebrisLifetimeTicks;
            if (kind == PhysicsParticleKind.Sawdust)
            {
                lifetime = Math.Max(120, (int)(lifetime * 0.40f)); // sawdust fades fast
            }

            var particle = new TypedPhysicsParticle
            {
                Position         = worldPos + new Vector2(
                    (Game1.random.NextSingle() - 0.5f) * 20f,
                    (Game1.random.NextSingle() - 0.5f) * 10f),
                Velocity         = vel,
                Rotation         = Game1.random.NextSingle() * MathF.PI * 2f,
                RotationVelocity = (Game1.random.NextSingle() - 0.5f) * 0.25f * speed,
                AgeTicks         = 0,
                MaxAgeTicks      = lifetime,
                Kind             = kind,
                HasBounced       = false,
            };
            this.typedParticles.Add(particle);
        }
    }

    /// <summary>
    /// Advance all typed physics particles by one game tick:
    /// apply gravity and drag, spin each particle, perform a single velocity-reflection
    /// bounce when Y velocity turns sufficiently positive, apply a scatter impulse when the
    /// player walks nearby, and remove expired particles.
    /// </summary>
    private void StepTypedParticles(GameLocation location)
    {
        var playerPos   = Game1.player?.Position ?? Vector2.Zero;
        var playerMoved = false;
        var playerVel   = Vector2.Zero;

        if (Game1.player != null
            && this.lastPositions.TryGetValue(this.GetCharacterKey(Game1.player), out var lpv))
        {
            playerVel   = Game1.player.Position - lpv;
            playerMoved = playerVel.LengthSquared() > 0.05f;
        }

        var scatterStr = this.config.TypedDebrisScatterStrength;

        for (int i = this.typedParticles.Count - 1; i >= 0; i--)
        {
            var p = this.typedParticles[i];
            p.AgeTicks++;

            if (p.AgeTicks >= p.MaxAgeTicks)
            {
                this.typedParticles.RemoveAt(i);
                continue;
            }

            // Per-kind gravity and drag constants
            var (gravity, drag) = p.Kind switch
            {
                PhysicsParticleKind.WoodSplinter => (0.12f, 0.97f),
                PhysicsParticleKind.Sawdust      => (0.04f, 0.92f),
                PhysicsParticleKind.StoneChunk   => (0.22f, 0.98f),
                PhysicsParticleKind.OreChunk     => (0.18f, 0.97f),
                PhysicsParticleKind.GemChunk     => (0.10f, 0.96f),
                _                                => (0.15f, 0.97f),
            };

            // Gravity (positive Y = toward bottom of screen)
            p.Velocity.Y += gravity;

            // One-time bounce: when the particle is falling fast enough, reflect Y velocity
            if (!p.HasBounced && p.AgeTicks > 8 && p.Velocity.Y > 1.8f)
            {
                p.Velocity.Y   *= -0.35f;
                p.Velocity.X   *= 0.70f;
                p.RotationVelocity *= 1.4f; // bouncing makes it spin faster
                p.HasBounced    = true;
            }

            // Air resistance
            p.Velocity         *= drag;
            p.RotationVelocity *= drag;

            // Walk-scatter impulse: player walking nearby kicks debris away
            if (scatterStr > 0f && playerMoved)
            {
                var dist = Vector2.Distance(p.Position, playerPos);
                if (dist < 60f && dist > 0.5f)
                {
                    var scatterDir = Vector2.Normalize(p.Position - playerPos);
                    if (!float.IsNaN(scatterDir.X) && !float.IsNaN(scatterDir.Y))
                    {
                        var falloff    = 1f - dist / 60f;
                        var scatterMag = playerVel.Length() * falloff * scatterStr * 0.8f;
                        p.Velocity            += scatterDir * scatterMag;
                        p.RotationVelocity    += (Game1.random.NextSingle() - 0.5f) * scatterMag * 0.2f;
                    }
                }
            }

            // Integrate
            p.Position += p.Velocity;
            p.Rotation += p.RotationVelocity;
        }
    }

    /// <summary>
    /// Scans terrain features for Tree/FruitTree tiles that existed last tick but are now gone.
    /// Each removed tree tile spawns a burst of wood splinters and sawdust, plus a
    /// proximity-scaled body/hair impulse on the player to simulate the ground thud.
    /// Called every 3 ticks from OnUpdateTicked.
    /// </summary>
    private void TryDetectTreeFell(GameLocation location)
    {
        if (location?.terrainFeatures == null) return;

        // Build current tree tile set
        var currentTrees = new HashSet<Point>();
        foreach (var kv in location.terrainFeatures.Pairs)
        {
            if (kv.Value is Tree or FruitTree)
            {
                currentTrees.Add(new Point((int)kv.Key.X, (int)kv.Key.Y));
            }
        }

        // Process removals
        foreach (var tile in this.prevTreeTiles)
        {
            if (currentTrees.Contains(tile)) continue;

            var worldPos = new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 32f);

            // Burst of wood splinters from the impact/stump point
            this.SpawnTypedDebris(worldPos, PhysicsParticleKind.WoodSplinter, 10 + Game1.random.Next(8), 2.2f);
            // Sawdust cloud scattered widely
            this.SpawnTypedDebris(worldPos, PhysicsParticleKind.Sawdust,      14 + Game1.random.Next(8), 1.6f);

            // Player body/hair impulse to simulate ground-thud camera shake
            if (this.config.EnableTreeFallImpulse && Game1.player != null)
            {
                var dist      = Vector2.Distance(worldPos, Game1.player.Position);
                var maxDist   = 512f;  // 8 tiles
                if (dist < maxDist)
                {
                    var distScale = Math.Max(0.15f, 1f - dist / maxDist);
                    var thudStr   = this.config.TreeFallImpulseStrength * distScale;
                    var pKey      = this.GetCharacterKey(Game1.player);

                    if (this.config.EnableBodyPhysics)
                    {
                        var existing = this.bodyImpulse.TryGetValue(pKey, out var bi) ? bi : Vector2.Zero;
                        this.bodyImpulse[pKey] = existing + new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * 0.18f * thudStr,
                            0.32f * thudStr);   // downward thud
                    }

                    if (this.config.EnableHairPhysics)
                    {
                        var hExist = this.hairImpulse.TryGetValue(pKey, out var hi) ? hi : Vector2.Zero;
                        this.hairImpulse[pKey] = hExist + new Vector2(
                            (Game1.random.NextSingle() - 0.5f) * 0.28f * thudStr,
                            0.44f * thudStr) * this.config.HairStrength;
                    }
                }
            }
        }

        this.prevTreeTiles.Clear();
        foreach (var t in currentTrees) this.prevTreeTiles.Add(t);
    }

    /// <summary>
    /// Render all live typed physics particles to the SpriteBatch as small coloured quads.
    /// Particles fade out over the final 25 % of their lifetime.
    /// The 1×1 white Texture2D is created lazily on first use so we don't depend on the
    /// graphics device being ready during Entry().
    /// </summary>
    private void RenderTypedParticles(SpriteBatch sb)
    {
        if (this.typedParticles.Count == 0) return;

        // Lazily create the pixel texture
        if (this.pixelTexture is null || this.pixelTexture.IsDisposed)
        {
            if (Game1.graphics?.GraphicsDevice is null) return;
            this.pixelTexture = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            this.pixelTexture.SetData(new[] { Color.White });
        }

        var zoom = Game1.options.zoomLevel;

        foreach (var p in this.typedParticles)
        {
            // Fade out over the final 25 % of the particle's life
            var lifeRatio = (float)p.AgeTicks / p.MaxAgeTicks;
            var alpha     = lifeRatio > 0.75f ? (1f - lifeRatio) / 0.25f : 1f;
            alpha         = Math.Clamp(alpha, 0f, 1f);

            var (color, size) = p.Kind switch
            {
                PhysicsParticleKind.WoodSplinter => (new Color(139,  90,  43, (int)(alpha * 220f)), 3.5f),
                PhysicsParticleKind.Sawdust      => (new Color(210, 180, 140, (int)(alpha * 150f)), 2.0f),
                PhysicsParticleKind.StoneChunk   => (new Color(130, 130, 130, (int)(alpha * 240f)), 4.5f),
                PhysicsParticleKind.OreChunk     => (new Color(184, 115,  51, (int)(alpha * 240f)), 3.5f),
                PhysicsParticleKind.GemChunk     => (new Color(100, 200, 255, (int)(alpha * 255f)), 3.0f),
                _                                => (new Color(180, 180, 180, (int)(alpha * 200f)), 3.0f),
            };

            if (color.A < 4) continue; // fully transparent — skip draw call

            var screenPos = Game1.GlobalToLocal(Game1.viewport, p.Position);

            sb.Draw(
                this.pixelTexture,
                screenPos,
                null,
                color,
                p.Rotation,
                new Vector2(0.5f, 0.5f),      // centre-origin for rotation
                size * zoom,
                SpriteEffects.None,
                0.91f);                        // draw on top of terrain, behind UI
        }
    }



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

    private bool gmcmRegistered = false;

    private void RegisterConfigMenu()
    {
        var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
        {
            return;
        }

        // Only register once — GameLaunched re-calls this, which is fine; the second call
        // just re-registers and GMCM replaces the old registration with the new one.
        this.gmcmRegistered = true;

        api.Register(
            this.ModManifest,
            reset: () =>
            {
                this.config = new ModConfig();
                this.LoadData(this.Helper);
                this.detector.SetConfigOverrides(this.config.GenderOverrides);
                this.UpdateWindStrength();
            },
            save: () =>
            {
                this.ApplyPresetIfMatched();
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

        // ── Spring-Damper Physics Engine ─────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Spring Physics Engine");
        api.AddParagraph(this.ModManifest, () =>
            "Controls the real spring-damper simulation behind all body-part jiggles. " +
            "Each bone (breast, butt, belly, thighs, groin) has its own spring. " +
            "Stiffness = how fast bones snap back. Damping = how many oscillation cycles before settling. " +
            "Tip: low stiffness + low damping = ultra-jelly. High stiffness + high damping = tight realistic bounce.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.BoneStiffness, v => this.config.BoneStiffness = v,
            () => "Bone stiffness",
            () => "Spring constant k. Lower = softer/jelly, bones take longer to return to rest. Higher = firm snap-back. Range 0.04–0.35. Default 0.12.", 0.04f, 0.35f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.BoneDamping, v => this.config.BoneDamping = v,
            () => "Bone damping",
            () => "Damping coefficient c. Lower = more oscillation cycles / jelly bouncing. Higher = one-shot settle. Range 0.03–0.25. Default 0.08.", 0.03f, 0.25f, 0.01f);

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
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairChainStiffness, v => this.config.HairChainStiffness = v,
            () => "Hair chain stiffness",
            () => "Spring return speed for hair chain segments. Lower = looser, more flowing hair. Range 0.03–0.25.", 0.03f, 0.25f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.HairChainDamping, v => this.config.HairChainDamping = v,
            () => "Hair chain damping",
            () => "How quickly hair stops oscillating. Lower = more swinging cycles. Range 0.03–0.20.", 0.03f, 0.20f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.HairChainSegments,
            v => this.config.HairChainSegments = (int)Math.Round(v),
            () => "Hair chain segments",
            () => "Number of spring links in the hair chain. More = smoother cascade. 2 = stiff, 8 = very flowing.", 2f, 8f, 1f);

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
        api.AddSectionTitle(this.ModManifest, () => "Idle Motion & Variety");
        api.AddParagraph(this.ModManifest, () =>
            "Controls how often idle physics bursts fire when standing still. " +
            "A built-in breathing pulse fires every 45 ticks (~0.75 s) regardless of this setting. " +
            "Weather-reactive idles fire at ~1 s intervals: shivers in rain/snow, wind-sway outdoors, fanning in summer. " +
            "A slow-walk micro-sway fires every 22 ticks during very slow movement to keep physics active while meandering. " +
            "The main idle burst (hip sway, shimmy, arm-raise, shoulder roll, lean, foot-tap, twirl, look-around, etc.) fires at the interval below.");
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.IdleMotionIntervalTicks,
            v => this.config.IdleMotionIntervalTicks = Math.Max(30, (int)v),
            () => "Idle burst interval (ticks)",
            () => "Ticks between major idle physics events. 90 = ~1.5 s (default), 30 = very frequent, 240 = infrequent. " +
                  "Breathing, weather-reactive, and slow-walk micro-sways are always active.",
            30f, 300f, 15f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.IdleMotionStrength,
            v => this.config.IdleMotionStrength = v,
            () => "Idle motion strength",
            () => "Overall scale for idle impulse magnitudes. 0.5 = subtle, 1.0 = default, 2.0 = very expressive.",
            0.1f, 3f, 0.1f);

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

        // ── Clothing flow physics ─────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Clothing Flow Physics");
        api.AddParagraph(this.ModManifest, () =>
            "A separate physics layer sits on top of body physics to simulate realistic cloth movement. " +
            "Clothing type is auto-detected from item name keywords:\n" +
            "  Flowy (dress/robe/cape/cloak/skirt/gown/tunic/silk/kimono/wizard/maid/apron): " +
            "large trailing lag, billows in wind, droops heavily in rain, floats in water, slow settle.\n" +
            "  Tight (tights/shorts/bikini/crop/sport/athletic/fitted/jeans/armor): " +
            "closely tracks body, minimal extra sway, barely affected by wind/rain.\n" +
            "  Default (everything else): moderate trail and amplitude.\n" +
            "The clothing offset is blended at ~42% scale on top of body physics — " +
            "clothing visually trails and flows without clipping through body physics. " +
            "Works with all vanilla and modded clothing items.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableClothingFlowPhysics, v => this.config.EnableClothingFlowPhysics = v,
            () => "Enable clothing flow physics",
            () => "Separate cloth-physics layer: flowy clothing trails and billows, tight clothing hugs body. " +
                  "Blended on top of body physics without clipping.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.ClothingFlowStrength, v => this.config.ClothingFlowStrength = v,
            () => "Clothing flow strength",
            () => "How much clothing flows beyond body physics. 0 = no extra cloth sway, 1 = default trail, 2 = very dramatic billow.",
            0f, 2f, 0.05f);

        // ── New physics triggers ──────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Additional Physics Triggers");
        api.AddParagraph(this.ModManifest, () =>
            "Extra events that trigger physics impulses beyond movement and combat:\n" +
            "  Warp-step: body bounce + hair toss + clothing resettlement when stepping through doors.\n" +
            "  Eating/drinking: chin-dip lean impulse when the farmer begins eating.\n" +
            "  Lightning flinch: full-body startle + hair electric whip + screen flash on lightning strike.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWarpStepImpulse, v => this.config.EnableWarpStepImpulse = v,
            () => "Enable warp-step impulse",
            () => "Body bounce and hair toss when the farmer steps through a door, warp point, or teleport.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableEatingBounce, v => this.config.EnableEatingBounce = v,
            () => "Enable eating/drinking bounce",
            () => "Slight chin-dip body lean and hair swing when the farmer starts eating or drinking.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableLightningFlinch, v => this.config.EnableLightningFlinch = v,
            () => "Enable lightning flinch",
            () => "Sharp random-direction body flinch + electric hair whip + brief screen flash when lightning strikes outdoors.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableFishingPhysics, v => this.config.EnableFishingPhysics = v,
            () => "Enable fishing physics",
            () => "Fishing rod cast = body leans forward and hair sweeps out. Bobber splash = settle jolt. " +
                  "Fish biting = body twitch. Ongoing reel struggle = micro-jitter. Catch snap-back = body recoil + hair whip. " +
                  "Compatible with all fishing rods including mod-added ones.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableBobberBonk, v => this.config.EnableBobberBonk = v,
            () => "Enable bobber bonk",
            () => "Casting the fishing rod at an NPC, monster, or farm animal within ~4 tiles causes a harmless cosmetic knockdown. " +
                  "The target stumbles and bounces — no damage, no relationship impact, purely silly. " +
                  "Works with all NPCs and modded characters.");


        api.AddSectionTitle(this.ModManifest, () => "Combat Hit VFX");
        api.AddParagraph(this.ModManifest, () =>
            "Cosmetic particle effects and physics reactions on weapon/tool hits:\n" +
            "  Sparks: yellow/orange particles burst when hitting stone, metal, ore, rock, or armored monsters.\n" +
            "  Slime spray: green slime particles fly + extra slime jiggle when hitting slimes.\n" +
            "  Blood splatter: red debris particles when hitting humanoids, monsters, or farm animals.\n" +
            "  Tool collision hitstop: weapon bounces back (swing-back impulse) + extra freeze frames when hitting hard surfaces.\n" +
            "All effects are purely cosmetic — no gameplay changes, no extra damage.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableSparkEffects, v => this.config.EnableSparkEffects = v,
            () => "Enable spark VFX",
            () => "Burst of spark particles when a weapon or pickaxe hits stone, metal, ore, boulders, or armored enemies.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableSlimeSprayEffects, v => this.config.EnableSlimeSprayEffects = v,
            () => "Enable slime spray VFX",
            () => "Green slime particle spray + extra body jiggle on the slime when it is struck. Slimes go extra wobbly!");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableBloodSplatterEffects, v => this.config.EnableBloodSplatterEffects = v,
            () => "Enable blood splatter VFX",
            () => "Red particle spray when a weapon hits a humanoid NPC, monster, or farm animal. Toggle off for cleaner gameplay.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.BloodSplatterIntensity, v => this.config.BloodSplatterIntensity = v,
            () => "Blood splatter intensity",
            () => "How many blood particles spawn per hit. 0 = off, 1 = default (4–6 particles), 2 = dramatic spray.",
            0f, 2f, 0.1f);
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableToolCollisionHitstop, v => this.config.EnableToolCollisionHitstop = v,
            () => "Enable tool collision hitstop",
            () => "When a sword or pickaxe hits stone/metal, the player's body recoils backward (swing-back impulse) and extra hitstop frames fire. Simulates tool bouncing off hard material.");

        // ── Wood shatter VFX ──────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Wood Shatter VFX");
        api.AddParagraph(this.ModManifest, () =>
            "When an axe or sword hits a wooden surface (trees, fences, stumps, crates, wood furniture), " +
            "brown wood splinter particles burst from the impact point, and the tool briefly hitstops (stops mid-swing). " +
            "Models the Source Engine 'weapon stops on hit' behaviour for non-hard surfaces.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWoodShatterEffects, v => this.config.EnableWoodShatterEffects = v,
            () => "Enable wood shatter VFX",
            () => "Wood splinter particles + tool swing-stop on wooden surfaces.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.WoodShatterIntensity, v => this.config.WoodShatterIntensity = v,
            () => "Wood shatter intensity",
            () => "Particle count multiplier. 1 = 4-8 splinters (default), 2 = dramatic chunk shower.", 0f, 2f, 0.1f);

        // ── Typed physics debris ──────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Typed Physics Debris");
        api.AddParagraph(this.ModManifest, () =>
            "Material-matched physics particles: stone chunks from rocks/ore/geodes, wood splinters and " +
            "sawdust from trees and stumps. Particles arc under gravity, bounce once, scatter when walked through, " +
            "and fade out over their lifetime. Complements the existing radial debris system.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableTypedPhysicsDebris, v => this.config.EnableTypedPhysicsDebris = v,
            () => "Enable typed physics debris",
            () => "Stone chunks, wood splinters, and sawdust that arc, bounce, scatter on walk-through, and fade.");
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.TypedDebrisLifetimeTicks,
            v  => this.config.TypedDebrisLifetimeTicks = (int)v,
            () => "Debris lifetime (ticks)",
            () => "How long debris particles remain before fading. 1200 = 20 s, 1500 = 25 s (default), 1800 = 30 s.",
            1200f, 1800f, 60f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.TypedDebrisScatterStrength, v => this.config.TypedDebrisScatterStrength = v,
            () => "Debris scatter strength",
            () => "How strongly debris scatters when you walk through it. 0 = no scatter, 1 = default, 2 = very strong.",
            0f, 2f, 0.1f);
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableTreeFallImpulse, v => this.config.EnableTreeFallImpulse = v,
            () => "Enable tree-fall thud impulse",
            () => "Apply a body/hair impulse to simulate screen-shake when a felled tree hits the ground.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.TreeFallImpulseStrength, v => this.config.TreeFallImpulseStrength = v,
            () => "Tree-fall impulse strength",
            () => "Intensity of the player body/hair impulse on tree thud. 0 = off, 1 = default. Scales with distance.",
            0f, 2f, 0.1f);

        // ── Wing physics ──────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Wing Physics (HDT Multi-Bone)");
        api.AddParagraph(this.ModManifest, () =>
            "4-bone per-wing spring chain (root → inner → outer → tip) for all winged creatures. " +
            "Inspired by Source Engine Jiggle Bones and Skyrim HDT wing setups. " +
            "Each wing has independent cascade lag: root reacts first, tip follows ~6 ticks later. " +
            "Works for bats, dragons, flying bugs, birds, and all modded winged creatures.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableWingPhysics, v => this.config.EnableWingPhysics = v,
            () => "Enable wing physics",
            () => "Multi-bone wing chain for all winged creatures.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.WingChainStiffness, v => this.config.WingChainStiffness = v,
            () => "Wing chain stiffness",
            () => "Lower = floppy membrane wings (bat, dragon). Higher = rigid feathered wings (bird). Range 0.04–0.25.", 0.04f, 0.25f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.WingChainDamping, v => this.config.WingChainDamping = v,
            () => "Wing chain damping",
            () => "Lower = more wing flutter cycles. Higher = quick settle. Range 0.03–0.20.", 0.03f, 0.20f, 0.01f);

        // ── Fur physics ───────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Fur Physics (HDT Surface Ripple)");
        api.AddParagraph(this.ModManifest, () =>
            "Surface fur ripple chain for all furry creatures and farm animals. " +
            "Fur propagates a wave from base-to-tip as the creature moves. " +
            "Different from hair — fur hugs the body surface (higher chain influence, gentler attenuation). " +
            "Works for wolves, bears, cats, dogs, sheep, rabbits, and all modded furry creatures.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableFurPhysics, v => this.config.EnableFurPhysics = v,
            () => "Enable fur physics",
            () => "Surface fur ripple chain for furry creatures and woolly/fluffy farm animals.");
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.FurChainSegments, v => this.config.FurChainSegments = (int)Math.Round(v),
            () => "Fur chain segments",
            () => "Number of fur spring links. 2 = coarse, 6 = smooth ripple wave.", 2f, 6f, 1f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FurChainStiffness, v => this.config.FurChainStiffness = v,
            () => "Fur chain stiffness",
            () => "Lower = very fluffy/loose fur. Higher = tight smooth coat.", 0.04f, 0.30f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.FurChainDamping, v => this.config.FurChainDamping = v,
            () => "Fur chain damping", () => "Fur ripple decay rate.", 0.03f, 0.20f, 0.01f);

        // ── Tail physics ──────────────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Tail Physics (HDT Pendant Chain)");
        api.AddParagraph(this.ModManifest, () =>
            "Multi-bone pendant tail chain for all tailed creatures. " +
            "Lateral wag is emphasized (tails wag side-to-side more than hair flows). " +
            "Base bone is stiffer, tip softens for natural tail-tip flutter. " +
            "Works for wolves, cats, dogs, dragons, cows, horses, pigs, and all modded tailed creatures.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableTailPhysics, v => this.config.EnableTailPhysics = v,
            () => "Enable tail physics",
            () => "Multi-bone tail chain for all tailed creatures and farm animals.");
        api.AddNumberOption(this.ModManifest,
            () => (float)this.config.TailChainSegments, v => this.config.TailChainSegments = (int)Math.Round(v),
            () => "Tail chain segments",
            () => "Number of tail spring links. 2 = stub tail, 6 = long serpentine dragon tail.", 2f, 6f, 1f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.TailChainStiffness, v => this.config.TailChainStiffness = v,
            () => "Tail stiffness",
            () => "Lower = whippy serpentine tail. Higher = stiff stubby tail.", 0.04f, 0.30f, 0.01f);
        api.AddNumberOption(this.ModManifest,
            () => this.config.TailChainDamping, v => this.config.TailChainDamping = v,
            () => "Tail damping", () => "Tail oscillation decay rate.", 0.03f, 0.20f, 0.01f);

        // ── Animal bone physics ───────────────────────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Animal Bone Physics");
        api.AddParagraph(this.ModManifest, () =>
            "Per-animal spring bones (ears, snout/beak, body) for all farm animals. " +
            "Rabbit ears flop independently on jumps. Chicken combs bob on pecks. " +
            "Cow ears flick, pig snouts bob on sniffs. Horse ears prick on acceleration. " +
            "Anatomically-correct spring constants per species (heavy = stiffer, light = floppy).");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableAnimalBonePhysics, v => this.config.EnableAnimalBonePhysics = v,
            () => "Enable animal bone physics",
            () => "Per-bone springs for ears, snout, and body on all farm animals.");
        api.AddNumberOption(this.ModManifest,
            () => this.config.AnimalBoneStrength, v => this.config.AnimalBoneStrength = v,
            () => "Animal bone strength",
            () => "Overall intensity multiplier. 0 = off, 1 = default, 2 = maximum jiggle.", 0f, 2f, 0.05f);

        // ── Nude sprite + gender-swap detection ───────────────────────────────
        api.AddSectionTitle(this.ModManifest, () => "Nude & Gender-Swap Sprite Detection");
        api.AddParagraph(this.ModManifest, () =>
            "When a nudity/body mod replaces a sprite with a nude version (texture name contains keywords " +
            "like 'nude', 'naked', 'bare', 'undressed'), clothing dampening is removed so full-strength " +
            "body physics apply even if clothing items are still equipped in the inventory. " +
            "Compatible with all nude replacer mods, body mods, and NSFW texture packs. " +
            "When a gender-swap sprite is detected (texture name contains 'genderswap', 'femboy', 'female', " +
            "etc.), the physics profile is automatically flipped so the correct jiggle model is applied.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableNudePhysicsBoost, v => this.config.EnableNudePhysicsBoost = v,
            () => "Enable nude sprite physics boost",
            () => "Remove clothing dampening when a nude sprite is detected. Full body physics apply.");
        api.AddBoolOption(this.ModManifest,
            () => this.config.EnableGenderSwapDetection, v => this.config.EnableGenderSwapDetection = v,
            () => "Enable gender-swap sprite detection",
            () => "Automatically flip physics profile (feminine/masculine) when sprite texture signals a gender swap.");

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
