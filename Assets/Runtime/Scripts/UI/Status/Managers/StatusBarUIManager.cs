using Strands.Applications.Core;
using Strands.UI.State;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Status
{
    /// <summary>The bar along the bottom: what is running, how far along it is, and a way to stop it.</summary>
    public class StatusBarUIManager : MonoBehaviour
    {
        const string HiddenClass = "hidden";
        const string BusyClass = "status__text--busy";
        // The four dots that show something is running light up in turn, this many times a second.
        const float TurnsPerSecond = 1.1f;
        const float Dimmest = 0.2f;

        [SerializeField] UIStateManager uiState;
        [SerializeField] JobManager jobs;

        Label text;
        ProgressBar progress;
        Button cancel;
        VisualElement busy;

        void Start()
        {
            VisualElement root = uiState.Root;
            text = root.Q<Label>("statusText");
            progress = root.Q<ProgressBar>("statusProgress");
            cancel = root.Q<Button>("statusCancel");
            busy = root.Q("statusBusy");
            cancel.clicked += jobs.Cancel;
        }

        void Update()
        {
            text.text = jobs.Status;
            bool known = jobs.Busy && jobs.Progress >= 0;
            progress.EnableInClassList(HiddenClass, !known);
            if (known)
                progress.value = jobs.Progress * 100f;
            cancel.EnableInClassList(HiddenClass, !jobs.Busy);
            // Work that cannot say how far along it is, such as finding the grid, would otherwise
            // show nothing moving at all.
            text.EnableInClassList(BusyClass, jobs.Busy);
            busy.EnableInClassList(HiddenClass, !jobs.Busy);
            if (!jobs.Busy)
                return;
            float turn = Time.unscaledTime * TurnsPerSecond;
            for (int dot = 0; dot < busy.childCount; dot++)
            {
                // Each dot is at its brightest a quarter of a turn after the one before it.
                float lit = 0.5f + 0.5f * Mathf.Cos(2 * Mathf.PI * (turn - dot / (float)busy.childCount));
                busy[dot].style.opacity = Dimmest + (1 - Dimmest) * lit * lit;
            }
        }
    }
}
