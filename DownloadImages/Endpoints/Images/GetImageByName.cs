namespace DownloadImages.Endpoints.Images;

internal sealed class GetImageByName : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "get-image-by-name/{image_name}",
            async (string image_name, IWebHostEnvironment env, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(image_name))
                {
                    return Results.BadRequest("image_name is required.");
                }

                string sanitizedName = Path.GetFileName(image_name);
                if (string.IsNullOrEmpty(sanitizedName) || sanitizedName != image_name)
                {
                    return Results.BadRequest("Invalid image_name.");
                }

                string fullPath = Path.Combine(env.WebRootPath, "images", sanitizedName);

                if (!File.Exists(fullPath))
                {
                    return Results.NotFound("Image not found.");
                }

                byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                string base64 = Convert.ToBase64String(bytes);

                return Results.Content(base64, "text/plain");
            }
        );
    }
}
