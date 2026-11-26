# StreamSift

StreamSift finds public radio streams, video and audio files. It has
native Windows and Mac apps with keyboard-operated results, address copying,
M3U8 export and opening in the system's default playlist player. The Windows
app can also send streams to an installed copy of foobar2000.
Enter or Return sends the selected result to the chosen media player.
Automatic uses the system default playlist app when available; neither
choice adds a saved playlist. Saving and opening a playlist still uses
the system default.
Find Streams and Files also accepts a directly entered public HTTPS audio file that
opens without a redirect, query string or sign-in. A verified file appears
as a Track. Use Enter or Return to play it. Track addresses are left out of
recent searches and restored results. This works with media file addresses
such as a public MP3 link supplied by its publisher.
Public HTTPS indexes with the standard “Index of” heading list the audio
files in the current folder. Select one or more tracks and press Enter or
Return to play the selection in the chosen player. Filter and Sort can order
these tracks by modified date and file size where shown in the index.
The editable address field remembers the last ten successful searches.
Select multiple results to save them as one playlist.
Results can be filtered by format, sorted by name, bitrate or reported
listener count, and searched without rescanning the website.

Save stations and tracks in Favourites, transfer them as M3U8 playlists,
or use Play Random in search results or Favourites. StreamSift also finds
audio in podcast feeds, public Internet Archive items, and supported articles
and demo pages.

Read the [Windows manual](Manual.html) or [Mac manual](Mac/Manual.html).

### Search

Normal scans do not execute site JavaScript. The optional Find with Browser
command uses an installed Chrome or Edge in a temporary session without
sharing your browser profile or logins.
Website scans recognise supported SecureNet, Triton and TuneIn station pages. The
same search also understands publicly advertised server listings, including
Icecast, SHOUTcast, Global and the Locus Sonus soundmap. The
public-directory command matches websites against Radio Browser; station
names and call signs are matched against Radio Browser and the
[International Public Radio Directory](https://iprd-org.github.io/iprd/).
If these yield no playable match, station-name searches also check the public
[Xiph Icecast directory](https://dir.xiph.org/).
StreamSift matches catalogue data locally and can reuse a matching recent
station website on a name search. It verifies the streams again and can
prefer an equivalent media-player address when a streaming panel offers one.
It does not send your search text to the directories.
When the Radio Browser cache needs refreshing, StreamSift can try another
public mirror if one is unavailable.

Optional automatic fallback runs the selected method first. When no
result is found, it tries the remaining methods in website, radio directory, then
browser order. This is off by default on both platforms.

Website and browser searches also recognise public MP4 and WebM video and
unencrypted live HLS video with no separate audio playlist. The configured
player can open video or just its audio, depending on the player. Automatic
uses the default playlist app when available, with a temporary file when
needed. Thanks to Cory for the video discovery contribution.

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

### Windows 7

The Windows 7 SP1 build uses the final .NET 6.0.36 Desktop Runtime for the
selected architecture. Microsoft has ended support for Windows 7 and .NET 6.
Passive search, radio directory lookup, and external-player playback are
available. Browser-assisted search requires the current desktop build and
a supported browser. HTTPS requires TLS 1.2 and an up-to-date certificate
store. See [Microsoft's Windows prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
and [.NET 6.0.36 Desktop Runtime downloads](https://dotnet.microsoft.com/en-us/download/dotnet/6.0).

```powershell
dotnet publish Windows/StreamSift.csproj -c Release -p:StreamSiftWindows7=true -r win-x86 --no-self-contained --artifacts-path ../StreamSift-win7-x86
dotnet publish Windows/StreamSift.csproj -c Release -p:StreamSiftWindows7=true -r win-x64 --no-self-contained --artifacts-path ../StreamSift-win7-x64
```

Build `WindowsTests/WindowsTests.csproj` with the same property and runtime
identifier to exercise the legacy updater. The Windows 7 release family
uses `win7-v<version>` tags and signed `StreamSift-Windows7-x86-<version>.zip`
and `StreamSift-Windows7-x64-<version>.zip` assets. Their signed platform
identifiers are `windows7-x86` and `windows7-x64`. Publish these tags with
GitHub's latest designation disabled so the macOS release feed keeps its
ordinary desktop release.

On Mac, run the following with Xcode and the .NET 10 SDK installed. The
universal app bundles a native copy of the shared scanner, so users do
not need .NET installed. The build runs the app's isolated self-tests.

~~~bash
bash Mac/Build.sh ../StreamSift-mac-build
~~~

## License

StreamSift is released under the [MIT License](LICENSE). Third-party
components retain their own license notices.
