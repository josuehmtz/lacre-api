using Invitations.Api.Features.Auth;

namespace Invitations.Tests;

public class AuthTests
{
    [Theory]
    [InlineData("/app/events/123", "/app/events/123")]
    [InlineData("/app", "/app")]
    [InlineData(null, "/app")]
    [InlineData("", "/app")]
    [InlineData("https://sitio-ajeno.com", "/app")]
    [InlineData("//sitio-ajeno.com", "/app")]
    [InlineData("/\\sitio-ajeno.com", "/app")]
    public void SafeReturnUrl_only_allows_internal_paths(string? input, string expected) =>
        Assert.Equal(expected, AuthEndpoints.SafeReturnUrl(input));

    [Theory]
    [InlineData("https://lacremx.netlify.app", "lacremx.netlify.app")]
    [InlineData("https://lacremx.netlify.app/", "lacremx.netlify.app")]
    [InlineData("https://lacremx.netlify.app:443", "lacremx.netlify.app")]
    [InlineData("http://localhost:5173", "localhost:5173")]
    [InlineData("http://ejemplo.mx", "ejemplo.mx")]
    public void Public_host_omits_default_port(string url, string expected) =>
        Assert.Equal(expected, Invitations.Api.Features.Common.PublicUrls.HostFor(new Uri(url)).Value);
}
