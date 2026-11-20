using System.Text.Json;
using Invitations.Api.Domain;
using Invitations.Api.Features.Photos;

namespace Invitations.Tests;

public class PhotosTests
{
    private static readonly Guid EventId = Guid.Parse("0193a0b1-0000-7000-8000-000000000001");
    private static readonly Guid OtherEvent = Guid.Parse("0193a0b1-0000-7000-8000-000000000002");
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static string Key(Guid ev) => Photos.NewKey(ev, "image/webp");

    [Fact]
    public void NewKey_is_scoped_to_event_and_matches_its_own_pattern()
    {
        var key = Key(EventId);
        Assert.StartsWith(Photos.Prefix(EventId), key);
        Assert.EndsWith(".webp", key);
        Assert.True(Photos.BelongsTo(key, EventId));
        Assert.False(Photos.BelongsTo(key, OtherEvent));
    }

    [Theory]
    [InlineData("events/x/../otro.webp")]
    [InlineData("https://evil.example/foto.webp")]
    [InlineData("")]
    public void BelongsTo_rejects_foreign_or_malformed_keys(string key) =>
        Assert.False(Photos.BelongsTo(key, EventId));

    [Theory]
    [InlineData("image/webp", 200_000, true)]
    [InlineData("image/jpeg", Photos.MaxBytes, true)]
    [InlineData("image/png", 1, true)]
    [InlineData("image/svg+xml", 1_000, false)]
    [InlineData("text/html", 1_000, false)]
    [InlineData(null, 1_000, false)]
    [InlineData("image/webp", 0, false)]
    [InlineData("image/webp", Photos.MaxBytes + 1, false)]
    public void ValidateUpload_allows_only_small_raster_images(string? type, long size, bool ok) =>
        Assert.Equal(ok, Photos.ValidateUpload(type, size) is null);

    [Fact]
    public void Validate_accepts_config_without_photos() =>
        Assert.Null(Photos.Validate(Json("""{"texts":{}}"""), EventId));

    [Fact]
    public void Validate_accepts_own_cover_and_gallery()
    {
        var config = Json(JsonSerializer.Serialize(new { photos = new { cover = Key(EventId), gallery = new[] { Key(EventId), Key(EventId) } } }));
        Assert.Null(Photos.Validate(config, EventId));
        Assert.Null(Templates.Validate("clasica", config, EventId));
    }

    [Fact]
    public void Validate_accepts_null_cover() =>
        Assert.Null(Photos.Validate(Json("""{"photos":{"cover":null,"gallery":[]}}"""), EventId));

    [Fact]
    public void Validate_rejects_photo_from_another_event()
    {
        var config = Json(JsonSerializer.Serialize(new { photos = new { cover = Key(OtherEvent) } }));
        Assert.NotNull(Templates.Validate("clasica", config, EventId));
    }

    [Fact]
    public void Validate_rejects_external_url_in_gallery() =>
        Assert.NotNull(Photos.Validate(Json("""{"photos":{"gallery":["https://evil.example/x.webp"]}}"""), EventId));

    [Fact]
    public void Validate_rejects_too_many_gallery_photos()
    {
        var gallery = Enumerable.Range(0, Photos.MaxGallery + 1).Select(_ => Key(EventId)).ToArray();
        Assert.NotNull(Photos.Validate(Json(JsonSerializer.Serialize(new { photos = new { gallery } })), EventId));
    }

    [Fact]
    public void Orphans_keeps_referenced_and_recent_uploads()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
        var used = Key(EventId);
        var replacedLongAgo = Key(EventId);
        var justUploaded = Key(EventId);
        var config = Json(JsonSerializer.Serialize(new { photos = new { cover = used } }));

        var orphans = Photos.Orphans(
            [(used, now.AddDays(-3)), (replacedLongAgo, now.AddDays(-2)), (justUploaded, now.AddMinutes(-5))],
            Photos.ReferencedKeys(config), now).ToList();

        Assert.Equal([replacedLongAgo], orphans);
    }

    // Ejemplos oficiales de AWS ("Signature Version 4 examples" de S3): si la firma coincide, R2 la acepta.
    private static readonly S3Signer.Credentials AwsExample =
        new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");
    private static readonly DateTimeOffset AwsExampleDate = DateTimeOffset.Parse("2013-05-24T00:00:00Z");

    [Fact]
    public void Presign_matches_aws_documented_example()
    {
        var url = S3Signer.Presign(AwsExample, "GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
            new Dictionary<string, string>(), TimeSpan.FromSeconds(86400), AwsExampleDate);

        Assert.Contains("X-Amz-SignedHeaders=host", url.Query);
        Assert.Contains("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", url.Query);
    }

    [Theory]
    [InlineData("https://examplebucket.s3.amazonaws.com/?lifecycle", "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
    [InlineData("https://examplebucket.s3.amazonaws.com/?max-keys=2&prefix=J", "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7")]
    public void AuthHeaders_match_aws_documented_examples(string url, string expectedSignature)
    {
        var headers = S3Signer.AuthHeaders(AwsExample, "GET", new Uri(url), AwsExampleDate, S3Signer.EmptyPayloadHash);

        Assert.Equal("20130524T000000Z", headers["x-amz-date"]);
        Assert.EndsWith(
            $"SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={expectedSignature}",
            headers["Authorization"]);
    }

    [Fact]
    public void Presigned_upload_signs_type_and_size()
    {
        var url = S3Signer.Presign(AwsExample with { Region = "auto" }, "PUT",
            new Uri("https://cuenta.r2.cloudflarestorage.com/lacre-fotos/events/abc/foto.webp"),
            new Dictionary<string, string> { ["content-type"] = "image/webp", ["content-length"] = "1234" },
            TimeSpan.FromMinutes(10), AwsExampleDate);

        Assert.Contains("X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost", url.Query);
        Assert.Contains("X-Amz-Expires=600", url.Query);
        Assert.Equal("/lacre-fotos/events/abc/foto.webp", url.AbsolutePath);
    }
}
