using System;
using Strands.Applications.Session;
using Strands.Export;
using UnityEngine;

namespace Strands.Applications.Core
{
    public class ApplicationManager : MonoBehaviour
    {
        [SerializeField] TextAsset projectTemplate;
        [SerializeField] int frameRate = 60;

        bool nameIsTheUsers;
        double refreshRate;
        float nextLookAtTheDisplay;

        /// <summary>The unit-saved empty project that every export is patched from.</summary>
        public byte[] ProjectTemplate { get { return projectTemplate.bytes; } }

        /// <summary>The files the user has loaded, on the output strips they are to play from.</summary>
        public readonly StripLayout Strips = new StripLayout();

        /// <summary>The song being worked on, or null before one has been analysed.</summary>
        public SongSession Session { get; set; }

        /// <summary>
        /// The name the project and its package will have. It is shown at the top of both screens,
        /// so it is kept here and not with the song, which does not exist until the files are analysed.
        /// Empty when nothing is loaded.
        /// </summary>
        public string ProjectName { get; private set; }

        public event Action ProjectNameChanged;

        // Settings kept from one run of the app to the next (the user, 2026-10-14).
        const string AllOnMasterKey = "Strands.AllOnMaster", ClickKey = "Strands.Click";

        /// <summary>Whether the click is on in the audition. It starts on.</summary>
        public bool ClickOn
        {
            get { return PlayerPrefs.GetInt(ClickKey, 1) == 1; }
            set
            {
                PlayerPrefs.SetInt(ClickKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Puts every strip on master, or back on outputs 1 to 8, and remembers which for the next run.</summary>
        public void SetAllOnMaster(bool on)
        {
            Strips.AllOnMaster = on;
            PlayerPrefs.SetInt(AllOnMasterKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        void Awake()
        {
            ProjectName = "";
            // Someone with only the master output connected sets it once.
            Strips.AllOnMaster = PlayerPrefs.GetInt(AllOnMasterKey, 0) == 1;
            Application.targetFrameRate = frameRate;
            Application.runInBackground = true;
            SizeWindowOnFirstRun();
        }

        void Update()
        {
            KeepToFrameRate();
        }

        /// <summary>
        /// Holds a build to the frame rate the app asks for. The quality settings wait for every
        /// refresh of the display, and that wins over a frame rate that is only asked for: on a
        /// 120 Hz screen the window was drawn 120 times a second, which with nothing happening
        /// took about a quarter of one core (found by the profiler, 2026-10-11; the user: "Capping
        /// at 60 fps is fine"). Waiting for every second refresh there gives 60 and still moves in
        /// step with the display. The count is worked out again when the window is on a screen
        /// with another rate. Not in the Editor, where a change to the quality settings outlives
        /// play mode: there the Game view goes on being drawn at the display's rate.
        /// </summary>
        void KeepToFrameRate()
        {
            if (Application.isEditor || Time.unscaledTime < nextLookAtTheDisplay)
                return;
            nextLookAtTheDisplay = Time.unscaledTime + 1;
            double refresh = Screen.currentResolution.refreshRateRatio.value;
            if (refresh == refreshRate)
                return;
            refreshRate = refresh;
            QualitySettings.vSyncCount = Mathf.Clamp(Mathf.RoundToInt((float)refresh / frameRate), 1, 4);
        }

        /// <summary>
        /// The first time a build runs, its window is made two thirds as tall as the screen. The size
        /// in the player settings is in pixels, and on a Retina screen that many pixels are a window
        /// half as wide and half as tall as the screens were drawn for, with everything in it half
        /// size (found in the first build, 2026-10-11). Two thirds of the height is the size they
        /// were drawn at on a 16-inch MacBook Pro and on a 1920 x 1080 screen alike. After that the
        /// window is wherever and whatever size the user left it, which Unity remembers.
        /// </summary>
        void SizeWindowOnFirstRun()
        {
            const string done = "Strands.WindowSized";
            if (Application.isEditor || PlayerPrefs.GetInt(done, 0) != 0)
                return;
            int height = Screen.currentResolution.height * 2 / 3;
            Screen.SetResolution(height * 16 / 9, height, FullScreenMode.Windowed);
            PlayerPrefs.SetInt(done, 1);
        }

        /// <summary>A name taken from the files. It is used until the user types one of their own.</summary>
        public void SuggestProjectName(string name)
        {
            if (!nameIsTheUsers)
                Use(name);
        }

        /// <summary>The name the user typed, which is kept whatever files are added after it.</summary>
        public void SetProjectName(string name)
        {
            nameIsTheUsers = true;
            Use(name);
        }

        /// <summary>Back to no name, for when every file is taken away.</summary>
        public void ForgetProjectName()
        {
            nameIsTheUsers = false;
            Use(null);
        }

        void Use(string name)
        {
            string clean = string.IsNullOrEmpty(name) ? "" : WavToSp16.SanitizeName(name, SongSession.MaxNameLength);
            if (clean == ProjectName)
                return;
            ProjectName = clean;
            if (ProjectNameChanged != null)
                ProjectNameChanged();
        }
    }
}
