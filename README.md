# Stardew-valley-hdt-hair-and-body-physics-

Prototype SMAPI mod scaffold for configurable body-motion, hair-motion, and low-health ragdoll-style knockback behavior.

## Implemented foundation
- **Physics engine loop** (`UpdateTicked`) that tracks movement vectors for player + humanoid NPCs.
- **Automatic profile detection** for masculine/feminine/androgynous physics by character mapping + fallback gender detection.
- **Optional GMCM integration** for sliders and toggles. The mod works without GMCM; GMCM only adds UI sliders/preset selection.
- **Per-part strength controls**:
  - Feminine: breast, butt, thigh, belly
  - Masculine: butt, groin, thigh, belly
- **Preset system** (`Soft`, `Default`, `High`) loaded from `assets/presets.json`.
- **Idle-motion impulse system** with tool-use safety guard (resets impulses when a tool button is pressed).
- **Indoor/outdoor hair response** with lower indoor motion and higher outdoor motion.
- **Low-health ragdoll-style knockback chance** (`< 30` health threshold and configurable chance).

## Files
- `src/StardewHdtPhysics/manifest.json`
- `src/StardewHdtPhysics/StardewHdtPhysics.csproj`
- `src/StardewHdtPhysics/ModEntry.cs`
- `src/StardewHdtPhysics/ModConfig.cs`
- `src/StardewHdtPhysics/SpriteProfileDetector.cs`
- `src/StardewHdtPhysics/GenericModConfigMenuApi.cs`
- `src/StardewHdtPhysics/assets/presets.json`
- `src/StardewHdtPhysics/assets/spriteProfiles.json`

## Notes
This repository did not previously include a build/test harness, so this change adds a minimal compile-ready SMAPI mod structure and data-driven hooks for extending sprite-specific profile detection and physics behavior.
