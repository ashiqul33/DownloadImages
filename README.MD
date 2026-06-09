# DownloadImages

ASP.NET Core 8 minimal API. Downloads a batch of images in parallel
(capped by `maxDownloadAtOnce`) into `wwwroot/images/` with GUID file
names, and lets you fetch any downloaded image back as a base64 string.

## Run

```bash
cd DownloadImages
dotnet run
```

Default URL: `http://localhost:5251` (matches the included Postman
collection). Swagger UI at `/swagger`.

## Endpoints

### `POST /image/download`

Body:

```json
{
  "imageUrls": ["https://picsum.photos/seed/alpha/1200/800"],
  "maxDownloadAtOnce": 5
}
```

- `200` on full success: `{ "success": true, "message": null, "urlAndNames": { "url": "fileName", ... } }`
- `400` on any failure (validation, IO, or per-URL error after rollback): `{ "success": false, "message": "...", "urlAndNames": {} }`

All-or-nothing: one failure rolls back the whole batch. Per-download
timeout is 30s; client disconnect cancels the batch.

### `GET /get-image-by-name/{image_name}`

- `200` — body is the base64 string, `Content-Type: text/plain`.
- `400` — `image_name` empty or contains path-traversal characters.
- `404` — file not found under `wwwroot/images/`.

## Postman collection

Import `DownloadImages.postman_collection.json` (repo root) into Postman,
or view the [online documentation](https://documenter.getpostman.com/view/7642403/2sBXwqtBaW).

It contains:

- `download` — `POST /image/download` with a sample body of picsum.photos URLs.
- `GetByName` — `GET /get-image-by-name/{fileName}`. The file name in the
  URL is from a previous run; send `download` first, then replace the
  path segment with one of the returned `fileName` values.
