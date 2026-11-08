using Adding2013VersionCards;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
{
    PrintHelp();
    return 0;
}

try
{
    var options = ImportOptions.Parse(args);
    if (options.ValidateOnly)
    {
        var validation = Magic2013Importer.Validate(options, Console.WriteLine);
        Console.WriteLine($"Validation passed: {validation.TotalCards} definitions, {validation.CollectibleCards} collectible cards, IDs {validation.FirstId}..{validation.LastId}.");
        return 0;
    }
    var result = Magic2013Importer.Run(options, Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine("Magic 2013 import completed.");
    Console.WriteLine($"Registered cards: {result.RegisteredCards}");
    Console.WriteLine($"Registered IDs: {result.FirstId}..{result.LastId}");
    Console.WriteLine($"Profile-visible IDs: {result.FirstId}..{result.LastUnlockableId}");
    Console.WriteLine($"Installed archive: {result.InstalledArchive}");
    Console.WriteLine($"Archive backup: {result.ArchiveBackup ?? "not required"}");
    Console.WriteLine($"Session: {result.SessionDirectory}");
    if (result.ProfileResult is not null)
    {
        Console.WriteLine($"Profile: {result.ProfileResult.ProfilePath}");
        Console.WriteLine($"Profile backup: {result.ProfileResult.ProfileBackup}");
    }
    else
    {
        Console.WriteLine("Profile was not patched. Use --profile with the active Steam profile path.");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("Adding2013VersionCards");
    Console.WriteLine();
    Console.WriteLine("Imports extracted Magic 2013 CARD_V2 XML files and TDX artwork into Magic Duels.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Adding2013VersionCards --source <folder> --game <Magic Duels folder> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --profile <file>    Active Steam .profile file. Auto-detected when omitted.");
    Console.WriteLine("  --skip-profile      Build/install the archive without changing a profile.");
    Console.WriteLine("  --validate-only     Validate source and Lua compilation without changing files.");
    Console.WriteLine("  --start-id <id>     First custom card ID. Default: 1328.");
    Console.WriteLine("  --quantity <1..7>   Number owned in the profile. Default: 4.");
    Console.WriteLine();
    Console.WriteLine("The source folder may contain Cards + Illustrations directly, or the original");
    Console.WriteLine("Data_All_Platforms/Cards + Data_All_Platforms/Art_Assets/Illustrations layout.");
}
