namespace SnapZap.Core.Stego;

/// <summary>The only phase <see cref="StegoEngine.EmbedAsync"/> itself reports — concatenation
/// folds embedding into the write, so there's no separate step to name. The dialog narrates its
/// own earlier zip/encrypt stages directly (they're dialog-owned, not engine-reported), rather
/// than through this enum — don't add phases here that nothing ever actually reports.</summary>
public enum HidePhase { Writing }

public sealed record HideProgress(HidePhase Phase, long BytesDone, long BytesTotal);
