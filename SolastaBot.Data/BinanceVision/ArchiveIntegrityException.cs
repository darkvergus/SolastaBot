namespace SolastaBot.Data.BinanceVision;

/// <summary>Raised when a downloaded archive does not match its published SHA-256.</summary>
public sealed class ArchiveIntegrityException(string message) : Exception(message);