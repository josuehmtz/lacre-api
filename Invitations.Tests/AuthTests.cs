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
}
