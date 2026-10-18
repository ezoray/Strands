using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>Small controls made in code, in one place so that every screen makes them the same way.</summary>
    public static class Controls
    {
        const string OnClass = "on";

        /// <summary>
        /// A row of choices of which one is on, such as a pair of outputs taken as stereo or as two
        /// mono outputs. It shows every choice, so the state and the way out of it are both in view.
        /// </summary>
        /// <param name="dots">A colour to show beside each label, or null for none.</param>
        public static VisualElement Segmented(IList<string> labels, int selected, Action<int> chosen, IList<Color?> dots = null)
        {
            var row = new VisualElement();
            row.AddToClassList("seg");
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var item = new Button(() => chosen(index));
                item.focusable = false;
                item.AddToClassList("seg__item");
                item.EnableInClassList(OnClass, i == selected);
                if (dots != null && dots[i].HasValue)
                {
                    var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                    dot.AddToClassList("dot");
                    dot.style.backgroundColor = dots[i].Value;
                    item.Add(dot);
                    item.Add(new Label(labels[i]) { pickingMode = PickingMode.Ignore });
                }
                else
                {
                    item.text = labels[i];
                }
                row.Add(item);
            }
            return row;
        }

        /// <summary>
        /// A row of things that are each on or off, such as the stems that are shown and played.
        /// It looks as <see cref="Segmented"/> does, and any number of it can be on.
        /// </summary>
        public static VisualElement Toggles(IList<string> labels, IList<bool> on, Action<int> pressed, IList<Color?> dots = null)
        {
            var row = new VisualElement();
            row.AddToClassList("seg");
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var item = new Button(() => pressed(index));
                item.focusable = false;
                item.AddToClassList("seg__item");
                item.EnableInClassList(OnClass, on[i]);
                if (dots != null && dots[i].HasValue)
                {
                    var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                    dot.AddToClassList("dot");
                    dot.style.backgroundColor = dots[i].Value;
                    item.Add(dot);
                    item.Add(new Label(labels[i]) { pickingMode = PickingMode.Ignore });
                }
                else
                {
                    item.text = labels[i];
                }
                row.Add(item);
            }
            return row;
        }

        const string TipName = "tip";

        /// <summary>
        /// Shows a few words beside the pointer while it is over an element, such as the whole of a
        /// name too long for its place. UI Toolkit's own tooltips are for the Editor's windows and
        /// never appear in the app.
        /// </summary>
        public static void Hint(VisualElement target, string text)
        {
            target.RegisterCallback<PointerEnterEvent>(evt => ShowTip(target, text, evt.position));
            target.RegisterCallback<PointerMoveEvent>(evt => ShowTip(target, text, evt.position));
            target.RegisterCallback<PointerLeaveEvent>(evt => HideTip(target));
            // What the pointer is over can be taken away under it, as a file's card is when the strips are drawn again.
            target.RegisterCallback<DetachFromPanelEvent>(evt => HideTip(evt.originPanel == null ? null : evt.originPanel.visualTree));
        }

        /// <summary>
        /// The element the app's stylesheet hangs from, which is where the tip has to be for the
        /// stylesheet to reach it: above that, the panel's own root has none of the app's styles.
        /// </summary>
        static VisualElement StyledTop(VisualElement from)
        {
            VisualElement top = from;
            while (top.parent != null && !top.ClassListContains("root"))
                top = top.parent;
            return top;
        }

        static void ShowTip(VisualElement target, string text, Vector2 pointer)
        {
            if (target.panel == null)
                return;
            // One label for the whole panel, found again each time and never kept: nothing here may outlive a run of the app.
            VisualElement top = StyledTop(target);
            Label tip = top.Q<Label>(TipName);
            if (tip == null)
            {
                tip = new Label { name = TipName, pickingMode = PickingMode.Ignore };
                tip.AddToClassList("tip");
                tip.style.position = Position.Absolute;
                top.Add(tip);
            }
            tip.text = text;
            tip.style.display = DisplayStyle.Flex;
            tip.BringToFront();
            // Below and to the right of the pointer, and brought back in when that would run off the edge.
            float width = float.IsNaN(tip.resolvedStyle.width) ? 0 : tip.resolvedStyle.width, height = float.IsNaN(tip.resolvedStyle.height) ? 0 : tip.resolvedStyle.height;
            Vector2 at = top.WorldToLocal(pointer);
            tip.style.left = Mathf.Max(4, Mathf.Min(at.x + 14, top.worldBound.width - width - 4));
            tip.style.top = at.y + 20 + height > top.worldBound.height ? at.y - height - 8 : at.y + 20;
        }

        /// <summary>Takes the words beside the pointer away, when what they were about is being moved or is gone.</summary>
        public static void HideTip(VisualElement anywhere)
        {
            if (anywhere == null || anywhere.panel == null)
                return;
            Label tip = anywhere.panel.visualTree.Q<Label>(TipName);
            if (tip != null)
                tip.style.display = DisplayStyle.None;
        }

        /// <summary>Shows a button that stays down, such as Click or Loop pattern, as on or off.</summary>
        public static void SetOn(VisualElement button, bool on)
        {
            button.EnableInClassList(OnClass, on);
        }

        /// <summary>
        /// Takes the keyboard away from every button under <paramref name="root"/>, so that Space
        /// plays and pauses whatever was clicked last instead of pressing it again.
        /// </summary>
        public static void KeepKeysOffButtons(VisualElement root)
        {
            root.Query<Button>().ForEach(button => button.focusable = false);
        }

        /// <summary>
        /// Makes the cursor of every text box under <paramref name="root"/> blink while the box has
        /// focus, and stay lit while keys are pressed, as a text cursor does (the user, 2026-10-18:
        /// with focus on the project name nothing showed it). Its colour is the stylesheet's
        /// (<c>--unity-cursor-color</c>, paper; Unity's own is black, which does not show on the
        /// app's wells), and it blinks by a class that makes it clear.
        /// </summary>
        public static void BlinkCursors(VisualElement root)
        {
            root.Query<TextField>().ForEach(BlinkCursor);
        }

        const long CursorBlinkMs = 530;
        const string CursorOffClass = "cursor--off";
        const string EmptyClass = "text-field--empty";

        static void BlinkCursor(TextField field)
        {
            IVisualElementScheduledItem blink = null;
            Action show = () =>
            {
                field.RemoveFromClassList(CursorOffClass);
                if (blink != null)
                    blink.Pause();
                blink = field.schedule.Execute(() => field.ToggleInClassList(CursorOffClass)).Every(CursorBlinkMs).StartingIn(CursorBlinkMs);
            };
            // An empty box draws its cursor too high; the stylesheet lowers it (text-field--empty).
            Action markEmpty = () => field.EnableInClassList(EmptyClass, string.IsNullOrEmpty(field.text));
            markEmpty();
            field.RegisterValueChangedCallback(_ => markEmpty());
            field.RegisterCallback<InputEvent>(_ => field.schedule.Execute(markEmpty));
            field.RegisterCallback<FocusInEvent>(_ => { markEmpty(); show(); });
            field.RegisterCallback<KeyDownEvent>(_ => show());
            field.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (blink != null)
                    blink.Pause();
                field.RemoveFromClassList(CursorOffClass);
            });
        }

        /// <summary>True while the user is typing in a text field, when a letter is a letter and not a shortcut.</summary>
        public static bool IsTyping(VisualElement root)
        {
            if (root == null || root.panel == null)
                return false;
            var focused = root.panel.focusController.focusedElement as VisualElement;
            for (VisualElement element = focused; element != null; element = element.parent)
                if (element is TextField)
                    return true;
            return false;
        }
    }
}
