namespace SnapZap.Core.Stego;

public sealed record HideResult(string OutputPath, long PayloadBytes, bool Encrypted);
