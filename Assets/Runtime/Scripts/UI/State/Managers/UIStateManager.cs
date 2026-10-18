using System;
using System.Collections.Generic;
using System.Globalization;
using Strands.Applications.Core;
using Strands.Sp16;
using Strands.UI.Shared;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.State
{
    /// <summary>
    /// Owns the UI document, decides which screen is showing, and looks after the bar along the top,
    /// which is the same on all of them: the mark, the three steps, the project's name and the sample memory.
    /// </summary>
    public class UIStateManager : MonoBehaviour
    {
        const string HiddenClass = "hidden";
        const string OnClass = "on";
        const string HighClass = "memory--high";
        const string OverClass = "memory--over";
        const float MemoryBarWidth = 140;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [SerializeField] UIDocument document;

        ApplicationManager app;
        Button stepSource, stepAlign, stepExport, exportButton;
        TextField nameField;
        Label memoryText, shortcuts;
        VisualElement memoryBar;
        bool alignAvailable, exportAvailable;

        public UIStates State { get; private set; }
        public VisualElement Root { get { return document.rootVisualElement; } }

        public event Action<UIStates> StateChanged;
        /// <summary>The user pressed one of the two steps at the top. The screens decide what it takes to go there.</summary>
        public event Action<UIStates> StepRequested;

        void Start()
        {
            Ready();
            Apply();
        }

        // The screens' managers may start before this one does and ask for the memory or the keys
        // to be shown, so whatever is asked for first finds the controls.
        void Ready()
        {
            if (stepSource != null)
                return;
            app = FindAnyObjectByType<ApplicationManager>();
            VisualElement root = Root;
            // Behind everything else in the window: the first thing in the element that has the dark ground.
            root.Q(className: "root").Insert(0, new Backdrop());
            root.Q("markHost").Add(new StrandsMark());
            // The version is the one in the Project Settings (Player, Version), which a build carries with it.
            root.Q<Label>("version").text = "v" + Application.version;
            stepSource = root.Q<Button>("stepSource");
            stepAlign = root.Q<Button>("stepAlign");
            stepExport = root.Q<Button>("stepExport");
            exportButton = root.Q<Button>("export");
            nameField = root.Q<TextField>("projectName");
            memoryText = root.Q<Label>("memoryText");
            memoryBar = root.Q("memoryBar");
            shortcuts = root.Q<Label>("shortcuts");

            stepSource.clicked += () => Request(UIStates.Source);
            stepAlign.clicked += () => Request(UIStates.Align);
            stepExport.clicked += () => Request(UIStates.Export);
            nameField.isDelayed = true;
            nameField.RegisterValueChangedCallback(evt =>
            {
                app.SetProjectName(evt.newValue);
                // Whatever was typed is shown as it will be written, and the keyboard goes back to the shortcuts.
                nameField.SetValueWithoutNotify(app.ProjectName);
                nameField.Blur();
            });
            app.ProjectNameChanged += ShowName;
            ShowName();
            Controls.KeepKeysOffButtons(root);
            Controls.BlinkCursors(root);
            // The arrow keys step through patterns and scenes. Left to itself the panel would also
            // take each one as a move to the next control, which puts the keyboard in the tempo box
            // and every shortcut after it into the tempo. With nothing in focus the move is sent to
            // the top of the panel, above this document, so that is where it is stopped.
            VisualElement top = root.panel == null ? root : root.panel.visualTree;
            top.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                if (evt.direction == NavigationMoveEvent.Direction.Next || evt.direction == NavigationMoveEvent.Direction.Previous)
                    return;
                evt.StopPropagation();
                top.focusController.IgnoreEvent(evt);
            }, TrickleDown.TrickleDown);
            // A click anywhere outside a text box ends the typing in it, so that the keys are
            // shortcuts again without the user having to press Return first.
            top.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (InTextField(evt.target as VisualElement))
                    return;
                var typingIn = top.focusController.focusedElement as VisualElement;
                if (InTextField(typingIn))
                    typingIn.Blur();
            }, TrickleDown.TrickleDown);
        }

        static bool InTextField(VisualElement element)
        {
            for (; element != null; element = element.parent)
                if (element is TextField)
                    return true;
            return false;
        }

        void OnDestroy()
        {
            if (app != null)
                app.ProjectNameChanged -= ShowName;
        }

        public void Show(UIStates state)
        {
            State = state;
            Apply();
            if (StateChanged != null)
                StateChanged(state);
        }

        /// <summary>Whether the second step can be pressed: there is a grid for the files that are loaded.</summary>
        public void SetAlignAvailable(bool available)
        {
            Ready();
            alignAvailable = available;
            stepAlign.SetEnabled(available || State != UIStates.Source);
        }

        /// <summary>Whether the third step can be pressed: the song is laid out and could be written.</summary>
        public void SetExportAvailable(bool available)
        {
            Ready();
            exportAvailable = available;
            stepExport.SetEnabled(available && State != UIStates.Source);
        }

        /// <summary>The line at the right of the status bar that lists the keys that do something on this screen.</summary>
        public void SetShortcuts(string text)
        {
            Ready();
            shortcuts.text = text;
        }

        /// <summary>
        /// Shows how much of the SP-16's sample memory the song takes, and how it is shared between
        /// the outputs: one length of the bar for each, in that output's colour.
        /// </summary>
        /// <param name="shares">Bytes and the colour to show them in, in the order they are drawn.</param>
        public void ShowMemory(IList<KeyValuePair<Color, long>> shares)
        {
            Ready();
            long total = 0;
            foreach (KeyValuePair<Color, long> share in shares)
                total += share.Value;
            long limit = Sp16Project.SampleMemoryBytes;
            memoryText.text = string.Format(Inv, "{0:0.0} of {1:0} MB", Sp16Project.Megabytes(total), Sp16Project.Megabytes(limit));
            memoryText.EnableInClassList(HighClass, total > 0.85 * limit && total <= limit);
            memoryText.EnableInClassList(OverClass, total > limit);

            memoryBar.Clear();
            // When the song is over the limit the bar is full, and the shares keep their proportions.
            double scale = MemoryBarWidth / (double)Math.Max(limit, total);
            foreach (KeyValuePair<Color, long> share in shares)
            {
                if (share.Value <= 0)
                    continue;
                var part = new VisualElement();
                part.AddToClassList("memory__part");
                part.style.width = Mathf.Max(1f, (float)(share.Value * scale));
                part.style.backgroundColor = share.Key;
                memoryBar.Add(part);
            }
        }

        void Request(UIStates state)
        {
            if (state != State && StepRequested != null)
                StepRequested(state);
        }

        void ShowName()
        {
            nameField.SetValueWithoutNotify(app.ProjectName);
        }

        void Apply()
        {
            Ready();
            VisualElement root = Root;
            root.Q("sourceScreen").EnableInClassList(HiddenClass, State != UIStates.Source);
            root.Q("alignScreen").EnableInClassList(HiddenClass, State != UIStates.Align);
            root.Q("analyse").EnableInClassList(HiddenClass, State != UIStates.Source);
            root.Q("exportScreen").EnableInClassList(HiddenClass, State != UIStates.Export);
            // The main button is always the next step: on to the Export page, and there the package itself.
            exportButton.EnableInClassList(HiddenClass, State == UIStates.Source);
            exportButton.text = State == UIStates.Export ? "Export package" : "Export";
            stepSource.EnableInClassList(OnClass, State == UIStates.Source);
            stepAlign.EnableInClassList(OnClass, State == UIStates.Align);
            stepExport.EnableInClassList(OnClass, State == UIStates.Export);
            stepAlign.SetEnabled(alignAvailable || State != UIStates.Source);
            // From the files the way to the Export page is through the analysis, which lays the song out.
            stepExport.SetEnabled(exportAvailable && State != UIStates.Source);
        }
    }
}
