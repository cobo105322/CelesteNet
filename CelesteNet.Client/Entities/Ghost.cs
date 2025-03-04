﻿using Celeste.Mod.CelesteNet.Client.Components;
using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Monocle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Client.Entities {
    public class Ghost : Actor {

        public CelesteNetClientContext Context;

        public DataPlayerInfo PlayerInfo;

        public float Alpha = 0.875f;

        public Vector2 Speed;

        public PlayerSprite Sprite;
        public PlayerHair Hair;
        public Leader Leader;
        public Holdable Holdable;
        private bool HoldableAdded;

        public GhostNameTag NameTag;
        public GhostEmote IdleTag;

        public static readonly Color[] FallbackHairColors = new Color[] { Color.Transparent };
        public Color[] HairColors;
        public static readonly string[] FallbackHairTextures = new string[] { "characters/player/hair00" };
        public string[] HairTextures;

        public bool? DashWasB;
        public Vector2 DashDir;

        public bool Dead;

        public List<GhostFollower> Followers = new();
        public GhostEntity Holding;

        public bool Interactive;

        public float GrabCooldown = 0f;
        public const float GrabCooldownMax = CelesteNetMainComponent.GrabCooldownMax;

        protected Color LastSpriteColor;
        protected Color LastHairColor;
        protected int LastDepth;
        protected int DepthOffset;

        protected Queue<Action<Ghost>> UpdateQueue = new();
        protected bool IsUpdating;

        public Ghost(CelesteNetClientContext context, DataPlayerInfo playerInfo, PlayerSpriteMode spriteMode)
            : base(Vector2.Zero) {
            Context = context;
            PlayerInfo = playerInfo;

            Depth = 0;

            RetryPlayerSprite:
            try {
                Sprite = new(spriteMode | (PlayerSpriteMode) (1 << 31));
            } catch (Exception) {
                if (spriteMode != PlayerSpriteMode.Madeline) {
                    spriteMode = PlayerSpriteMode.Madeline;
                    goto RetryPlayerSprite;
                }
                throw;
            }

            Add(Hair = new(Sprite));
            Add(Sprite);
            Hair.Color = Player.NormalHairColor;
            Add(Leader = new(new(0f, -8f)));
            Holdable = new() {
                OnCarry = OnCarry,
                OnRelease = OnRelease
            };

            Collidable = true;
            Collider = new Hitbox(8f, 11f, -4f, -11f);
            Add(new PlayerCollider(OnPlayer));

            NameTag = new(this, "");
            NameTag.Alpha = 0.85f;

            Dead = false;
            AllowPushing = false;
            SquishCallback = null;

            Tag = Tags.Persistent | Tags.PauseUpdate | Tags.TransitionUpdate;
        }

        public override void Added(Scene scene) {
            base.Added(scene);

            Hair.Start();
            Scene.Add(NameTag);
        }

        public override void Removed(Scene scene) {
            base.Removed(scene);

            NameTag.RemoveSelf();
        }

        public void OnPlayer(Player player) {
            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.Interactions || Context?.Main.GrabbedBy == this)
                return;

            if (player.StateMachine.State == Player.StNormal &&
                player.Speed.Y > 0f && player.Bottom <= Top + 3f) {

                Dust.Burst(player.BottomCenter, -1.57079637f, 8);
                (Scene as Level)?.DirectionalShake(Vector2.UnitY, 0.05f);
                Input.Rumble(RumbleStrength.Light, RumbleLength.Medium);
                player.Bounce(Top + 2f);
                player.Play("event:/game/general/thing_booped");

            } else if (player.StateMachine.State != Player.StDash &&
                player.StateMachine.State != Player.StRedDash &&
                player.StateMachine.State != Player.StDreamDash &&
                player.StateMachine.State != Player.StBirdDashTutorial &&
                player.Speed.Y <= 0f && Bottom <= player.Top + 5f) {
                player.Speed.Y = Math.Max(player.Speed.Y, 16f);
            }
        }

        public void OnCarry(Vector2 position) {
            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.Interactions || IdleTag != null)
                return;

            Position = position;
            Collidable = false;

            CelesteNetClient client = Context?.Client;
            if (PlayerInfo == null || client == null)
                return;

            try {
                client.Send(new DataPlayerGrabPlayer {
                    UpdateID = Context.Main.FrameNextID++,

                    Player = client.PlayerInfo,

                    Grabbing = PlayerInfo,
                    Position = position,
                    Force = null
                });
            } catch (Exception e) {
                Logger.Log(LogLevel.INF, "client-ghost", $"Error in OnCarry:\n{e}");
                Context.Dispose();
            }
        }

        public void OnRelease(Vector2 force) {
            Collidable = true;

            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.Interactions || IdleTag != null)
                return;

            CelesteNetClient client = Context?.Client;
            if (PlayerInfo == null || client == null)
                return;

            try {
                client.Send(new DataPlayerGrabPlayer {
                    UpdateID = Context.Main.FrameNextID++,

                    Player = client.PlayerInfo,

                    Grabbing = PlayerInfo,
                    Position = Position,
                    Force = force
                });
            } catch (Exception e) {
                Logger.Log(LogLevel.INF, "client-ghost", $"Error in OnRelease:\n{e}");
                Context.Dispose();
            }
        }

        public override void Update() {
            lock (UpdateQueue) {
                IsUpdating = true;
                while (UpdateQueue.Count > 0)
                    UpdateQueue.Dequeue()(this);
                IsUpdating = false;
            }

            if (string.IsNullOrEmpty(NameTag.Name) && Active) {
                RemoveSelf();
                return;
            }

            bool holdable = Interactive && GrabCooldown <= 0f && CelesteNetClientModule.Settings.Interactions && IdleTag == null;

            GrabCooldown -= Engine.RawDeltaTime;
            if (GrabCooldown < 0f)
                GrabCooldown = 0f;

            if (!holdable && Holdable.Holder != null) {
                Collidable = false;
                Holdable.Release(Vector2.Zero);
                Collidable = true;
            }

            if (!HoldableAdded && holdable) {
                HoldableAdded = true;
                Add(Holdable);
            } else if (HoldableAdded && !holdable && !Holdable.IsHeld) {
                HoldableAdded = false;
                Remove(Holdable);
            }

            Alpha = 0.875f * ((CelesteNetClientModule.Settings.PlayerOpacity + 2) / 6f);
            DepthOffset = 0;
            Player p = Scene.Tracker.GetEntity<Player>();
            if (p != null) {
                float dist = (p.Position - Position).LengthSquared();
                if (CelesteNetClientModule.Settings.PlayerOpacity > 2)
                    Alpha = Calc.LerpClamp(Alpha * 0.5f, Alpha, dist / 256f);
                if (dist <= 256f) {
                    Depth = LastDepth + 1;
                    DepthOffset = 1;
                }
            }

            Hair.Color = LastHairColor * Alpha;
            Hair.Alpha = Alpha;
            Sprite.Color = LastSpriteColor * Alpha;

            if (NameTag.Scene != Scene)
                Scene.Add(NameTag);

            Visible = !Dead;

            base.Update();

            if (Scene is not Level level)
                return;

            if (!level.GetUpdateHair() || level.Overlay is PauseUpdateOverlay)
                Hair.AfterUpdate();

            foreach (GhostFollower gf in Followers)
                if (gf.Scene != level)
                    level.Add(gf);

            if (Holding != null && Holding.Scene != level)
                level.Add(Holding);

            // TODO: Get rid of this, sync particles separately!
            if (DashWasB != null && level != null && Speed != Vector2.Zero && level.OnRawInterval(0.02f))
                level.ParticlesFG.Emit(DashWasB.Value ? Player.P_DashB : Player.P_DashA, Center + Calc.Random.Range(Vector2.One * -2f, Vector2.One * 2f), DashDir.Angle());
        }

        public void RunOnUpdate(Action<Ghost> action, bool wait = false) {
            bool updating;
            lock (UpdateQueue)
                updating = IsUpdating;
            if (updating) {
                action(this);
                return;
            }

            using ManualResetEvent waiter = wait ? new ManualResetEvent(false) : null;
            if (wait) {
                Action<Ghost> real = action;
                action = g => {
                    try {
                        real(g);
                    } finally {
                        waiter.Set();
                    }
                };
            }

            lock (UpdateQueue)
                UpdateQueue.Enqueue(action);

            if (wait)
                WaitHandle.WaitAny(new WaitHandle[] { waiter });
        }

        public void UpdateHair(Facings facing, Color color, bool simulateMotion, int count, Color[] colors, string[] textures) {
            Hair.Facing = facing;
            LastHairColor = color;
            Hair.Color = color * Alpha;
            Hair.SimulateMotion = simulateMotion;

            if (count == 0) {
                count = 1;
                colors = FallbackHairColors;
                textures = FallbackHairTextures;
                Hair.Alpha = 0;
            }

            while (Hair.Nodes.Count < count)
                Hair.Nodes.Add(Hair.Nodes.LastOrDefault());
            while (Hair.Nodes.Count > count)
                Hair.Nodes.RemoveAt(Hair.Nodes.Count - 1);
            HairColors = colors;
            HairTextures = textures;
            Sprite.HairCount = count;
        }

        public void UpdateSprite(Vector2 position, Vector2 speed, Vector2 scale, Facings facing, int depth, Color color, float rate, Vector2? justify, string animationID, int animationFrame) {
            if (Holdable.Holder == null) {
                Position = position;
            }
            Speed = speed;

            Sprite.Scale = scale;
            Sprite.Scale.X *= (float) facing;

            LastDepth = depth;
            Depth = depth + DepthOffset;

            LastSpriteColor = color;
            Sprite.Color = color * Alpha;

            Sprite.Rate = rate;
            Sprite.Justify = justify;

            if (!string.IsNullOrEmpty(animationID)) {
                try {
                    if (Sprite.CurrentAnimationID != animationID)
                        Sprite.Play(animationID);
                    Sprite.SetAnimationFrame(animationFrame);
                } catch {
                    // Play likes to fail randomly as the ID doesn't exist in an underlying dict.
                    // Let's ignore this for now.
                }
            }
        }

        public void UpdateDash(bool? wasB, Vector2 dir) {
            DashWasB = wasB;
            DashDir = dir;
        }

        public void UpdateDead(bool dead) {
            if (!Dead && dead)
                HandleDeath();
            Dead = dead;
        }

        public void UpdateFollowers(DataPlayerFrame.Entity[] followers) {
            for (int i = 0; i < followers.Length; i++) {
                DataPlayerFrame.Entity f = followers[i];
                GhostFollower gf;
                if (i >= Followers.Count) {
                    gf = new(this);
                    gf.Position = Position + Leader.Position;
                    Followers.Add(gf);
                } else {
                    gf = Followers[i];
                }
                gf.UpdateSprite(f.Scale, f.Depth, f.Color, f.SpriteRate, f.SpriteJustify, f.SpriteID, f.CurrentAnimationID, f.CurrentAnimationFrame);
            }

            while (Followers.Count > followers.Length) {
                GhostFollower gf = Followers[Followers.Count - 1];
                gf.Ghost = null;
                Followers.RemoveAt(Followers.Count - 1);
            }
        }

        public void UpdateHolding(DataPlayerFrame.Entity h) {
            if (h == null) {
                if (Holding != null)
                    Holding.Ghost = null;
                Holding = null;
                return;
            }

            if (Holding == null)
                Holding = new(this);
            Holding.Position = h.Position;
            Holding.UpdateSprite(h.Scale, h.Depth, h.Color, h.SpriteRate, h.SpriteJustify, h.SpriteID, h.CurrentAnimationID, h.CurrentAnimationFrame);
        }


        public void HandleDeath() {
            if (Scene is not Level level ||
                level.Paused || level.Overlay != null)
                return;
            level.Add(new GhostDeadBody(this, Vector2.Zero));
        }

    }
}
