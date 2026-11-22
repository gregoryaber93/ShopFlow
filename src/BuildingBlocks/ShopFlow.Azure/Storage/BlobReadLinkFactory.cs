using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace ShopFlow.Azure.Storage;

public sealed class BlobLinkOptions
{
    public const string SectionName = "BlobLinks";

    /// <summary>
    /// Public address customers download files from, e.g. <c>https://files.shopflow.example</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: the documents Storage account has public access disabled (private endpoint only), so a link with
    /// the account name (<c>stshopflowdocsprodweu.blob.core.windows.net</c>) does not work outside the VNet.
    /// Files are served through Front Door Premium with a Private Link origin to the Storage account: Front Door
    /// connects to the account privately and the client only sees the shop's domain. The SAS signature covers the
    /// resource path (account/container/blob), not the host name, so the link stays valid after the host is
    /// replaced with the Front Door address. When the value is empty, the link points at the account directly
    /// (development environments).
    /// </remarks>
    public Uri? PublicBaseUrl { get; set; }
}

/// <summary>
/// Short-lived read-only links signed with a user delegation key (user delegation SAS).
/// </summary>
/// <remarks>
/// AZURE: a classic SAS is signed with the account key, and our accounts have keys disabled
/// (<c>allowSharedKeyAccess: false</c>). A user delegation SAS is signed with a key the application gets
/// from Storage based on its Entra ID identity (the Storage Blob Delegator role on the account and read
/// permission on the blob). Benefits:
/// <list type="bullet">
/// <item>There is no key in configuration that could leak.</item>
/// <item>A link is valid at most as long as the delegation key (max 7 days), and only as long as the identity
/// has permissions. Removing the role or revoking delegation keys invalidates all links.</item>
/// <item>Storage logs show whose identity issued the link.</item>
/// </list>
/// The delegation key is kept in memory and reused while it remains valid longer than the issued link:
/// every key request is a call to Storage, and we issue a link for every e-mail.
/// </remarks>
public sealed class BlobReadLinkFactory(BlobServiceClient blobs, IOptions<BlobLinkOptions> options, TimeProvider timeProvider)
{
    /// <summary>The longest link lifetime the factory will issue.</summary>
    /// <remarks>
    /// AZURE: Storage issues a delegation key valid for at most 7 days from the request, and a link cannot live
    /// longer than the key it was signed with (see <see cref="BuildReadLink"/>). A day of headroom allows one key
    /// to be used for many links instead of fetching a new one each time.
    /// </remarks>
    public static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromDays(6);

    private static readonly TimeSpan MaxKeyLifetime = TimeSpan.FromDays(7) - TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeyReuseWindow = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _key;

    public async Task<Uri> CreateReadLinkAsync(string containerName, string blobName, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lifetime, MaxLinkLifetime);

        var now = timeProvider.GetUtcNow();
        var key = await GetKeyAsync(now, lifetime, cancellationToken).ConfigureAwait(false);
        return BuildReadLink(blobs.Uri, blobs.AccountName, containerName, blobName, key, now, lifetime, options.Value.PublicBaseUrl);
    }

    public static Uri BuildReadLink(
        Uri serviceUri,
        string accountName,
        string containerName,
        string blobName,
        UserDelegationKey key,
        DateTimeOffset now,
        TimeSpan lifetime,
        Uri? publicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(key);

        // AZURE: the signature includes the delegation key's expiry date (the ske parameter). After that date Storage
        // rejects the link with 403 even if the link's own se is later. Such a link would work for less time than the
        // e-mail promises, so instead of issuing it we report a programming error.
        if (now + lifetime > key.SignedExpiresOn)
        {
            throw new ArgumentException(
                $"A link valid until {now + lifetime:O} would outlive the user delegation key that expires at {key.SignedExpiresOn:O}.",
                nameof(lifetime));
        }

        var sas = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",

            // A few minutes of headroom for clock skew between us and Storage.
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = now + lifetime,
            Protocol = SasProtocol.Https,
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        var builder = new BlobUriBuilder(serviceUri)
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Sas = sas.ToSasQueryParameters(key, accountName),
        };

        var link = builder.ToUri();
        return publicBaseUrl is null
            ? link
            : new UriBuilder(link) { Scheme = publicBaseUrl.Scheme, Host = publicBaseUrl.Host, Port = publicBaseUrl.Port }.Uri;
    }

    private async Task<UserDelegationKey> GetKeyAsync(DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var requiredUntil = now + lifetime;
        if (_key is { } cached && cached.SignedExpiresOn >= requiredUntil)
        {
            return cached;
        }

        await _keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_key is { } fresh && fresh.SignedExpiresOn >= requiredUntil)
            {
                return fresh;
            }

            // WHY: the key lives longer than the link needs (by the reuse window), so that subsequent links over the next
            // day use the same key. A 15-minute link (label) gets a key valid for a day, a 3-day link (invoice e-mail)
            // a key valid for 4 days.
            var keyLifetime = lifetime + KeyReuseWindow < MaxKeyLifetime ? lifetime + KeyReuseWindow : MaxKeyLifetime;
            var response = await blobs.GetUserDelegationKeyAsync(now.AddMinutes(-5), now + keyLifetime, cancellationToken).ConfigureAwait(false);
            _key = response.Value;
            return _key;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}
