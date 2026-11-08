namespace Adding2013VersionCards;

public sealed record ImportOptions(
    string SourceDirectory,
    string GameDirectory,
    string? ProfilePath,
    bool SkipProfile,
    bool ValidateOnly,
    int StartId,
    byte Quantity)
{
    public static ImportOptions Parse(IReadOnlyList<string> args)
    {
        string? source = null;
        string? game = null;
        string? profile = null;
        var skipProfile = false;
        var validateOnly = false;
        var startId = 1328;
        byte quantity = 4;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            string Value()
            {
                if (++index >= args.Count)
                    throw new ArgumentException($"Missing value after {arg}.");
                return args[index];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--source": source = Value(); break;
                case "--game": game = Value(); break;
                case "--profile": profile = Value(); break;
                case "--skip-profile": skipProfile = true; break;
                case "--validate-only": validateOnly = true; break;
                case "--start-id": startId = int.Parse(Value()); break;
                case "--quantity": quantity = byte.Parse(Value()); break;
                default: throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("--source is required.");
        if (string.IsNullOrWhiteSpace(game))
            throw new ArgumentException("--game is required.");
        if (startId < 0 || startId >= ProfileCodec.CardCapacity)
            throw new ArgumentOutOfRangeException(nameof(startId), "The start ID must be within 0..2047.");
        if (quantity is 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be within 1..7.");

        return new ImportOptions(
            Path.GetFullPath(source),
            Path.GetFullPath(game),
            string.IsNullOrWhiteSpace(profile) ? null : Path.GetFullPath(profile),
            skipProfile,
            validateOnly,
            startId,
            quantity);
    }
}
