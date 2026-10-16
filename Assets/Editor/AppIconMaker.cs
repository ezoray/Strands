using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Strands.Editor
{
    /// <summary>
    /// Draws the app's icon, the mark of four strands on a dark grey ground, and makes it the
    /// icon the builds carry. The mark is drawn as the app draws it (StrandsMark): an S of two
    /// bowls, each three-eighths short of a full circle, four strands side by side. It is drawn
    /// here a second time because the app's own classes cannot be seen from the Editor's.
    /// </summary>
    public static class AppIconMaker
    {
        public const string IconPath = "Assets/Runtime/Graphics/AppIcon.png";

        const int Size = 1024;
        // The shape macOS gives an app's icon: a square with round corners that leaves a margin
        // of the picture clear all round. Windows and Linux show the same picture as it is.
        const float BodySize = 824, CornerRadius = 185;
        // The mark's own square of 100 units, as in StrandsMark, and how large a unit is here.
        const float Unit = 6.2f;
        const float UpperCentre = 30, LowerCentre = 70, MiddleRadius = 20, Pitch = 4, Thickness = 3.4f;
        // Samples to a pixel each way, which is what smooths the edges.
        const int Fine = 4;

        // The four strips' colours, as in Theme: coral, saffron, mint, periwinkle.
        static readonly Color32[] Strands =
        {
            new Color32(0xff, 0x7d, 0x5c, 0xff),
            new Color32(0xff, 0xd0, 0x4a, 0xff),
            new Color32(0x3f, 0xdc, 0xb6, 0xff),
            new Color32(0x8a, 0xa9, 0xff, 0xff)
        };
        // Dark grey, a little lighter at the top than at the foot.
        static readonly Color GroundTop = new Color32(0x3b, 0x3d, 0x44, 0xff), GroundFoot = new Color32(0x1f, 0x20, 0x25, 0xff);

        [MenuItem("Strands/Make app icon")]
        static void MakeMenu()
        {
            Debug.Log(Make());
        }

        /// <summary>Draws the icon, saves it in the project and sets it as the icon of every build.</summary>
        public static string Make()
        {
            var pixels = new Color32[Size * Size];
            float origin = Size / 2f - 50 * Unit;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float red = 0, green = 0, blue = 0, inside = 0;
                    for (int fineY = 0; fineY < Fine; fineY++)
                    {
                        for (int fineX = 0; fineX < Fine; fineX++)
                        {
                            float px = x + (fineX + 0.5f) / Fine, py = y + (fineY + 0.5f) / Fine;
                            if (!InBody(px, py))
                                continue;
                            inside++;
                            int strand = StrandAt((px - origin) / Unit, (py - origin) / Unit);
                            Color colour = strand >= 0 ? (Color)Strands[strand] : Color.Lerp(GroundTop, GroundFoot, (py - (Size - BodySize) / 2) / BodySize);
                            red += colour.r;
                            green += colour.g;
                            blue += colour.b;
                        }
                    }
                    // Rows are stored from the foot of the picture up.
                    int at = (Size - 1 - y) * Size + x;
                    pixels[at] = inside == 0 ? new Color32(0, 0, 0, 0)
                        : new Color(red / inside, green / inside, blue / inside, inside / (Fine * Fine));
                }
            }

            var picture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            picture.SetPixels32(pixels);
            picture.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(IconPath));
            File.WriteAllBytes(IconPath, picture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(picture);

            AssetDatabase.ImportAsset(IconPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(IconPath);
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
            AssetDatabase.SaveAssets();
            return "Icon drawn at " + IconPath + " and set as the default icon of the builds.";
        }

        /// <summary>Whether a point of the picture is on the round-cornered square.</summary>
        static bool InBody(float x, float y)
        {
            float half = BodySize / 2, dx = Math.Abs(x - Size / 2f), dy = Math.Abs(y - Size / 2f);
            if (dx > half || dy > half)
                return false;
            float cornerX = dx - (half - CornerRadius), cornerY = dy - (half - CornerRadius);
            return cornerX <= 0 || cornerY <= 0 || cornerX * cornerX + cornerY * cornerY <= CornerRadius * CornerRadius;
        }

        /// <summary>Which strand a point of the mark's square is on, 0 to 3, or -1 for none.</summary>
        static int StrandAt(float x, float y)
        {
            // Angles run clockwise from the right, as they do on screen, where y goes down.
            float upperX = x - 50, upperY = y - UpperCentre, lowerX = x - 50, lowerY = y - LowerCentre;
            float upperRadius = Mathf.Sqrt(upperX * upperX + upperY * upperY), lowerRadius = Mathf.Sqrt(lowerX * lowerX + lowerY * lowerY);
            float upperAngle = Mathf.Repeat(Mathf.Atan2(upperY, upperX) * Mathf.Rad2Deg, 360), lowerAngle = Mathf.Repeat(Mathf.Atan2(lowerY, lowerX) * Mathf.Rad2Deg, 360);
            // The upper bowl runs from the upper right, over the top and down the left to the
            // middle; the lower one on round the right, to end at the lower left.
            bool onUpper = upperAngle >= 90 && upperAngle <= 315, onLower = lowerAngle >= 270 || lowerAngle <= 135;
            for (int strand = 0; strand < 4; strand++)
            {
                // A strand on the outside of the upper bowl is on the inside of the lower one.
                float upper = MiddleRadius + (1.5f - strand) * Pitch, lower = 2 * MiddleRadius - upper;
                if (onUpper && Math.Abs(upperRadius - upper) <= Thickness / 2)
                    return strand;
                if (onLower && Math.Abs(lowerRadius - lower) <= Thickness / 2)
                    return strand;
            }
            return -1;
        }
    }
}
