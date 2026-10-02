using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;

// Uploads an App Bundle to a Google Play track and rolls it out to everyone on that track, in one edit.
// The service account key comes in TRANSCRIBER_PLAY_KEY so it's never on disk unencrypted.
// Usage: PlayUpload <package> <bundle.aab> <track> <language> [notes.txt]

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: PlayUpload <package> <bundle.aab> <track> <language> [notes.txt]");
    return 2;
}
var (package, bundlePath, track, language) = (args[0], args[1], args[2], args[3]);
var notes = args.Length > 4 ? File.ReadAllText(args[4]).Trim() : "";
var key = Environment.GetEnvironmentVariable("TRANSCRIBER_PLAY_KEY");
if (string.IsNullOrEmpty(key))
{
    Console.Error.WriteLine("TRANSCRIBER_PLAY_KEY isn't set.");
    return 2;
}

var credential = CredentialFactory.FromJson<ServiceAccountCredential>(key).ToGoogleCredential().CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
using var service = new AndroidPublisherService(new BaseClientService.Initializer
{
    HttpClientInitializer = credential,
    ApplicationName = "Transcriber release",
});
service.HttpClient.Timeout = TimeSpan.FromMinutes(10);

var edit = await service.Edits.Insert(new AppEdit(), package).ExecuteAsync();

Console.WriteLine($"Uploading {Path.GetFileName(bundlePath)} ({new FileInfo(bundlePath).Length / 1048576.0:F1} MB)…");
Bundle bundle;
await using (var stream = File.OpenRead(bundlePath))
{
    var upload = service.Edits.Bundles.Upload(package, edit.Id, stream, "application/octet-stream");
    var progress = await upload.UploadAsync();
    if (progress.Status != UploadStatus.Completed) throw progress.Exception ?? new IOException("The upload didn't complete.");
    bundle = upload.ResponseBody;
}

var release = new TrackRelease
{
    VersionCodes = [(long?)bundle.VersionCode],
    Status = "completed",
    ReleaseNotes = notes.Length == 0 ? null : [new LocalizedText { Language = language, Text = notes }],
};
await service.Edits.Tracks.Update(new Track { TrackValue = track, Releases = [release] }, package, edit.Id, track).ExecuteAsync();
await service.Edits.Commit(package, edit.Id).ExecuteAsync();

Console.WriteLine($"versionCode {bundle.VersionCode} is rolled out on the {track} track.");
return 0;
