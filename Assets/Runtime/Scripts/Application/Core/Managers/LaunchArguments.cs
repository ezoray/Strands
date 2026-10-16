using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Strands.UI.Source;
using Strands.Utils;
using UnityEngine;
using UnityEngine.Profiling;

namespace Strands.Applications.Core
{
    /// <summary>
    /// What a build does with the arguments it is started with. It is a developer's way to check a
    /// build from a script, where there is no Editor to drive it and no way to click:
    ///
    ///   Strands.app/Contents/MacOS/Strands -open Drums.wav Bass.wav -analyse -picture shot.png -then-quit
    ///
    /// -open loads the files that follow it, as Add files does; a folder among them stands for its
    /// audio files, as Add folder takes them. Paths are from the folder the app was started in. -analyse goes on to the Align
    /// screen. -picture saves a picture of the window, and beside it a text file with the window's
    /// size, the frame rate and the memory in use. -then-quit closes the app afterwards.
    /// Started without any of these the app does nothing here, and in the Editor it never does.
    /// </summary>
    public class LaunchArguments : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Begin()
        {
            if (Application.isEditor)
                return;
            var files = new List<string>();
            bool analyse = false, quit = false, any = false;
            string picture = null;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 1; i < arguments.Length; i++)
            {
                if (arguments[i] == "-open")
                {
                    any = true;
                    while (i + 1 < arguments.Length && !arguments[i + 1].StartsWith("-", StringComparison.Ordinal))
                        Open(FullPath(arguments[++i]), files);
                }
                else if (arguments[i] == "-analyse")
                {
                    any = analyse = true;
                }
                else if (arguments[i] == "-picture" && i + 1 < arguments.Length)
                {
                    any = true;
                    picture = FullPath(arguments[++i]);
                }
                else if (arguments[i] == "-then-quit")
                {
                    any = quit = true;
                }
            }
            if (!any)
                return;
            Debug.Log("Launch arguments: opening " + (files.Count == 0 ? "nothing" : string.Join(", ", files.ToArray())));
            var runner = new GameObject("LaunchArguments").AddComponent<LaunchArguments>();
            runner.StartCoroutine(runner.Run(files, analyse, picture, quit));
        }

        IEnumerator Run(List<string> files, bool analyse, string picture, bool quit)
        {
            // The screens make themselves in their own Start, which comes after this.
            yield return null;
            yield return null;
            var source = FindAnyObjectByType<SourceUIManager>();
            var jobs = FindAnyObjectByType<JobManager>();
            if (files.Count > 0)
            {
                source.AddFiles(files.ToArray());
                yield return new WaitUntil(() => !jobs.Busy);
                if (analyse)
                {
                    source.Analyse();
                    yield return new WaitUntil(() => !jobs.Busy);
                }
            }
            if (picture != null)
            {
                // Long enough for the screen to settle, and to count the frames of a few seconds.
                yield return new WaitForSecondsRealtime(1);
                int frame = Time.frameCount;
                float time = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(3);
                float framesPerSecond = (Time.frameCount - frame) / (Time.realtimeSinceStartup - time);
                // Written here and now: CaptureScreenshot saves later on, which a quit straight after
                // can cut off, and puts a relative path wherever the platform chooses (found on
                // Windows, 2026-10-16, where only the text file came back).
                yield return new WaitForEndOfFrame();
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(picture, shot.EncodeToPNG());
                Destroy(shot);
                Debug.Log("Launch arguments: picture saved to " + picture);
                File.WriteAllText(picture + ".txt", Describe(framesPerSecond));
            }
            if (quit)
                Application.Quit();
        }

        /// <summary>
        /// A path as the shell that started the app meant it. The player moves its working folder
        /// to the one the app is in (seen on macOS, 2026-10-16), so a relative path is taken from
        /// the shell's folder where the shell says what it is (PWD), and from the app's otherwise.
        /// </summary>
        static string FullPath(string path)
        {
            string shell = Environment.GetEnvironmentVariable("PWD");
            if (Path.IsPathRooted(path) || string.IsNullOrEmpty(shell) || !Directory.Exists(shell))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(shell, path));
        }

        /// <summary>A file as it is, or a folder's audio files; a path that is neither is logged and left out.</summary>
        static void Open(string path, List<string> files)
        {
            if (Directory.Exists(path))
                files.AddRange(FileDialogs.AudioFilesIn(path));
            else if (File.Exists(path))
                files.Add(path);
            else
                Debug.LogWarning("Launch arguments: no file or folder at " + path);
        }

        static string Describe(float framesPerSecond)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            Resolution display = Screen.currentResolution;
            return string.Format(inv,
                "window {0} x {1} pixels, {2:0} dpi\n" +
                "display {3} x {4} pixels at {5:0.#} Hz\n" +
                "frames a second {6:0.0} (asked for {7}, every {8} refresh of the display)\n" +
                "managed memory in use {9:0.0} MB of a {10:0.0} MB heap\n" +
                "Unity's own memory {11:0.0} MB, all reserved {12:0.0} MB\n",
                Screen.width, Screen.height, Screen.dpi,
                display.width, display.height, display.refreshRateRatio.value,
                framesPerSecond, Application.targetFrameRate, QualitySettings.vSyncCount,
                Profiler.GetMonoUsedSizeLong() / 1048576.0, Profiler.GetMonoHeapSizeLong() / 1048576.0,
                Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, Profiler.GetTotalReservedMemoryLong() / 1048576.0);
        }
    }
}
