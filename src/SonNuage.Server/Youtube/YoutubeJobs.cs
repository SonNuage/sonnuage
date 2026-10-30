using SonNuage.Server.Jobs;

namespace SonNuage.Server.Youtube;

/// <summary>Étape 1 d'un import YouTube : téléchargement et conversion.</summary>
public sealed class YoutubeDownloadJob(YoutubeImporter importer) : IJobHandler
{
    public JobKind Kind => JobKinds.YoutubeDownload;

    public Task RunAsync(JobContext ctx) => importer.DownloadAsync(ctx);

    public Task CanceledAsync(IServiceProvider services, Guid? subjectId, CancellationToken ct) => importer.CanceledAsync(services, subjectId, ct);
}

/// <summary>Étape 3 d'un import YouTube : métadonnées, tags, rangement (l'étape 2 est <see cref="AcoustIdIdentifyJob"/>).</summary>
public sealed class YoutubeMetadataJob(YoutubeImporter importer) : IJobHandler
{
    public JobKind Kind => JobKinds.YoutubeMetadata;

    public Task RunAsync(JobContext ctx) => importer.ProcessAsync(ctx);

    public Task CanceledAsync(IServiceProvider services, Guid? subjectId, CancellationToken ct) => importer.CanceledAsync(services, subjectId, ct);
}
