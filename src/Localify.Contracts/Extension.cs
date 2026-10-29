namespace Localify.Contracts;

/// <summary>Clé d'API d'une extension de navigateur (le secret n'est montré qu'à la création).</summary>
public sealed record ExtensionKeyDto(Guid Id, string Name, string Prefix, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record CreateExtensionKeyRequest(string Name);

public sealed record CreatedExtensionKeyDto(ExtensionKeyDto Key, string Secret);

/// <summary>Ce que Localify sait d'une vidéo (Status null = inconnue) et, si une adresse est donnée, si elle est suivie.</summary>
public sealed record ExtensionStatusDto(string? VideoId, string? Status, bool NeedsReview, string? Error, bool? Subscribed);
