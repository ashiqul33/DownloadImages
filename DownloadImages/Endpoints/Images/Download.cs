namespace DownloadImages.Endpoints.Images;

internal sealed class Download : IEndpoint
{
    public record RequestDownload(IEnumerable<string> ImageUrls, int MaxDownloadAtOnce);

    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "image/download",
            (RequestDownload request) => Results.Ok((object?)request.ImageUrls));
    }
}