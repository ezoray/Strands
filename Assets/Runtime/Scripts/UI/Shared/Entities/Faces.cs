using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The typefaces for text that is drawn in code, which takes a font of its own and not the one
    /// its element is styled with. The stylesheet names the font files once and hands them to the
    /// views that draw (a custom property is only seen by an element its rule selects), so the
    /// files are named nowhere else.
    /// </summary>
    public class Faces
    {
        /// <summary>How wide every character of the mono face is, as a part of the text's size. With it a figure can be placed from its right-hand end or centred.</summary>
        public const float MonoAdvance = 0.6f;

        static readonly CustomStyleProperty<Font> TextProperty = new CustomStyleProperty<Font>("--drawn-text");
        static readonly CustomStyleProperty<Font> StrongProperty = new CustomStyleProperty<Font>("--drawn-strong");
        static readonly CustomStyleProperty<Font> MonoProperty = new CustomStyleProperty<Font>("--drawn-figures");
        static readonly Dictionary<Font, FontAsset> Made = new Dictionary<Font, FontAsset>();

        /// <summary>
        /// The size to hand to DrawText for text that is to be `size` tall in the panel's own units.
        /// DrawText takes its size in pixels of the screen, not in those units, so on a panel that
        /// is scaled up the text came out small: at twice the size, half as tall as everything
        /// round it. Its position is in the panel's units, as it should be. Found in the first
        /// build to open at twice the size the screens were drawn at, 2026-10-11; in the Editor the
        /// panel is not scaled, so it never showed there.
        /// </summary>
        public static float Size(VisualElement element, float size)
        {
            return size * element.scaledPixelsPerPoint;
        }

        /// <summary>Names and labels. Null until the style is known, which draws in the panel's own font.</summary>
        public FontAsset Text { get; private set; }
        /// <summary>A name that stands out, such as the scene in view.</summary>
        public FontAsset Strong { get; private set; }
        /// <summary>Figures.</summary>
        public FontAsset Mono { get; private set; }

        /// <summary>Keeps the faces in step with the element's style, and says when they have changed so that it can be drawn again.</summary>
        public Faces(VisualElement element, Action changed)
        {
            element.RegisterCallback<CustomStyleResolvedEvent>(evt =>
            {
                FontAsset text = Read(evt.customStyle, TextProperty), strong = Read(evt.customStyle, StrongProperty), mono = Read(evt.customStyle, MonoProperty);
                if (text == Text && strong == Strong && mono == Mono)
                    return;
                Text = text;
                Strong = strong;
                Mono = mono;
                changed();
            });
        }

        static FontAsset Read(ICustomStyle style, CustomStyleProperty<Font> property)
        {
            Font font;
            if (!style.TryGetValue(property, out font) || font == null)
                return null;
            FontAsset asset;
            if (!Made.TryGetValue(font, out asset) || asset == null)
            {
                asset = FontAsset.CreateFontAsset(font);
                Made[font] = asset;
            }
            return asset;
        }
    }
}
