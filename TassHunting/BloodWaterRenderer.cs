using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TassHunting
{
    /// <summary>
    /// Cloudy blood-in-water tint (field 2026-07-21: the cube-particle stand-in
    /// read as floating voxels; the goal is The Hunter's diffuse stain look).
    /// One soft radial-gradient quad per water tile (1.6 blocks wide so
    /// neighbors overlap into a continuous cloud), vertex-tinted the config
    /// blood color, alpha bucketed by blood amount and display-LERPED so stains
    /// bloom in and fade out smoothly instead of popping at the 1 Hz sync rate.
    ///
    /// Recipe is THW's proven translucent-geometry stack (visibility dome /
    /// snowfall renderer): MeshData with baked vertex color+alpha, white-to-
    /// transparent gradient texture, PreparedStandardShader (scene-lit, engine-
    /// fogged), blend on, depth-mask off, AfterOIT stage (THW field law:
    /// blended geometry in the opaque pass makes water behind it vanish).
    /// </summary>
    public class BloodWaterRenderer : IRenderer, IDisposable
    {
        private const int Buckets = 4;
        private const float QuadHalf = 0.8f;   // 1.6-block quads overlap the tile grid into a cloud
        private const float SurfaceY = 0.9f;   // a hair above the 0.875 waterline (waves-safe)
        private static readonly float[] BucketAlphaFrac = { 0.3f, 0.5f, 0.75f, 1f };

        private readonly ICoreClientAPI capi;
        private readonly BloodVisuals owner;
        private readonly float[] modelMat = Mat4f.Create();
        private readonly MeshRef[] meshes = new MeshRef[Buckets];
        private string appliedHex;
        private float appliedOpacity = -1f;
        private int texId = -1;
        private bool texFailed;

        public double RenderOrder => 0.85;
        public int RenderRange => 999;

        public BloodWaterRenderer(ICoreClientAPI api, BloodVisuals owner)
        {
            capi = api;
            this.owner = owner;
        }

        private void EnsureMeshes(HuntingConfig cfg)
        {
            float opacity = GameMath.Clamp(cfg.WaterBloodMaxOpacity, 0.05f, 1f);
            if (meshes[0] != null && appliedHex == cfg.BloodColorHex && appliedOpacity == opacity) return;
            appliedHex = cfg.BloodColorHex;
            appliedOpacity = opacity;

            byte r = 116, g = 8, b = 12;
            try
            {
                string h = (cfg.BloodColorHex ?? "").TrimStart('#');
                if (h.Length == 6)
                {
                    r = Convert.ToByte(h.Substring(0, 2), 16);
                    g = Convert.ToByte(h.Substring(2, 2), 16);
                    b = Convert.ToByte(h.Substring(4, 2), 16);
                }
            }
            catch { }

            for (int i = 0; i < Buckets; i++)
            {
                try { meshes[i]?.Dispose(); } catch { }
                meshes[i] = capi.Render.UploadMesh(BuildQuad(r, g, b, (byte)(255f * opacity * BucketAlphaFrac[i])));
            }
        }

        private static MeshData BuildQuad(byte r, byte g, byte b, byte alpha)
        {
            var mesh = new MeshData(4, 6, false, true, true, false);
            float h = QuadHalf;
            float[] xs = { -h, h, h, -h };
            float[] zs = { -h, -h, h, h };
            float[] us = { 0f, 1f, 1f, 0f };
            float[] vs = { 0f, 0f, 1f, 1f };
            for (int i = 0; i < 4; i++)
            {
                mesh.xyz[i * 3 + 0] = xs[i];
                mesh.xyz[i * 3 + 1] = 0f;
                mesh.xyz[i * 3 + 2] = zs[i];
                mesh.Uv[i * 2 + 0] = us[i];
                mesh.Uv[i * 2 + 1] = vs[i];
                mesh.Rgba[i * 4 + 0] = r;
                mesh.Rgba[i * 4 + 1] = g;
                mesh.Rgba[i * 4 + 2] = b;
                mesh.Rgba[i * 4 + 3] = alpha;
            }
            mesh.Indices[0] = 0; mesh.Indices[1] = 1; mesh.Indices[2] = 2;
            mesh.Indices[3] = 0; mesh.Indices[4] = 2; mesh.Indices[5] = 3;
            mesh.VerticesCount = 4;
            mesh.IndicesCount = 6;
            return mesh;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled || !cfg.WaterBloodEnabled || texFailed) return;
                var tiles = owner.ClientWaterTiles;
                if (tiles.Count == 0) return;
                var plr = capi.World.Player?.Entity;
                if (plr == null) return;

                if (texId < 0)
                {
                    try
                    {
                        texId = capi.Render.GetOrLoadTexture(
                            new AssetLocation("tasshunting", "textures/particle/bloodblob.png"));
                    }
                    catch (Exception ex)
                    {
                        texFailed = true;
                        capi.Logger.Warning("[TassHunting] water blood texture load failed: {0}", ex.Message);
                        return;
                    }
                }
                EnsureMeshes(cfg);

                long now = capi.World.ElapsedMilliseconds;
                Vec3d cam = plr.CameraPos;
                double px = plr.Pos.X, pz = plr.Pos.Z;
                float maxDist2 = cfg.BloodRenderDistanceBlocks * cfg.BloodRenderDistanceBlocks;
                float lerp = Math.Min(1f, dt * 2.5f);

                IStandardShaderProgram prog = null;
                foreach (var kv in tiles)
                {
                    var t = kv.Value;
                    // bloom toward the synced amount; fade toward 0 once the
                    // server stops refreshing (2 missed 1 Hz broadcasts)
                    float target = (now - t.LastSeenMs > 2500) ? 0f : Math.Min(6f, t.Amount);
                    t.Display += (target - t.Display) * lerp;
                    if (t.Display < 0.04f) continue;
                    double dx = kv.Key.x + 0.5 - px, dz = kv.Key.z + 0.5 - pz;
                    if (dx * dx + dz * dz > maxDist2) continue;

                    if (prog == null)
                    {
                        prog = capi.Render.PreparedStandardShader(
                            (int)plr.Pos.X, (int)plr.Pos.Y, (int)plr.Pos.Z, null);
                        prog.Tex2D = texId;
                        prog.ViewMatrix = capi.Render.CameraMatrixOriginf;
                        prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
                        prog.NormalShaded = 0;
                        prog.ExtraGlow = 0;
                        capi.Render.GlToggleBlend(true);
                        capi.Render.GlDisableCullFace();
                        capi.Render.GLDepthMask(false);
                    }

                    int bucket = GameMath.Clamp((int)(t.Display / 6f * Buckets), 0, Buckets - 1);
                    Mat4f.Identity(modelMat);
                    Mat4f.Translate(modelMat, modelMat, new float[] {
                        (float)(kv.Key.x + 0.5 - cam.X),
                        (float)(kv.Key.y + SurfaceY - cam.Y),
                        (float)(kv.Key.z + 0.5 - cam.Z) });
                    prog.ModelMatrix = modelMat;
                    capi.Render.RenderMesh(meshes[bucket]);
                }

                if (prog != null)
                {
                    capi.Render.GLDepthMask(true);
                    capi.Render.GlEnableCullFace();
                    capi.Render.GlToggleBlend(false);
                    prog.Stop();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                try { meshes[i]?.Dispose(); } catch { }
                meshes[i] = null;
            }
        }
    }
}
