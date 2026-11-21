# StreamSift

StreamSift finds public live audio streams behind radio websites. It has
native Windows and Mac apps with keyboard-operated results, address copying,
M3U8 export and opening in the system's default playlist player. The Windows
app can also send streams to an installed copy of foobar2000.

Read the [Windows manual](Manual.html) or [Mac manual](Mac/Manual.html).
Scans do not execute site JavaScript or share browser logins.

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
