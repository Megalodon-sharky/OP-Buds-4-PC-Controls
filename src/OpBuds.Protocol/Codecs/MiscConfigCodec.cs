using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>Outcome of a misc-config read, including whether the reply was truncated.</summary>
public sealed record MiscConfigResult(
    IReadOnlyList<MiscFeature> Features,
    IReadOnlyList<byte> MissingIds,
    bool SuspectedTruncation);

/// <summary>
/// The feature-switch list, <c>0x010D</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This handler truncates silently and lies about it.</b> The reply is capped at 32
/// payload bytes — exactly 15 <c>[id][value]</c> pairs — entries come back in ascending id
/// order, and the count field reports the <i>truncated</i> count as though it were the real
/// one. There is no error status and no flag. Requesting all seventeen supported ids returns
/// a well-formed, entirely plausible reply that has quietly lost the highest two.
/// </para>
/// <para>
/// That behaviour was mistaken for a dynamic capability list for an entire session, and six
/// hypotheses about the device changing state were tested before anyone suspected the
/// request. The list is static. Page requests at <see cref="MaxIdsPerReply"/> and it is safe
/// to cache for the lifetime of a connection.
/// </para>
/// </remarks>
public static class MiscConfigCodec
{
    /// <summary>Entries that fit under the 32-byte reply cap.</summary>
    public const int MaxIdsPerReply = 15;

    /// <summary>The reply payload cap, in bytes.</summary>
    public const int ReplyPayloadCap = 32;

    private const int RecordWidth = 2;

    public const byte BassWaveFeatureId = 0x1D;

    /// <summary>
    /// The seventeen ids this hardware supports, ascending.
    /// </summary>
    /// <remarks>
    /// Gadgetbridge's <c>ldac</c> (<c>0x01</c>) and <c>multipoint</c> (<c>0x03</c>) are
    /// absent on this model. <c>0x1D</c> is BassWave; the rest are unnamed.
    /// </remarks>
    public static readonly byte[] KnownIds =
    [
        0x02, 0x04, 0x05, 0x06, 0x0B, 0x0C, 0x0D, 0x11, 0x12,
        0x14, 0x16, 0x17, 0x18, 0x19, 0x1B, 0x1D, 0x1E,
    ];

    /// <summary>Splits ids into requests small enough to survive the reply cap.</summary>
    public static IReadOnlyList<byte[]> Pages(IReadOnlyList<byte>? ids = null)
    {
        ids ??= KnownIds;

        var pages = new List<byte[]>();
        for (var i = 0; i < ids.Count; i += MaxIdsPerReply)
        {
            pages.Add([.. ids.Skip(i).Take(MaxIdsPerReply)]);
        }

        return pages;
    }

    /// <summary>Request payload for one page. Throws if the page cannot fit in a reply.</summary>
    public static byte[] BuildRequest(params byte[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Length > MaxIdsPerReply)
        {
            throw new ArgumentException(
                $"{ids.Length} ids exceeds the {MaxIdsPerReply}-entry reply cap; the device " +
                "would silently drop the highest ones. Use Pages().",
                nameof(ids));
        }

        return CountPrefixed.BuildRequest(ids);
    }

    /// <summary>
    /// Parses a <c>0x810D</c> reply against the ids that were requested.
    /// </summary>
    /// <remarks>
    /// Comparing returned ids against requested ids is the <i>only</i> way to detect the
    /// truncation, so <paramref name="requestedIds"/> is required rather than optional.
    /// </remarks>
    public static bool TryParse(
        ReadOnlyMemory<byte> payload,
        IReadOnlyList<byte> requestedIds,
        out MiscConfigResult result)
    {
        ArgumentNullException.ThrowIfNull(requestedIds);
        result = new MiscConfigResult([], [], false);

        if (CountPrefixed.TryParse(payload, RecordWidth, out var records) != ParseStatus.Ok)
        {
            return false;
        }

        var features = records
            .Select(record => new MiscFeature(record.Id, record.Value))
            .ToArray();

        var returned = features.Select(feature => feature.Id).ToHashSet();
        var missing = requestedIds.Where(id => !returned.Contains(id)).ToArray();

        // Exactly 15 entries in a full 32-byte payload, with something missing, is the
        // signature of the cap rather than of genuinely unsupported ids.
        var truncated = missing.Length > 0
            && features.Length == MaxIdsPerReply
            && payload.Length == ReplyPayloadCap;

        result = new MiscConfigResult(features, missing, truncated);
        return true;
    }
}
