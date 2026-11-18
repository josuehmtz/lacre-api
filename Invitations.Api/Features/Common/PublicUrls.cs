namespace Invitations.Api.Features.Common;

public static class PublicUrls
{
    /// <summary>
    /// Host público sin el puerto cuando es el estándar (443 en https, 80 en http).
    /// Google compara el redirect_uri letra por letra: "sitio.app:443" no es igual a "sitio.app".
    /// </summary>
    public static HostString HostFor(Uri publicUrl) =>
        publicUrl.IsDefaultPort ? new HostString(publicUrl.Host) : new HostString(publicUrl.Host, publicUrl.Port);
}
