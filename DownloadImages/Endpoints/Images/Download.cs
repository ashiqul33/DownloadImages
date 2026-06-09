using System.Collections.Concurrent;

namespace DownloadImages.Endpoints.Images;

internal sealed class Download : IEndpoint
{
    private record RequestDownload(IEnumerable<string> ImageUrls, int MaxDownloadAtOnce);

    private record ResponseDownload(bool Success, string? Message, IDictionary<string, string> UrlAndNames);

    private const int ChunkSize = 65536;
    private const int PerDownloadTimeoutSeconds = 30;

    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "image/download",
            async (
                RequestDownload request,
                IHttpClientFactory httpClientFactory,
                IWebHostEnvironment env,
                CancellationToken cancellationToken
            ) =>
            {
                if (request.MaxDownloadAtOnce <= 0)
                {
                    return Results.BadRequest(
                        new ResponseDownload(
                            false,
                            "MaxDownloadAtOnce must be greater than zero.",
                            new Dictionary<string, string>()
                        )
                    );
                }

                List<string> uniqueUrls = request.ImageUrls
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (uniqueUrls.Count == 0)
                {
                    return Results.BadRequest(
                        new ResponseDownload(
                            false,
                            "No valid image URLs provided.",
                            new Dictionary<string, string>()
                        )
                    );
                }

                int maxConcurrent = request.MaxDownloadAtOnce;
                int downloadCount = uniqueUrls.Count;

                string storagePath = Path.Combine(env.WebRootPath, "images");
                Directory.CreateDirectory(storagePath);

                var urlToNameMap = new ConcurrentDictionary<string, string>(downloadCount, downloadCount);
                using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

                var tasks = new Task<DownloadOutcome>[downloadCount];
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var firstErrorFlag = new FirstErrorFlag();

                for (int i = 0; i < downloadCount; i++)
                {
                    tasks[i] = DownloadOneAsync(
                        uniqueUrls[i],
                        storagePath,
                        httpClientFactory,
                        semaphore,
                        urlToNameMap,
                        firstErrorFlag,
                        batchCts
                    );
                }

                await Task.WhenAll(tasks);

                string? firstError = firstErrorFlag.Get();
                if (firstError is not null)
                {
                    RollbackFiles(env.WebRootPath, urlToNameMap);

                    return Results.BadRequest(
                        new ResponseDownload(
                            false,
                            firstError,
                            new Dictionary<string, string>()
                        )
                    );
                }

                return Results.Ok(
                    new ResponseDownload(
                        true,
                        null,
                        new Dictionary<string, string>(urlToNameMap)
                    )
                );
            }
        );
    }

    private static async Task<DownloadOutcome> DownloadOneAsync(
        string url,
        string storagePath,
        IHttpClientFactory httpClientFactory,
        SemaphoreSlim semaphore,
        ConcurrentDictionary<string, string> urlToNameMap,
        FirstErrorFlag firstErrorFlag,
        CancellationTokenSource batchCts
    )
    {
        await semaphore.WaitAsync(batchCts.Token);
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(PerDownloadTimeoutSeconds));
            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(batchCts.Token, timeoutCts.Token);

            HttpClient httpClient = httpClientFactory.CreateClient();

            using HttpResponseMessage response = await httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            response.EnsureSuccessStatusCode();

            string extension = ResolveExtension(response, url);
            string fileName = $"{Guid.NewGuid():N}{extension}";
            string fullPath = Path.Combine(storagePath, fileName);

            await using FileStream fs = new(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            await using Stream body = await response.Content.ReadAsStreamAsync(linkedCts.Token);

            byte[] buffer = new byte[ChunkSize];
            int read;
            while (
                (read = await body.ReadAsync(buffer.AsMemory(0, ChunkSize), linkedCts.Token)) >
                0
            )
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token);
            }

            await fs.FlushAsync(linkedCts.Token);

            urlToNameMap[url] = fileName;
            return DownloadOutcome.Success;
        }
        catch (OperationCanceledException) when (batchCts.Token.IsCancellationRequested)
        {
            return DownloadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            if (firstErrorFlag.TrySet($"Failed to download '{url}': {ex.Message}"))
            {
                try
                {
                    await batchCts.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return DownloadOutcome.Failed;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static void RollbackFiles(string webRootPath, ConcurrentDictionary<string, string> urlToNameMap)
    {
        foreach (KeyValuePair<string, string> entry in urlToNameMap)
        {
            try
            {
                string path = Path.Combine(webRootPath, "images", entry.Value);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveExtension(HttpResponseMessage response, string url)
    {
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrEmpty(contentType))
        {
            string mapped = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/bmp" => ".bmp",
                "image/svg+xml" => ".svg",
                "image/tiff" => ".tiff",
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(mapped))
            {
                return mapped;
            }
        }

        string pathOnly = new Uri(url).AbsolutePath;
        string ext = Path.GetExtension(pathOnly);
        return string.IsNullOrEmpty(ext) ? ".bin" : ext;
    }

    private enum DownloadOutcome
    {
        Success,
        Failed,
        Cancelled
    }

    private sealed class FirstErrorFlag
    {
        private string? _message;
        private int _set;

        public bool TrySet(string message)
        {
            if (Interlocked.CompareExchange(ref _set, 1, 0) == 0)
            {
                Volatile.Write(ref _message, message);
                return true;
            }

            return false;
        }

        public string? Get()
        {
            return Volatile.Read(ref _message);
        }
    }
}