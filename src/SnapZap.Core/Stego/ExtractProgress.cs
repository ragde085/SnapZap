namespace SnapZap.Core.Stego;

/// <summary>The only phase <see cref="StegoEngine.ExtractAsync"/> itself reports. The dialog
/// narrates its own later decrypt/unzip stages directly (they're dialog-owned, not
/// engine-reported), rather than through this enum — don't add phases here that nothing ever
/// actually reports.</summary>
public enum ExtractPhase { Reading }

public sealed record ExtractProgress(ExtractPhase Phase, long BytesDone, long BytesTotal);
