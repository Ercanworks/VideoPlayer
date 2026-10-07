# Video Player

A clean, Movies & TV–style video player for Windows 10/11, powered by **mpv**. It plays
virtually any format and stays smooth during screen sharing (e.g. Discord streams).

The interface is shown in Turkish on Turkish Windows and in English everywhere else.

## Features

- Borderless Movies & TV–style interface; controls and window buttons fade out when the
  mouse stops moving
- Movies & TV–style reveal lighting: the square control buttons' borders light up around the
  cursor as it approaches
- Other videos in the opened file's folder are queued in File Explorer order
- Movies & TV–style previous/next arrows on the left and right edges of the video (hidden when there is no video in that direction)
- **Drag to switch:** grab the video and drag it sideways — it slides inside the window,
  and if you drag far enough it slides out and the next/previous video in the folder starts
- Movies & TV–style layout measured pixel by pixel: the video title appears in large type for
  ~2.7 s when paused and the mouse moves (never while playing), with a deeper shade behind it
- Movies & TV–style seek bar: thin track in the light accent color, ring thumb that fills while
  pressed, elapsed and remaining time (hh:mm:ss) below the bar; click the right one for total time
- **Precise seeking:** press anywhere on the seek bar and drag; the video pauses while the
  button is held and shows the exact frame under the cursor. Move the cursor up away from the
  bar for up to 3× finer control
- Even frame pacing: every video frame reaches the screen (e.g. 60 fps video on a 144 Hz screen)
- **Subtitles and audio tracks:** Movies & TV–style menu next to the volume button to choose
  embedded or external subtitles and audio tracks, or load a subtitle file (or just drop one on
  the window). Subtitle files are found automatically even with a language suffix
  (`Movie.tr.srt`, `Movie.English.srt`) or in a `Subs` folder; subtitles in the Windows display
  language are preferred, and your choice carries over to the next video in the folder
- Subtitles move up above the controls while they are visible, and back into the black bar
  when they fade out
- Follows the Windows caption settings (Settings > Ease of Access > Captions), like Movies & TV
- **Media keys work everywhere:** keyboard and headset play/pause/next/previous keys control
  the player even when it isn't focused, and the Windows media overlay shows the video title
- Full screen, maximize and an always-on-top mini view
- Playback speed button (0.25× – 2×) next to mini view; when a video ends: stop / play next in folder / repeat
- Remembers window size, position and volume
- Single window: opening another video while the player is running reuses the same window
- "Make default video player" option

## Keyboard shortcuts

| Key | Action |
|---|---|
| Space / K | Play / pause |
| ← / → | Back 10 s / forward 30 s |
| ↑ / ↓ | Volume |
| M | Mute / unmute |
| F / F11 / double-click | Full screen (Esc to exit) |
| N / P | Next / previous video |
| Shift + . / Shift + , | Faster / slower (0.25× steps, like YouTube) |
| . / , | Next / previous frame (while paused) |
| C | Subtitles on / off |
| 0 – 9 | Jump to 0 % – 90 % of the video |
| Ctrl+O | Open file |
| Mouse side buttons | Previous / next video |

## Building

Requirements: .NET 8 SDK (with the Windows 10 SDK targeting pack, restored automatically), 7-Zip.

```powershell
# Download the mpv library (once)
.\tools\libmpv-indir.ps1

# Build into the Uygulama folder
dotnet publish -c Release -o Uygulama -p:DebugType=none
```

`libmpv-2.dll` is not in the repository because it exceeds GitHub's file size limit; the
script downloads it into `lib\` and the build copies it to the output.

To try the other interface language, set the `VIDEOPLAYER_LANG` environment variable to
`en` or `tr` before starting the player.

## Credits

- [mpv](https://mpv.io) — libmpv, [shinchiro builds](https://github.com/shinchiro/mpv-winbuild-cmake)
