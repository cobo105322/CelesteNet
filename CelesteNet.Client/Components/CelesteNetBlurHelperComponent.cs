﻿using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MDraw = Monocle.Draw;

namespace Celeste.Mod.CelesteNet.Client.Components {
    public unsafe class CelesteNetBlurHelperComponent : CelesteNetGameComponent {

        public const int BLUR_SCALE = 3;
        public const int BLUR_WIDTH = UI_WIDTH / BLUR_SCALE;
        public const int BLUR_HEIGHT = UI_HEIGHT / BLUR_SCALE;

        public RenderTarget2D FakeRT;

        public RenderTarget2D BlurXRT;
        public RenderTarget2D BlurYRT;
        public RenderTarget2D BlurRT;

        public CelesteNetBlurHelperComponent(CelesteNetClientComponent context, Game game)
            : base(context, game) {

            UpdateOrder = 10000;
            Visible = false;
        }

        public void Rect(float x, float y, float width, float height, Color color) {
            int xi = (int) Math.Floor(x);
            int yi = (int) Math.Floor(y);
            int wi = (int) Math.Ceiling(x + width) - xi;
            int hi = (int) Math.Ceiling(y + height) - yi;

            Rectangle rect = new Rectangle(xi, yi, wi, hi);
            
            MDraw.SpriteBatch.Draw(
                BlurRT,
                rect, rect,
                Color.White * Math.Min(1f, color.A / 255f * 2f)
            );
            
            MDraw.Rect(xi, yi, wi, hi, color);
        }

        public override void Initialize() {
            base.Initialize();

            BlurXRT = new RenderTarget2D(GraphicsDevice, BLUR_WIDTH, BLUR_HEIGHT, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            BlurYRT = new RenderTarget2D(GraphicsDevice, BLUR_WIDTH, BLUR_HEIGHT, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
            BlurRT = new RenderTarget2D(GraphicsDevice, UI_WIDTH, UI_HEIGHT, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.DiscardContents);

            IL.Monocle.Engine.RenderCore += ILRenderCore;
            IL.Celeste.Level.Render += ILRenderLevel;
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);

            MainThreadHelper.Do(() => {
                IL.Monocle.Engine.RenderCore -= ILRenderCore;
                IL.Celeste.Level.Render -= ILRenderLevel;

                FakeRT?.Dispose();
                BlurXRT?.Dispose();
                BlurYRT?.Dispose();
                FakeRT = null;
            });
        }

        private RenderTarget2D GetFakeRT(RenderTarget2D realRT) {
            if (realRT != null)
                return realRT;

            if (FakeRT != null && (FakeRT.Width != Engine.ViewWidth || FakeRT.Height != Engine.ViewHeight)) {
                FakeRT.Dispose();
                FakeRT = null;
            }

            if (FakeRT == null) {
                FakeRT = new RenderTarget2D(
                    GraphicsDevice,
                    Engine.ViewWidth,
                    Engine.ViewHeight,
                    false,
                    SurfaceFormat.Color,
                    GraphicsDevice.PresentationParameters.DepthStencilFormat,
                    0,
                    RenderTargetUsage.DiscardContents
                );
            }

            return FakeRT;
        }

        private void ILRenderCore(ILContext il) {
            ILCursor c = new ILCursor(il);

            VariableDefinition vd_tmpRealVP = new VariableDefinition(il.Import(typeof(Viewport)));
            il.Body.Variables.Add(vd_tmpRealVP);
            VariableDefinition vd_tmpRealRT = new VariableDefinition(il.Import(typeof(RenderTarget2D)));
            il.Body.Variables.Add(vd_tmpRealRT);

            c.EmitDelegate<Func<Viewport>>(() => {
                Viewport tmpRealVP = Engine.Viewport;
                Engine.SetViewport(new Viewport(0, 0, Engine.ViewWidth, Engine.ViewHeight));
                return tmpRealVP;
            });
            c.Emit(OpCodes.Stloc, vd_tmpRealVP);

            c.GotoNext(i => i.MatchCallOrCallvirt(typeof(GraphicsDevice), "SetRenderTarget"));
            c.Emit(OpCodes.Dup);
            c.Emit(OpCodes.Stloc, vd_tmpRealRT);
            c.EmitDelegate<Func<RenderTarget2D, RenderTarget2D>>(GetFakeRT);

            c.GotoNext(i => i.MatchRet());
            c.Emit(OpCodes.Ldloc, vd_tmpRealVP);
            c.Emit(OpCodes.Ldloc, vd_tmpRealRT);
            c.EmitDelegate<Action<Viewport, RenderTarget2D>>((tmpRealVP, tmpRealRT) => {
                Engine.SetViewport(tmpRealVP);

                if (FakeRT != null) {
                    const int blurrad = 16;
                    const float blurdist = 0.6f;
                    Vector2 blurScale = new Vector2(BLUR_WIDTH / (float) tmpRealVP.Width, BLUR_HEIGHT / (float) tmpRealVP.Height);
                    Vector2 blurScaleInv = new Vector2(UI_WIDTH / (float) BLUR_WIDTH, UI_HEIGHT / (float) BLUR_HEIGHT);

                    GraphicsDevice.Viewport = new Viewport(0, 0, BLUR_WIDTH, BLUR_HEIGHT);

                    GraphicsDevice.SetRenderTarget(BlurXRT);
                    GraphicsDevice.Clear(Engine.ClearColor);
                    MDraw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Matrix.Identity
                    );

                    MDraw.SpriteBatch.Draw(FakeRT, new Vector2(0f, 0f), null, Color.White, 0f, Vector2.Zero, blurScale, SpriteEffects.None, 0);

                    for (int x = blurrad - 1; x > 0; x--) {
                        float a = (blurrad - x) * 0.5f / blurrad;
                        Color c = new Color(a, a, a, a) * 0.5f;
                        MDraw.SpriteBatch.Draw(FakeRT, new Vector2(-x * blurdist - 0.5f, 0), null, c, 0f, Vector2.Zero, blurScale, SpriteEffects.None, 0);
                        MDraw.SpriteBatch.Draw(FakeRT, new Vector2(+x * blurdist - 0.5f, 0), null, c, 0f, Vector2.Zero, blurScale, SpriteEffects.None, 0);
                    }

                    MDraw.SpriteBatch.End();

                    GraphicsDevice.SetRenderTarget(BlurYRT);
                    GraphicsDevice.Clear(Engine.ClearColor);
                    MDraw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Matrix.Identity
                    );

                    MDraw.SpriteBatch.Draw(BlurXRT, new Vector2(0f, 0f), null, Color.White, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);

                    for (int y = blurrad - 1; y > 0; y--) {
                        float a = (blurrad - y) * 0.5f / blurrad;
                        Color c = new Color(a, a, a, a) * 0.5f;
                        MDraw.SpriteBatch.Draw(BlurXRT, new Vector2(0f, -y * blurdist - 0.5f), null, c, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);
                        MDraw.SpriteBatch.Draw(BlurXRT, new Vector2(0f, +y * blurdist - 0.5f), null, c, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0);
                    }

                    MDraw.SpriteBatch.End();

                    GraphicsDevice.SetRenderTarget(BlurRT);
                    GraphicsDevice.Clear(Engine.ClearColor);
                    MDraw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Matrix.Identity
                    );

                    MDraw.SpriteBatch.Draw(BlurYRT, new Vector2(0f, 0f), null, Color.White, 0f, Vector2.Zero, blurScaleInv, SpriteEffects.None, 0);

                    MDraw.SpriteBatch.End();
                }

                if (FakeRT != null) {
                    GraphicsDevice.SetRenderTarget(tmpRealRT);
                    GraphicsDevice.Viewport = Engine.Viewport;
                    GraphicsDevice.Clear(Engine.ClearColor);

                    MDraw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Matrix.Identity
                    );

                    MDraw.SpriteBatch.Draw(FakeRT, Vector2.Zero, Color.White);

                    MDraw.SpriteBatch.End();
                }
            });
        }

        private void ILRenderLevel(ILContext il) {
            ILCursor c = new ILCursor(il);

            while (c.TryGotoNext(i => i.MatchCallOrCallvirt(typeof(GraphicsDevice), "SetRenderTarget"))) {
                c.EmitDelegate<Func<RenderTarget2D, RenderTarget2D>>(GetFakeRT);
                c.Index++;
            }
        }

    }
}