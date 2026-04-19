using System;
using UnityEngine;

namespace AdaptiveCueing
{
    public enum CuePresetId
    {
        SteppingStones2D,
        ZebraStripes2D,
        Beams3D,
        Hurdles3D,
        AlternatingPathBars
    }

    public enum CuePresetShape
    {
        SteppingStones,
        ZebraStripes,
        Beams,
        Hurdles,
        PathBars
    }

    [Serializable]
    public struct CuePresetDefinition
    {
        public CuePresetId Id;
        public string DisplayName;
        public CuePresetShape Shape;
        public Color PrimaryColor;
        public Color SecondaryColor;
        public bool UseSecondaryColor;
        public bool AlternateFeet;
        public bool CenterOnPath;
        public float Width;
        public float Thickness;
        public float LengthScale;
        public float LateralOffsetScale;
    }

    public static class CuePresetLibrary
    {
        private static readonly CuePresetDefinition[] BuiltInPresets =
        {
            new CuePresetDefinition
            {
                Id = CuePresetId.SteppingStones2D,
                DisplayName = "2D Stepping Stones",
                Shape = CuePresetShape.SteppingStones,
                PrimaryColor = new Color(60f / 255f, 67f / 255f, 247f / 255f, 1f),
                SecondaryColor = new Color(53f / 255f, 226f / 255f, 243f / 255f, 1f),
                UseSecondaryColor = true,
                AlternateFeet = true,
                CenterOnPath = false,
                Width = 0.22f,
                Thickness = 0.008f,
                LengthScale = 0.52f,
                LateralOffsetScale = 1f
            },
            new CuePresetDefinition
            {
                Id = CuePresetId.ZebraStripes2D,
                DisplayName = "2D Zebra Stripes",
                Shape = CuePresetShape.ZebraStripes,
                PrimaryColor = new Color(56f / 255f, 211f / 255f, 46f / 255f, 1f),
                SecondaryColor = new Color(56f / 255f, 211f / 255f, 46f / 255f, 1f),
                UseSecondaryColor = false,
                AlternateFeet = false,
                CenterOnPath = true,
                Width = 0.48f,
                Thickness = 0.008f,
                LengthScale = 0.74f,
                LateralOffsetScale = 0f
            },
            new CuePresetDefinition
            {
                Id = CuePresetId.Beams3D,
                DisplayName = "3D Beams",
                Shape = CuePresetShape.Beams,
                PrimaryColor = new Color(69f / 255f, 173f / 255f, 51f / 255f, 1f),
                SecondaryColor = new Color(60f / 255f, 190f / 255f, 197f / 255f, 1f),
                UseSecondaryColor = true,
                AlternateFeet = false,
                CenterOnPath = true,
                Width = 0.36f,
                Thickness = 0.045f,
                LengthScale = 0.58f,
                LateralOffsetScale = 0f
            },
            new CuePresetDefinition
            {
                Id = CuePresetId.Hurdles3D,
                DisplayName = "3D Hurdles",
                Shape = CuePresetShape.Hurdles,
                PrimaryColor = new Color(167f / 255f, 61f / 255f, 35f / 255f, 1f),
                SecondaryColor = new Color(177f / 255f, 156f / 255f, 45f / 255f, 1f),
                UseSecondaryColor = true,
                AlternateFeet = false,
                CenterOnPath = true,
                Width = 0.44f,
                Thickness = 0.085f,
                LengthScale = 0.62f,
                LateralOffsetScale = 0f
            },
            new CuePresetDefinition
            {
                Id = CuePresetId.AlternatingPathBars,
                DisplayName = "Alternating Path Bars",
                Shape = CuePresetShape.PathBars,
                PrimaryColor = new Color(167f / 255f, 61f / 255f, 35f / 255f, 1f),
                SecondaryColor = new Color(177f / 255f, 156f / 255f, 45f / 255f, 1f),
                UseSecondaryColor = true,
                AlternateFeet = false,
                CenterOnPath = true,
                Width = 0.42f,
                Thickness = 0.012f,
                LengthScale = 0.78f,
                LateralOffsetScale = 0f
            }
        };

        private static readonly Color PreviewBackground = new Color(0.10f, 0.10f, 0.12f, 1f);
        private static readonly Color PreviewFloor = new Color(0.21f, 0.19f, 0.16f, 1f);

        public static CuePresetDefinition[] GetBuiltInPresets()
        {
            CuePresetDefinition[] copy = new CuePresetDefinition[BuiltInPresets.Length];
            Array.Copy(BuiltInPresets, copy, BuiltInPresets.Length);
            return copy;
        }

        public static CuePresetDefinition Get(CuePresetId presetId)
        {
            for (int index = 0; index < BuiltInPresets.Length; index++)
            {
                if (BuiltInPresets[index].Id == presetId)
                {
                    return BuiltInPresets[index];
                }
            }

            return BuiltInPresets[0];
        }

        public static Color GetCueColor(CuePresetDefinition preset, int cueIndex)
        {
            return preset.UseSecondaryColor && cueIndex % 2 != 0
                ? preset.SecondaryColor
                : preset.PrimaryColor;
        }

        public static Vector3 GetCueScale(CuePresetDefinition preset, float spacing)
        {
            float depth = Mathf.Max(0.12f, spacing * preset.LengthScale);
            return new Vector3(preset.Width, preset.Thickness, depth);
        }

        public static float GetLateralOffset(CuePresetDefinition preset, float defaultOffset)
        {
            return defaultOffset * preset.LateralOffsetScale;
        }

        public static float GetVerticalOffset(CuePresetDefinition preset, float hoverHeight)
        {
            return hoverHeight + (preset.Thickness * 0.5f);
        }

        public static Texture2D CreatePreviewTexture(CuePresetDefinition preset, int width = 192, int height = 112)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = $"{preset.Id}_Preview",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[width * height];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = PreviewBackground;
            }

            texture.SetPixels(pixels);

            int floorHeight = Mathf.RoundToInt(height * 0.42f);
            DrawRect(texture, 0, height - floorHeight, width, floorHeight, PreviewFloor);

            switch (preset.Shape)
            {
                case CuePresetShape.SteppingStones:
                    DrawPreviewSteppingStones(texture, preset);
                    break;

                case CuePresetShape.ZebraStripes:
                    DrawPreviewCenteredBars(texture, preset, 5, Mathf.RoundToInt(height * 0.10f), 0);
                    break;

                case CuePresetShape.Beams:
                    DrawPreviewCenteredBars(texture, preset, 5, Mathf.RoundToInt(height * 0.08f), Mathf.RoundToInt(height * 0.03f));
                    break;

                case CuePresetShape.Hurdles:
                    DrawPreviewCenteredBars(texture, preset, 5, Mathf.RoundToInt(height * 0.12f), Mathf.RoundToInt(height * 0.05f));
                    break;

                case CuePresetShape.PathBars:
                    DrawPreviewAngledBars(texture, preset);
                    break;
            }

            texture.Apply(false, false);
            return texture;
        }

        private static void DrawPreviewSteppingStones(Texture2D texture, CuePresetDefinition preset)
        {
            int width = texture.width;
            int height = texture.height;
            int stepWidth = Mathf.RoundToInt(width * 0.22f);
            int stepHeight = Mathf.RoundToInt(height * 0.18f);
            int top = Mathf.RoundToInt(height * 0.18f);
            int spacing = Mathf.RoundToInt(height * 0.17f);
            int leftX = Mathf.RoundToInt(width * 0.18f);
            int rightX = Mathf.RoundToInt(width * 0.58f);

            for (int index = 0; index < 4; index++)
            {
                bool isLeft = index % 2 == 0;
                int x = isLeft ? leftX : rightX;
                int y = top + (index * spacing);
                int scaleOffset = Mathf.RoundToInt(index * 3f);
                Color color = GetCueColor(preset, index);
                DrawRect(texture, x, y, stepWidth + scaleOffset, stepHeight + scaleOffset, color);
            }
        }

        private static void DrawPreviewCenteredBars(Texture2D texture, CuePresetDefinition preset, int count, int barHeight, int inset)
        {
            int width = texture.width;
            int height = texture.height;
            int top = Mathf.RoundToInt(height * 0.16f);
            int spacing = Mathf.RoundToInt(height * 0.15f);

            for (int index = 0; index < count; index++)
            {
                int localInset = inset + Mathf.RoundToInt(index * 6f);
                int barWidth = width - (localInset * 2);
                int x = localInset;
                int y = top + (index * spacing);
                DrawRect(texture, x, y, barWidth, barHeight, GetCueColor(preset, index));
            }
        }

        private static void DrawPreviewAngledBars(Texture2D texture, CuePresetDefinition preset)
        {
            int width = texture.width;
            int height = texture.height;
            int barWidth = Mathf.RoundToInt(width * 0.32f);
            int barHeight = Mathf.RoundToInt(height * 0.12f);
            int top = Mathf.RoundToInt(height * 0.18f);
            int spacing = Mathf.RoundToInt(height * 0.18f);
            int[] xOffsets = { 18, 58, 36, 70 };

            for (int index = 0; index < 4; index++)
            {
                int x = Mathf.RoundToInt(width * (xOffsets[index] / 100f));
                int y = top + (index * spacing);
                DrawRect(texture, x, y, barWidth, barHeight, GetCueColor(preset, index));
            }
        }

        private static void DrawRect(Texture2D texture, int x, int y, int width, int height, Color color)
        {
            int maxX = Mathf.Min(texture.width, x + width);
            int maxY = Mathf.Min(texture.height, y + height);
            int minX = Mathf.Max(0, x);
            int minY = Mathf.Max(0, y);

            for (int row = minY; row < maxY; row++)
            {
                for (int column = minX; column < maxX; column++)
                {
                    texture.SetPixel(column, row, color);
                }
            }
        }
    }
}
