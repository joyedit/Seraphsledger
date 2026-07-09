using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SeraphsLedger
{
    // One floating label above a hidden lockbox.
    public class LockboxLabel
    {
        public double X;
        public double Y;
        public double Z;
        public string Text;
        public LoadedTexture Texture;
    }

    // Renders floating text labels at lockbox positions while the player holds a
    // Page of Secrets (the server only sends positions while one is held, and
    // only those within range). Ported from Quartermaster's ContainerLabelRenderer:
    // drawing happens in 2D screen space during the Ortho stage, so the labels are
    // visible through walls, with a distance fade toward the sync range.
    public class LockboxLabelRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly List<LockboxLabel> labels = new List<LockboxLabel>();

        // The server stops sending boxes beyond 30 blocks, so fade out just below
        // that to avoid labels popping in/out at the boundary.
        private const double FadeStartDist = 20.0;
        private const double FadeEndDist = 30.0;
        private const double MaxRenderDist = 30.0;

        public double RenderOrder => 0.95; // after most Ortho elements, before GUI
        public int RenderRange => 999;

        public LockboxLabelRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void SetLabels(List<LockboxLabel> newLabels)
        {
            ClearLabels();
            foreach (var label in newLabels)
            {
                label.Texture = GenTextTexture(label.Text);
                labels.Add(label);
            }
        }

        public void ClearLabels()
        {
            foreach (var label in labels)
            {
                label.Texture?.Dispose();
            }
            labels.Clear();
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (labels.Count == 0) return;

            IRenderAPI rapi = capi.Render;
            double[] projMat = rapi.PerspectiveProjectionMat;
            // CameraMatrixOrigin is the view matrix with the player at the origin,
            // matching our player-relative dx/dy/dz input.
            double[] viewMat = rapi.CameraMatrixOrigin;

            var camPos = capi.World.Player.Entity.CameraPos;
            int fbWidth = rapi.FrameWidth;
            int fbHeight = rapi.FrameHeight;

            foreach (var label in labels)
            {
                if (label.Texture == null) continue;

                // Label floats just above the lockbox block.
                double dx = label.X + 0.5 - camPos.X;
                double dy = label.Y + 1.2 - camPos.Y;
                double dz = label.Z + 0.5 - camPos.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (dist > MaxRenderDist) continue;

                // View matrix (column-major), then projection.
                double vx = viewMat[0] * dx + viewMat[4] * dy + viewMat[8] * dz + viewMat[12];
                double vy = viewMat[1] * dx + viewMat[5] * dy + viewMat[9] * dz + viewMat[13];
                double vz = viewMat[2] * dx + viewMat[6] * dy + viewMat[10] * dz + viewMat[14];
                double vw = viewMat[3] * dx + viewMat[7] * dy + viewMat[11] * dz + viewMat[15];

                double px = projMat[0] * vx + projMat[4] * vy + projMat[8] * vz + projMat[12] * vw;
                double py = projMat[1] * vx + projMat[5] * vy + projMat[9] * vz + projMat[13] * vw;
                double pw = projMat[3] * vx + projMat[7] * vy + projMat[11] * vz + projMat[15] * vw;

                if (pw <= 0) continue; // behind the camera

                double ndcX = px / pw;
                double ndcY = py / pw;
                if (ndcX < -1.5 || ndcX > 1.5 || ndcY < -1.5 || ndcY > 1.5) continue;

                double screenX = (ndcX + 1.0) * 0.5 * fbWidth;
                double screenY = (1.0 - ndcY) * 0.5 * fbHeight;

                float alpha = 1.0f;
                if (dist > FadeStartDist)
                {
                    alpha = (float)(1.0 - (dist - FadeStartDist) / (FadeEndDist - FadeStartDist));
                    alpha = GameMath.Clamp(alpha, 0f, 1f);
                }
                if (alpha <= 0f) continue;

                float texW = label.Texture.Width;
                float texH = label.Texture.Height;

                rapi.Render2DTexturePremultipliedAlpha(
                    label.Texture.TextureId,
                    (float)screenX - texW / 2f,
                    (float)screenY - texH / 2f,
                    texW, texH,
                    50f,
                    new Vec4f(1f, 1f, 1f, alpha)
                );
            }
        }

        private LoadedTexture GenTextTexture(string text)
        {
            CairoFont font = CairoFont.WhiteSmallishText();
            font.WithColor(new double[] { 1, 1, 1, 1 });

            return capi.Gui.TextTexture.GenTextTexture(
                text,
                font,
                new TextBackground()
                {
                    FillColor = GuiStyle.DialogStrongBgColor,
                    Padding = 5,
                    Radius = 3,
                    BorderWidth = 1,
                    BorderColor = GuiStyle.DialogBorderColor
                }
            );
        }

        public void Dispose()
        {
            ClearLabels();
        }
    }
}
