using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// A small picture of the whole of one file. It is drawn to the file's own loudest point, so
    /// that a quiet stem can still be told from its shape.
    /// </summary>
    public class MiniWave : VisualElement
    {
        readonly WavePicture picture;

        public MiniWave(WavePicture picture)
        {
            this.picture = picture;
            AddToClassList("mini");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width, height = contentRect.height;
            if (picture == null || float.IsNaN(width) || width < 2 || height < 4)
                return;
            picture.Draw(context, 0, (int)width, 0, picture.Frames / (double)width, height / 2, height / 2 - 2);
        }
    }
}
