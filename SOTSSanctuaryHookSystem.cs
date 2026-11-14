using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Terraria.WorldBuilding;

namespace InfernumSOTSSanctuaryAdjuster
{
    public class SOTSSanctuaryHookSystem : ModSystem
    {
        private ILHook ilHook;
        private static FieldInfo fiSpawnPos;
        private static FieldInfo fiRectangle;
        private static MethodInfo miSetRect;
        private static MethodInfo miGenerate;

        // SOTS generates structures extending beyond its central
        // 800-tile Sanctuary region.
        private const int MinimumSafeMargin = 610;

        public override void Load()
        {
            if (!ModLoader.TryGetMod("SOTS", out Mod sots))
            {
                Mod.Logger.Warn("SOTS is not loaded. Sanctuary adjustment disabled.");
                return;
            }

            try
            {
                Assembly assembly = sots.GetType().Assembly;

                Type helperType = assembly.GetType("SOTS.WorldgenHelpers.SanctuaryWorldgenHelper", throwOnError: false);

                helperType ??= assembly.GetTypes().FirstOrDefault(t => t.Name == "SanctuaryWorldgenHelper");

                if (helperType == null)
                {
                    Mod.Logger.Error("Could not locate SanctuaryWorldgenHelper.");
                    return;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                fiSpawnPos = helperType.GetField("SpawnPos", flags);
                fiRectangle = helperType.GetField("Rectangle", flags);
                miSetRect = helperType.GetMethod("SetRect", flags);
                miGenerate = helperType.GetMethod("GenerateSanctuary", flags);

                if (fiSpawnPos == null || fiRectangle == null || miSetRect == null || miGenerate == null)
                {
                    Mod.Logger.Error("SOTS Sanctuary API has changed. " + "Required fields or methods were not found.");
                    return;
                }

                if (fiSpawnPos.FieldType != typeof(int) || fiRectangle.FieldType != typeof(Rectangle) || miSetRect.ReturnType != typeof(Rectangle) || miSetRect.GetParameters().Length != 0 || miGenerate.ReturnType != typeof(void) || miGenerate.GetParameters().Length != 0)
                {
                    Mod.Logger.Error("SOTS Sanctuary API signatures have changed.");
                    return;
                }

                ilHook = new ILHook(miGenerate, Patch_GenerateSanctuary);

                Mod.Logger.Info("SOTS Sanctuary IL hook registered.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"Failed to install Sanctuary hook: {ex}");
            }
        }

        public override void Unload()
        {
            ilHook?.Dispose();
            ilHook = null;

            fiSpawnPos = null;
            fiRectangle = null;
            miSetRect = null;
            miGenerate = null;
        }

        private void Patch_GenerateSanctuary(ILContext il)
        {
            ILCursor cursor = new ILCursor(il);

            // SOTS resets SpawnPos, then calls InitTypes().
            // Our adjustment must happen AFTER InitTypes(),
            // but BEFORE the first SideOfWorld calculation.
            if (!cursor.TryGotoNext(MoveType.After, instruction => instruction.MatchCallOrCallvirt(out var method) && method.Name == "InitTypes" && method.DeclaringType?.FullName == "SOTS.WorldgenHelpers.SanctuaryWorldgenHelper"))
            {
                Mod.Logger.Error("Sanctuary IL patch failed: " + "could not locate InitTypes() call.");
                return;
            }

            Mod.Logger.Info("Sanctuary IL insertion point located successfully.");

            cursor.EmitDelegate<Action>(ApplySanctuaryPosition);
        }

        private void ApplySanctuaryPosition()
        {
            try
            {
                SanctuarySpawnConfig cfg = ModContent.GetInstance<SanctuarySpawnConfig>();

                bool compatible = !cfg.OnlyWhenSpookyOrInfernum || ModLoader.HasMod("Spooky") || ModLoader.HasMod("InfernumMode");

                if (!compatible)
                {
                    Mod.Logger.Info("Compatibility condition not met. " + "Using SOTS default Sanctuary placement.");
                    return;
                }

                int maxX = Main.maxTilesX;
                int center = maxX / 2;
                bool jungleRight = GenVars.JungleX > center;

                int position = CalculatePosition(cfg, maxX, jungleRight);

                // The original configurable margin is preserved,
                // but a minimum is enforced to accommodate the
                // Sanctuary's outer structures.
                int margin = Math.Max(cfg.MinMarginTiles, MinimumSafeMargin);

                margin = Math.Clamp(margin, 0, center - 1);

                int originalPosition = position;

                position = Math.Clamp(position, margin, maxX - margin);

                if (position != originalPosition)
                {
                    Mod.Logger.Warn($"Sanctuary position {originalPosition} " + $"was clamped to {position} to keep " + "the structure within world bounds.");
                }

                // Apply the new position before SetRect().
                fiSpawnPos.SetValue(null, position);

                Rectangle rectangle = (Rectangle)miSetRect.Invoke(null, null);

                fiRectangle.SetValue(null, rectangle);

                // Verify that SOTS retained the selected position.
                int actualPosition = (int)fiSpawnPos.GetValue(null);

                if (actualPosition != position)
                {
                    Mod.Logger.Warn($"SOTS changed Sanctuary SpawnPos from " + $"{position} to {actualPosition} " +"while recalculating its rectangle.");

                    // Keep the rectangle consistent with the actual position rather than forcing a potentially conflicting portal location.
                    rectangle = (Rectangle)miSetRect.Invoke(null, null);
                    fiRectangle.SetValue(null, rectangle);
                }

                Mod.Logger.Info($"Sanctuary position: {actualPosition}, " + $"world width: {maxX}, " + $"jungle right: {jungleRight}, " + $"rectangle: {rectangle}.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"Failed to adjust Sanctuary position: {ex}");
            }
        }

        private static int CalculatePosition(SanctuarySpawnConfig cfg, int maxX, bool jungleRight)
        {
            int center = maxX / 2;

            switch (cfg.PositionMode)
            {
                case PositionMode.Fraction:
                    {
                        float fraction = jungleRight ? cfg.LeftFraction : cfg.RightFraction;

                        fraction = Math.Clamp(fraction, 0f, 1f);

                        return (int)Math.Round(fraction * maxX);
                    }

                case PositionMode.Tiles:
                    {
                        return jungleRight ? cfg.LeftTiles : maxX - cfg.RightTilesFromRightEdge;
                    }

                case PositionMode.Nudge:
                default:
                    {
                        int basePosition = jungleRight ? maxX / 8 : maxX * 7 / 8;

                        int nudge = Math.Max(0, cfg.NudgeTilesTowardCenter);

                        int direction = Math.Sign(center - basePosition);

                        return basePosition + direction * nudge;
                    }
            }
        }
    }
}
