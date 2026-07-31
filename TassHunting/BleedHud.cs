// THE BLEEDING BOX (2026-07-29). Same shape as the effect box XSkills shows for
// its healing-over-time ability, because that is the shape players here already
// read: a small gray panel with a red drop, how many wounds are open and how
// long until they close. Nothing else - a player who sees a blood drop counting
// down works out what it means, and the panel stays out of the way.
//
// HOW XSKILLS DOES IT (decompiled xlib 1.0.28, XLib.XEffects):
//   - EffectFrame : HudElement holds one EffectBox per active effect, and a
//     100ms client tick calls Update() - which closes the dialog when nothing
//     is active, re-composes when the effect COUNT changes, and otherwise just
//     pushes new text into the existing rows.
//   - EffectBox is a GuiElement: 32x32 icon at the left, a
//     GuiElementDynamicText beside it reading "Name(stacks) 12.4s".
// This file is that recipe with one effect in it, and no XLib dependency: the
// data comes off our own watched attributes so it works with or without
// XSkills installed. Ours defaults to the RIGHT-middle of the screen precisely
// because the XSkills frame lives at the left-middle - the two never overlap.
// (XSkills also opens a description panel on hover. Deliberately not copied -
// user call 2026-07-29: the big description box reads as clutter.)
//
// EVERYTHING HERE IS CLIENT-SIDE AND READ-ONLY. It reads two attributes the
// server already publishes on the player's own entity: "thbleed" (open wounds)
// and "thbleedsecs" (seconds until the last one closes, -1 while an arrow pins
// one open).

using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace TassHunting
{
    /// <summary>Client owner of the bleeding box: builds the hud and ticks it.</summary>
    public class BleedHudSystem : ModSystem
    {
        private ICoreClientAPI capi;
        private BleedHud hud;
        private long tickId;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            try
            {
                hud = new BleedHud(api);
                tickId = api.Event.RegisterGameTickListener(OnTick, 200);
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] bleeding box unavailable: {0}", ex.Message); }
        }

        private void OnTick(float dt)
        {
            try { hud?.Update(); }
            catch (Exception ex)
            {
                // One bad frame must not spam every tick: drop the hud and say so once.
                capi?.Logger.Warning("[TassHunting] bleeding box stopped: {0}", ex.Message);
                try { if (tickId != 0) capi.Event.UnregisterGameTickListener(tickId); } catch { }
                tickId = 0;
                try { hud?.Dispose(); } catch { }
                hud = null;
            }
        }

        /// <summary>The live box, for the client smoke test to look at. Null if it failed to build.</summary>
        public BleedHud Hud => hud;

        public override void Dispose()
        {
            try { if (capi != null && tickId != 0) capi.Event.UnregisterGameTickListener(tickId); } catch { }
            tickId = 0;
            try { hud?.Dispose(); } catch { }
            hud = null; capi = null;
            base.Dispose();
        }
    }

    /// <summary>The box: red drop, wound count, time left. Nothing to click, nothing to hover.</summary>
    public class BleedHud : HudElement
    {
        // Wide enough for the longest label the box can show ("Bleeding(10) 2.0m") and no wider -
        // the first render reserved 190 and left a third of the panel empty.
        private const int TextWidth = 140;
        private const int RowHeight = 32;
        // Sit a little off the screen edge rather than flush against it.
        private const int EdgeInset = 8;

        private GuiElementDynamicText rowText;

        private bool composed;
        private int shownWounds = int.MinValue;
        private int shownSecs = int.MinValue;
        private string shownPosition;

        public BleedHud(ICoreClientAPI capi) : base(capi) { }

        public override string ToggleKeyCombinationCode => null;
        public override bool Focusable => false;

        /// <summary>The text currently in the row - what the player is reading.</summary>
        public string Label { get; private set; }

        /// <summary>Ticked from the client; owns show, hide and the text refresh.</summary>
        public void Update()
        {
            var cfg = HuntingModSystem.Cfg;
            var ent = capi?.World?.Player?.Entity;
            if (cfg == null || !cfg.BleedHudEnabled || ent?.WatchedAttributes == null) { Hide(); return; }

            int wounds = ent.WatchedAttributes.GetInt("thbleed", 0);
            if (wounds <= 0) { Hide(); return; }
            int secs = ent.WatchedAttributes.GetInt("thbleedsecs", 0);

            // Re-compose only when the layout choice moved; the row text is pushed in place
            // otherwise, exactly like the XSkills frame does.
            if (!composed || shownPosition != cfg.BleedHudPosition) Compose(cfg);
            if (!composed) return;

            if (wounds != shownWounds || secs != shownSecs)
            {
                shownWounds = wounds; shownSecs = secs;
                Label = RowLabel(wounds, secs);
                rowText.SetNewText(Label, false, false, false);
            }

            if (!IsOpened()) TryOpen();
        }

        /// <summary>"Bleeding(3) 42.0s" - the XSkills Effect.GetName() shape.</summary>
        public static string RowLabel(int wounds, int secs)
        {
            string name = Lang.Get("tasshunting:bleedhud-name");
            string stacks = wounds > 1 ? "(" + wounds + ")" : "";
            string time = secs < 0 ? Lang.Get("tasshunting:bleedhud-pinned-short") : TimeToString(secs);
            return name + stacks + " " + time;
        }

        /// <summary>Seconds to a short human string. Mirrors XLib's Effect.TimeToString.</summary>
        public static string TimeToString(int seconds)
        {
            if (seconds <= 60) return seconds.ToString("N0") + "s";
            if (seconds <= 3600) return (seconds / 60f).ToString("N1") + "m";
            return (seconds / 3600f).ToString("N1") + "h";
        }

        private void Hide()
        {
            if (IsOpened()) TryClose();
            shownWounds = int.MinValue; shownSecs = int.MinValue; Label = null;
        }

        private void Compose(HuntingConfig cfg)
        {
            var align = ParseAlignment(cfg.BleedHudPosition);
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(align)
                .WithFixedAlignmentOffset(cfg.BleedHudOffsetX + InsetX(align),
                                          cfg.BleedHudOffsetY + InsetY(align));

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.HalfPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var rowBounds = ElementBounds.Fixed(0, 0, RowHeight + TextWidth, RowHeight);
            bgBounds.WithChildren(rowBounds);
            dialogBounds.WithChild(bgBounds);

            var iconBounds = ElementBounds.Fixed(0, 0, RowHeight, RowHeight).WithParent(rowBounds);
            var textBounds = ElementBounds.Fixed(RowHeight + 4, 4, TextWidth, RowHeight).WithParent(rowBounds);

            rowText = new GuiElementDynamicText(capi, RowLabel(1, 0), CairoFont.WhiteDetailText(), textBounds);

            SingleComposer = capi.Gui.CreateCompo("tasshuntingbleedhud", dialogBounds)
                .AddGrayBG(bgBounds)
                .AddStaticElement(new BleedIcon(capi, iconBounds), null)
                .AddInteractiveElement(rowText, null)
                .Compose();

            composed = true;
            shownPosition = cfg.BleedHudPosition;
            shownWounds = int.MinValue; shownSecs = int.MinValue;
        }

        /// <summary>Nudge away from whichever screen edge the box is anchored to.</summary>
        private static int InsetX(EnumDialogArea a)
        {
            if (a == EnumDialogArea.LeftTop || a == EnumDialogArea.LeftMiddle || a == EnumDialogArea.LeftBottom) return EdgeInset;
            if (a == EnumDialogArea.RightTop || a == EnumDialogArea.RightMiddle || a == EnumDialogArea.RightBottom) return -EdgeInset;
            return 0;
        }

        private static int InsetY(EnumDialogArea a)
        {
            if (a == EnumDialogArea.LeftTop || a == EnumDialogArea.RightTop || a == EnumDialogArea.CenterTop) return EdgeInset;
            if (a == EnumDialogArea.LeftBottom || a == EnumDialogArea.RightBottom || a == EnumDialogArea.CenterBottom) return -EdgeInset;
            return 0;
        }

        private static EnumDialogArea ParseAlignment(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "lefttop": return EnumDialogArea.LeftTop;
                case "leftbottom": return EnumDialogArea.LeftBottom;
                case "righttop": return EnumDialogArea.RightTop;
                case "rightmiddle": return EnumDialogArea.RightMiddle;
                case "rightbottom": return EnumDialogArea.RightBottom;
                case "centertop": return EnumDialogArea.CenterTop;
                case "centerbottom": return EnumDialogArea.CenterBottom;
                default: return EnumDialogArea.LeftMiddle;
            }
        }

        /// <summary>
        /// The blood drop, drawn straight onto the panel with Cairo.
        ///
        /// It started as a shipped svg registered through Gui.Icons.CustomIcons, the way vanilla
        /// does its character-screen icons - and the real-client test caught that coming back with
        /// null Data ("Asset Data is null. Is the asset loaded?") at the moment the box composes,
        /// which took the whole panel down. Drawing it here needs no asset, no load order and no
        /// icon registry, so there is nothing left to go missing.
        /// </summary>
        private class BleedIcon : GuiElement
        {
            public BleedIcon(ICoreClientAPI capi, ElementBounds bounds) : base(capi, bounds) { }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                // The icon is decoration - it must never be able to take the panel down with it.
                try { DrawDrop(ctx, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight); }
                catch (Exception) { }
            }

            /// <summary>Pointed top, round belly, sized to fit the given box.</summary>
            private static void DrawDrop(Context ctx, double x, double y, double w, double h)
            {
                double pad = Math.Min(w, h) * 0.18;
                double cx = x + w / 2.0;
                double top = y + pad;
                double r = Math.Min(w, h) / 2.0 - pad;
                if (r <= 0.5) return;
                double cy = y + h - pad - r; // belly centre

                ctx.NewPath();
                ctx.MoveTo(cx, top);
                ctx.CurveTo(cx + r * 0.9, cy - r * 0.9, cx + r, cy - r * 0.5, cx + r, cy);
                ctx.Arc(cx, cy, r, 0, Math.PI); // belly: right, down, left
                ctx.CurveTo(cx - r, cy - r * 0.5, cx - r * 0.9, cy - r * 0.9, cx, top);
                ctx.ClosePath();

                ctx.SetSourceRGBA(0.72, 0.09, 0.11, 1.0); // fresh blood
                ctx.FillPreserve();
                ctx.SetSourceRGBA(0.20, 0.02, 0.03, 1.0); // dark rim so it reads on a light panel
                ctx.LineWidth = 1.2;
                ctx.Stroke();
            }
        }
    }
}
