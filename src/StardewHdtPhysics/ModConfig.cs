namespace StardewHdtPhysics;

public sealed class ModConfig
{
    // ── System toggles ────────────────────────────────────────────────────────
    public bool EnableBodyPhysics { get; set; } = true;
    public bool EnableHairPhysics { get; set; } = true;
    public bool EnableRagdollKnockback { get; set; } = true;
    public bool EnableIdleMotion { get; set; } = true;
    public bool EnableMonsterBodyPhysics { get; set; } = true;
    public bool EnableMonsterRagdoll { get; set; } = true;
    public bool EnableNpcSwordKnockdown { get; set; } = true;
    public bool EnableFarmAnimalPhysics { get; set; } = true;
    public bool EnableEnvironmentalPhysics { get; set; } = true;
    public bool EnableItemCollisionPhysics { get; set; } = true;
    public bool EnableWindDetection { get; set; } = true;
    public bool EnableHitDirectionalImpulse { get; set; } = true;
    public bool EnableClothingPhysicsModifier { get; set; } = true;
    public bool EnableHitstopEffect { get; set; } = true;
    public bool EnableDebrisPhysics { get; set; } = true;
    /// <summary>Apply jiggle physics to the farmer while mounted on horse.</summary>
    public bool EnableHorseRiderPhysics { get; set; } = true;
    /// <summary>Apply gentle body impulse when the player walks into an NPC.</summary>
    public bool EnableProximityCollisionImpulse { get; set; } = true;
    /// <summary>Hair droops heavily for a few ticks after the farmer exits water.</summary>
    public bool EnableWaterEmergenceHairDroop { get; set; } = true;
    /// <summary>Dragon archetype physics: wingbeat bursts, tail thrash, ground rumble. Used for Druid mod dragons and all dragon-type monsters.</summary>
    public bool EnableDragonPhysics { get; set; } = true;
    /// <summary>Body/hair impulse reacts to casting magic spells and using magic tools. Compatible with Magic mod, Druid mod, SpaceCore skills, and any magic-named tool.</summary>
    public bool EnableMagicCastPhysics { get; set; } = true;
    /// <summary>Brief celebration body bounce + hair toss when the farmer gains a skill level (works with SpaceCore custom skills too).</summary>
    public bool EnableSkillLevelUpBounce { get; set; } = true;
    /// <summary>Boosts breast lateral splay and butt jiggle when the character is facing down (away from camera). Most visible from behind.</summary>
    public bool EnableDirectionalBodyBoost { get; set; } = true;
    /// <summary>Grass, crops, and weeds bend and part when any character or monster walks or rolls through them. All creatures have dynamic collision with terrain features.</summary>
    public bool EnableCropWeedCollisionPhysics { get; set; } = true;
    /// <summary>All NPCs, monsters, and farm animals contribute to dynamic grass bending (not just the player).</summary>
    public bool EnableAllCreatureGrassCollision { get; set; } = true;
    /// <summary>
    /// Separate physics layer for clothing on top of body physics.
    /// Flowy clothing (dresses, robes, capes, skirts) trails and billows behind movement and is pushed by wind/rain.
    /// Tight clothing (shorts, tights, bikini, fitted) closely tracks the body with minimal extra sway.
    /// No body-physics clipping: clothing offset is added on top and blended at reduced scale.
    /// Works with all vanilla and modded clothing.
    /// </summary>
    public bool EnableClothingFlowPhysics { get; set; } = true;
    /// <summary>Brief body + hair impulse when the farmer steps through a door or warp point.</summary>
    public bool EnableWarpStepImpulse { get; set; } = true;
    /// <summary>Body bounce when the farmer starts eating or drinking.</summary>
    public bool EnableEatingBounce { get; set; } = true;
    /// <summary>Brief body/hair flinch when lightning strikes outdoors.</summary>
    public bool EnableLightningFlinch { get; set; } = true;
    /// <summary>
    /// Fishing rod cast, reel, and catch all trigger body/hair impulses:
    /// cast = forward lean + hair sweep, nibble = body twitch, catch = body snap-back.
    /// Works with all fishing rods including mod-added ones.
    /// </summary>
    public bool EnableFishingPhysics { get; set; } = true;
    /// <summary>
    /// When casting a fishing rod, check if any NPC, monster, or farm animal is in the
    /// cast trajectory. If so, apply a harmless cosmetic knockdown (bobber bonk).
    /// The NPC/animal stumbles and sits down briefly — no damage or relationship impact.
    /// </summary>
    public bool EnableBobberBonk { get; set; } = true;

    // ── Combat hit VFX ────────────────────────────────────────────────────────
    /// <summary>
    /// Blood particle spray when a weapon or tool hits a humanoid, monster, or farm animal.
    /// Uses Game1 radial debris to spawn red particles at the hit position.
    /// Toggle this off for a cleaner or less violent experience.
    /// </summary>
    public bool EnableBloodSplatterEffects { get; set; } = true;
    /// <summary>
    /// Spark particle burst when a weapon or pickaxe hits stone, metal, ore, geodes, or armored monsters.
    /// Scales with tool weight: pickaxe produces the most sparks, sword produces fewer.
    /// </summary>
    public bool EnableSparkEffects { get; set; } = true;
    /// <summary>
    /// Green slime spray VFX + extra body jiggle impulse when a weapon hits a slime-type monster.
    /// Uses colored radial debris to simulate slime splatter at the impact point.
    /// </summary>
    public bool EnableSlimeSprayEffects { get; set; } = true;
    /// <summary>
    /// When a weapon or pickaxe hits a hard surface (stone wall, metal furniture, rock object),
    /// applies a reverse swing-back impulse to the player body + additional hitstop frames.
    /// Simulates the tool bouncing back off a hard material.
    /// </summary>
    public bool EnableToolCollisionHitstop { get; set; } = true;
    /// <summary>
    /// Overall intensity multiplier for blood splatter particles. 0 = off, 1 = default, 2 = dramatic.
    /// </summary>
    public float BloodSplatterIntensity { get; set; } = 1.0f;

    // ── Feminine body strengths ───────────────────────────────────────────────
    public float FemaleBreastStrength { get; set; } = 0.75f;
    public float FemaleButtStrength { get; set; } = 0.5f;
    public float FemaleThighStrength { get; set; } = 0.4f;
    public float FemaleBellyStrength { get; set; } = 0.3f;

    // ── Masculine body strengths ──────────────────────────────────────────────
    public float MaleButtStrength { get; set; } = 0.45f;
    public float MaleGroinStrength { get; set; } = 0.45f;
    public float MaleThighStrength { get; set; } = 0.35f;
    public float MaleBellyStrength { get; set; } = 0.25f;

    // ── HDT Hair physics ──────────────────────────────────────────────────────
    public float HairStrength { get; set; } = 0.55f;
    public float HairWindBoostOutdoors { get; set; } = 1.0f;
    public float HairDampeningIndoors { get; set; } = 0.45f;

    // ── Ragdoll & knockback ───────────────────────────────────────────────────
    public float RagdollChanceUnderLowHealth { get; set; } = 0.5f;
    public float RagdollKnockbackStrength { get; set; } = 1.5f;
    public float NpcSwordKnockdownChance { get; set; } = 0.4f;
    /// <summary>Player HP must be at or below this value for ragdoll to activate (0–100).</summary>
    public float RagdollHealthThreshold { get; set; } = 30f;
    /// <summary>Chance (0–1) each clothing slot (hat, shirt, pants, shoes) flies off on ragdoll.</summary>
    public float RagdollClothingScatterChance { get; set; } = 0.10f;
    /// <summary>Chance (0–1) that one inventory item is dropped during ragdoll.</summary>
    public float RagdollItemDropChance { get; set; } = 0.15f;

    // ── Monster archetype physics ─────────────────────────────────────────────
    public float MonsterArchetypeStrength { get; set; } = 0.55f;

    // ── Farm animal physics ───────────────────────────────────────────────────
    public float FarmAnimalPhysicsStrength { get; set; } = 0.45f;

    // ── Environmental physics ─────────────────────────────────────────────────
    public float EnvironmentalPhysicsStrength { get; set; } = 0.5f;

    // ── Debris physics ────────────────────────────────────────────────────────
    public float DebrisPhysicsStrength { get; set; } = 0.65f;

    // ── Dragon physics ────────────────────────────────────────────────────────
    /// <summary>Overall intensity of dragon archetype body, wing, and tail physics. 1.2 = powerful by default to match dragon scale.</summary>
    public float DragonPhysicsStrength { get; set; } = 1.2f;

    // ── Magic cast physics ────────────────────────────────────────────────────
    /// <summary>Intensity of body/hair impulse when casting a spell or using a magic tool. Higher values = more dramatic hair fly and body reaction.</summary>
    public float MagicCastImpulseStrength { get; set; } = 1.0f;

    // ── Crop/weed collision ───────────────────────────────────────────────────
    /// <summary>How strongly grass, crops, and weeds bend and part when walked through or ragdolled into.</summary>
    public float CropWeedCollisionStrength { get; set; } = 0.65f;

    // ── Clothing flow physics ─────────────────────────────────────────────────
    /// <summary>
    /// Overall strength of the clothing flow physics layer.
    /// Flowy clothing (dresses, capes) swings wider and trails longer.
    /// Tight clothing (shorts, tights) has a tighter, quicker follow.
    /// 0 = no extra clothing sway, 1 = default, 2 = very dramatic cloth movement.
    /// </summary>
    public float ClothingFlowStrength { get; set; } = 1.0f;

    // ── Wood shatter VFX ──────────────────────────────────────────────────────
    /// <summary>
    /// Wood splinter/shatter particle effect when axe or sword hits a wooden surface
    /// (trees, fences, wood furniture, stumps, crates).
    /// Toggle off for a cleaner visual experience.
    /// </summary>
    public bool EnableWoodShatterEffects { get; set; } = true;
    /// <summary>
    /// Intensity multiplier for wood shatter particle count.
    /// 0 = off, 1 = default (4–8 splinters), 2 = dramatic chunk shower.
    /// </summary>
    public float WoodShatterIntensity { get; set; } = 1.0f;

    // ── Wing physics ──────────────────────────────────────────────────────────
    /// <summary>
    /// Multi-bone wing physics for all winged creatures: bats, dragons, flying bugs, butterflies,
    /// birds, and any modded flying creature. Each wing has 4 spring bones (root→inner→outer→tip)
    /// producing a realistic fold/unfold wave on every wingbeat.
    /// </summary>
    public bool EnableWingPhysics { get; set; } = true;
    /// <summary>Wing spring stiffness. Lower = floppy membrane wings (bat). Higher = rigid feathered wings (bird).</summary>
    public float WingChainStiffness { get; set; } = 0.09f;
    /// <summary>Wing chain damping. Lower = more wing flutter cycles. Higher = quick settle.</summary>
    public float WingChainDamping { get; set; } = 0.08f;

    // ── Fur physics ───────────────────────────────────────────────────────────
    /// <summary>
    /// Surface fur ripple chain for all furry creatures: wolves, bears, cats, dogs, foxes,
    /// sheep, rabbits, and any modded furry creature or farm animal. Fur ripples as the
    /// creature moves and settles with a gentle wave when it stops.
    /// </summary>
    public bool EnableFurPhysics { get; set; } = true;
    /// <summary>Number of fur chain segments (2–6). More segments = smoother ripple wave.</summary>
    public int FurChainSegments { get; set; } = 3;
    /// <summary>Fur chain spring stiffness. Lower = very fluffy loose fur. Higher = tight smooth coat.</summary>
    public float FurChainStiffness { get; set; } = 0.14f;
    /// <summary>Fur chain damping.</summary>
    public float FurChainDamping { get; set; } = 0.10f;

    // ── Tail physics ──────────────────────────────────────────────────────────
    /// <summary>
    /// Multi-bone tail chain for all tailed creatures: wolves, cats, dogs, dragons,
    /// foxes, cows, horses, pigs, and any modded tailed creature or farm animal.
    /// Tail wags side-to-side and the tip lags the base with spring-damper lag.
    /// </summary>
    public bool EnableTailPhysics { get; set; } = true;
    /// <summary>Number of tail chain segments (2–6). Default 4: root→mid1→mid2→tip.</summary>
    public int TailChainSegments { get; set; } = 4;
    /// <summary>Tail spring stiffness. Lower = whippy serpentine tail. Higher = stiff stubby tail.</summary>
    public float TailChainStiffness { get; set; } = 0.11f;
    /// <summary>Tail chain damping.</summary>
    public float TailChainDamping { get; set; } = 0.09f;

    // ── Animal bone physics ───────────────────────────────────────────────────
    /// <summary>
    /// Per-animal spring bone set (ears, snout, body) for all farm animals.
    /// Rabbit ears flop independently, chicken combs bob on pecks, cow ears flick,
    /// pig snouts bob on sniffs. All controlled by anatomically correct spring constants.
    /// </summary>
    public bool EnableAnimalBonePhysics { get; set; } = true;
    /// <summary>Overall strength multiplier for animal bone physics. 0 = off, 1 = default, 2 = maximum.</summary>
    public float AnimalBoneStrength { get; set; } = 0.55f;

    // ── Furniture collision physics ───────────────────────────────────────────
    /// <summary>
    /// When the player walks into or swings a tool at furniture/objects, they produce a spring
    /// recoil — the object appears to shift slightly then spring back. Also triggers wood shatter
    /// VFX on wood furniture when struck with an axe or sword.
    /// </summary>
    public bool EnableFurnitureCollisionPhysics { get; set; } = true;
    /// <summary>Overall strength of furniture collision spring recoil.</summary>
    public float FurnitureCollisionStrength { get; set; } = 0.45f;

    // ── Wood shatter VFX ──────────────────────────────────────────────────────

    // ── Idle motion ───────────────────────────────────────────────────────────
    /// <summary>
    /// Ticks between idle physics bursts (body sways, leans, stretches). Default 90 = ~1.5 seconds.
    /// Lower values = physics visible more often. Minimum recommended: 30.
    /// </summary>
    public int IdleMotionIntervalTicks { get; set; } = 90;
    /// <summary>
    /// Overall strength multiplier for idle motion impulses.
    /// 0.5 = subtle, 1.0 = default, 2.0 = very expressive idle movements.
    /// </summary>
    public float IdleMotionStrength { get; set; } = 1.0f;

    // ── Spring-damper physics engine ─────────────────────────────────────────
    /// <summary>
    /// Per-bone spring stiffness (k). Controls how fast each bone returns to rest.
    /// Lower = more jiggly / longer oscillation. Higher = firm snap-back.
    /// Range 0.04–0.35. Default 0.12 = realistic soft jelly.
    /// </summary>
    public float BoneStiffness { get; set; } = 0.12f;

    /// <summary>
    /// Per-bone damping coefficient (c). Controls how quickly oscillation dies out.
    /// Lower = more bouncing cycles. Higher = one-shot settle.
    /// Range 0.03–0.25. Default 0.08 = jelly with 3–4 visible oscillation cycles.
    /// </summary>
    public float BoneDamping { get; set; } = 0.08f;

    /// <summary>
    /// Hair chain spring stiffness. Lower = hair hangs looser / more flowing.
    /// Range 0.03–0.25. Default 0.07.
    /// </summary>
    public float HairChainStiffness { get; set; } = 0.07f;

    /// <summary>
    /// Hair chain damping. Lower = hair keeps swinging longer.
    /// Range 0.03–0.20. Default 0.07.
    /// </summary>
    public float HairChainDamping { get; set; } = 0.07f;

    /// <summary>
    /// Number of spring segments in the hair chain. More segments = smoother flowing cascade.
    /// Each additional segment adds a tiny simulation cost. Range 2–8. Default 5.
    /// </summary>
    public int HairChainSegments { get; set; } = 5;

    // ── Presets ───────────────────────────────────────────────────────────────
    public string Preset { get; set; } = "Default";

    /// <summary>
    /// Manual gender overrides keyed by NPC/farmer display name (case-insensitive).
    /// Accepted values: "Feminine", "Masculine", "Androgynous".
    /// Overrides take priority over all automatic sprite and gender detection.
    /// Edit config.json directly or via the GMCM page instruction text.
    /// Example: { "Krobus": "Feminine", "Sam": "Feminine" }
    /// </summary>
    public Dictionary<string, string> GenderOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
