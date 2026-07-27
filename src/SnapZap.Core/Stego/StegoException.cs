namespace SnapZap.Core.Stego;

/// <summary>Thrown for an unreadable/non-image carrier, a missing SNZC footer on extract, a
/// corrupted footer/payload, and an unrelated pre-existing file at the output path.
/// Wrong-passphrase/tampered-data failures are NOT wrapped here — they surface as the BCL
/// <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/> from
/// <c>AesGcm.Decrypt</c>, which callers catch separately.</summary>
public sealed class StegoException(string message) : Exception(message);
