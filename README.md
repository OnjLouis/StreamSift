# StreamSift

StreamSift finds public live audio streams behind radio websites. It has
native Windows and Mac apps with keyboard-operated results, address copying,
M3U8 export and opening in the system's default playlist player. The Windows
app can also send streams to an installed copy of foobar2000.
Enter or Return plays a selected result in VLC or FastPlay on both platforms,
or foobar2000 on Windows. This does not add a saved playlist. Saving and
opening a playlist still uses the system default.
The editable address field remembers the last ten successful searches.
Select multiple results to save them as one playlist.
Results can be filtered by format, sorted by name, bitrate or reported
listener count, and searched without rescanning the website.

Read the [Windows manual](Manual.html) or [Mac manual](Mac/Manual.html).
Normal scans do not execute site JavaScript. The optional Find with Browser
command uses an installed Chrome or Edge in a temporary session without
sharing your browser profile or logins.
Website scans recognise supported SecureNet, Triton and TuneIn station pages. The
same search also understands publicly advertised server listings, including
Icecast, SHOUTcast, Global and the Locus Sonus soundmap. The
public-directory command matches websites against Radio Browser; station
names and call signs are matched against Radio Browser and the
[International Public Radio Directory](https://iprd-org.github.io/iprd/).
StreamSift matches the downloaded catalogue data locally before verifying
stream addresses; it does not send your search text to the directories.
When the Radio Browser cache needs refreshing, StreamSift can try another
public mirror if one is unavailable.

Optional automatic fallback runs the selected method first. When no live
stream is verified, it tries the remaining methods in website, browser, then
directory order. This is off by default on both platforms.

## Experimental video support

Website and browser-assisted searches can discover public MP4, WebM and HLS
video, including supported AES-128 HLS streams. A verified HLS video rendition
is returned as its master playlist, allowing the player to select quality and
audio. On Windows and Mac, video uses VLC; Windows can also use Winamp for some
direct videos. VLC must be installed, or on Windows a portable VLC distribution
may be placed in the app's `VLC` folder. VLC is not included in this repository
or the standard build.

Video discovery includes adapters for the public NBC10 Boston, CBS Boston,
Boston 25 and WCVB player pages. WCVB may expose a replay between broadcasts.
Known duplicate video qualities and HLS renditions are grouped. A normal website
search can check up to 120 addresses, subject to separate time and result limits.
DASH, DRM, logins, special playback headers and general HLS video-on-demand
support are not included. Availability and playback can vary by station and region.
Mac video playback and WCVB replay playback require further testing.

## Build and test

The shared discovery engine requires the .NET 10 SDK to build. The Windows
app uses the .NET 10 Desktop Runtime (x64). From the source directory:

```powershell
dotnet build Tests/Tests.csproj -c Release --artifacts-path ../StreamSift-build
dotnet ../StreamSift-build/bin/Tests/release/Tests.dll
dotnet publish Windows/StreamSift.csproj -c Release --artifacts-path ../StreamSift-build --no-self-contained -r win-x64
```

The test runner performs local deterministic checks without using an account
or touching a player. Pass public HTTP or HTTPS addresses as separate arguments
to run the same discovery engine against those sites.
Run the test runner with `--browser-self-test` for the local browser-assisted
fixture; it requires Chrome or Edge.

On Windows, build `WindowsTests/WindowsTests.csproj` and run the resulting
`StreamSift.WindowsTests.dll` with a new, disposable test directory as its
only argument to check preferences, player handoff and signed updates.
The vendored updater is from Onj Signed Updater commit
`58aaf32bc3bb3e4488395b23686362e753546464`.

On Mac, run the following with Xcode and the .NET 10 SDK installed. The
universal app bundles a native copy of the shared scanner, so users do
not need .NET installed. The build runs the app's isolated self-tests.

~~~bash
bash Mac/Build.sh ../StreamSift-mac-build
~~~

## License

StreamSift is released under the [MIT License](LICENSE). Third-party
components retain their own license notices.
