using System.Text;
using Android.App;
using Android.Content;
using Android.Provider;
using Transcriber.Core.Output;
using Uri = Android.Net.Uri;

namespace Transcriber.Mobile;

/// <summary>
/// A folder the user picked with Android's folder picker (Storage Access Framework), for example the
/// vault folder Obsidian mobile or Syncthing uses. Settings store it as a content:// tree URI.
/// </summary>
public static class PhoneFolder
{
    private const int PickRequest = 4711;
    private static TaskCompletionSource<string?>? _pending;

    /// <summary>Opens the system folder picker and keeps permission to write there across restarts.</summary>
    public static Task<string?> PickAsync()
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionOpenDocumentTree)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        Platform.CurrentActivity!.StartActivityForResult(intent, PickRequest);
        return _pending.Task;
    }

    /// <summary>Called from MainActivity.OnActivityResult.</summary>
    public static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != PickRequest) return;
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null)
        {
            Platform.AppContext.ContentResolver!.TakePersistableUriPermission(uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        _pending?.TrySetResult(uri?.ToString());
        _pending = null;
    }

    /// <summary>"primary:Documents/Vault" → "Documents/Vault" for display.</summary>
    public static string Describe(string? treeUri)
    {
        if (string.IsNullOrEmpty(treeUri) || !treeUri.StartsWith("content://", StringComparison.Ordinal)) return "No folder chosen";
        try
        {
            var id = DocumentsContract.GetTreeDocumentId(Uri.Parse(treeUri)) ?? treeUri;
            var colon = id.IndexOf(':');
            var path = colon >= 0 ? id[(colon + 1)..] : id;
            return path.Length == 0 ? "Internal storage" : path;
        }
        catch (Java.Lang.Exception)
        {
            return treeUri;
        }
    }
}

public sealed class PhoneFolderDestination(string treeUri) : INoteDestination
{
    public async Task<SavedNote> SaveAsync(string fileName, string markdown, CancellationToken ct)
    {
        if (!treeUri.StartsWith("content://", StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a folder for notes in Settings → Output.");

        var resolver = Platform.AppContext.ContentResolver!;
        var tree = Uri.Parse(treeUri)!;
        var folderId = DocumentsContract.GetTreeDocumentId(tree)!;
        var folder = DocumentsContract.BuildDocumentUriUsingTree(tree, folderId)!;

        var existing = ListNames(resolver, tree, folderId);
        var unique = await FileNames.UniqueAsync(fileName, n => Task.FromResult(existing.Contains(n)));

        // octet-stream stops providers from appending their own extension ("note.md.txt").
        var document = DocumentsContract.CreateDocument(resolver, folder, "application/octet-stream", unique)
            ?? throw new IOException("Android couldn't create the note in the chosen folder. Pick the folder again in Settings.");
        await using (var stream = resolver.OpenOutputStream(document, "wt") ?? throw new IOException("Couldn't open the new note for writing."))
        {
            var bytes = new UTF8Encoding(false).GetBytes(markdown);
            await stream.WriteAsync(bytes, ct);
        }
        return new SavedNote($"{PhoneFolder.Describe(treeUri)}/{unique}", null);
    }

    private static HashSet<string> ListNames(ContentResolver resolver, Uri tree, string folderId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, folderId)!;
        using var cursor = resolver.Query(children, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
        while (cursor?.MoveToNext() == true)
        {
            var name = cursor.GetString(0);
            if (name is not null) names.Add(name);
        }
        return names;
    }
}
