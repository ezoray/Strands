# StandaloneFileBrowser

Native file dialogs for Unity on macOS, Windows and Linux, by Gökhan Gökçe, with Linux support by Ricardo Rodrigues, under the MIT licence (see `LICENSE.txt`).

Source: https://github.com/gkngkc/UnityStandaloneFileBrowser, the `Assets/StandaloneFileBrowser` folder (version 1.2). The sample scenes were not taken.

The app reaches it only through `FileDialogs` in `Assets/Runtime/Scripts/Utils`.

## Changed from the original

**`Plugins/StandaloneFileBrowser.bundle`**: the macOS plugin is a universal build (Intel and Apple silicon) of the same source, signed ad hoc. The one in the original repository is for Intel only.

Everything else is as it was downloaded: the C# files, the Windows libraries (`Ookii.Dialogs.dll` and Mono's `System.Windows.Forms.dll`), the Linux library and the WebGL file were checked against the original on 2026-10-15.
