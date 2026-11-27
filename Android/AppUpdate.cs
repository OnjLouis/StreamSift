using Android.App;
using Android.Content;
using Android.Content.PM;
using OperationCanceledException = System.OperationCanceledException;

namespace StreamSift.Mobile;

public sealed partial class MainActivity
{
    private CancellationTokenSource? updateOperation;

    private async Task CheckForUpdateAsync()
    {
        if (updateOperation != null) return;
        using var operation = new CancellationTokenSource();
        updateOperation = operation;
        using var dialog = new AlertDialog.Builder(this).SetTitle("Checking for Updates")!
            .SetNegativeButton("Cancel", (_, _) => operation.Cancel())!.Create()!;
        dialog.CancelEvent += (_, _) => operation.Cancel();
        dialog.Show();

        Version? current = null;
        Version? latest = null;
        string? failure = null;
        try
        {
            using var installed = PackageManager?.GetPackageInfo(PackageName!, PackageInfoFlags.MetaData);
            if (!Version.TryParse(installed?.VersionName, out current))
                throw new InvalidDataException("The installed StreamSift version is unavailable.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = PublicNetwork.CreateClient();
            latest = await AndroidReleaseCheck.CheckAsync(client, current, timeout.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (InvalidDataException error) { failure = error.Message; }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or
            System.Text.Json.JsonException or Java.Lang.Exception)
        {
            failure = "Check your connection and try again.";
        }
        finally
        {
            updateOperation = null;
            dialog.Dismiss();
        }

        if (operation.IsCancellationRequested || IsFinishing || IsDestroyed) return;
        if (failure != null) UpdateMessage("Update Check Failed", failure);
        else if (latest == null)
            UpdateMessage("No New Android Release", $"Installed version {current}.");
        else
            new AlertDialog.Builder(this).SetTitle($"StreamSift {latest.ToString(3)} Available")!
                .SetMessage("Open the release page to download the update and confirm installation in Android.")!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .SetPositiveButton("Open Release Page", (_, _) => OpenUpdateRelease(latest))!.Show();
    }

    private void OpenUpdateRelease(Version version)
    {
        using var address = Android.Net.Uri.Parse(AndroidReleaseCheck.ReleasePage(version));
        using var open = new Intent(Intent.ActionView, address);
        try { StartActivity(open); }
        catch (ActivityNotFoundException) { UpdateMessage("Release Page Unavailable", "No browser is available."); }
    }

    private void UpdateMessage(string title, string message) =>
        new AlertDialog.Builder(this).SetTitle(title)!.SetMessage(message)!
            .SetPositiveButton("Close", (_, _) => { })!.Show();
}
