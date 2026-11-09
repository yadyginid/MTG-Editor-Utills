# Adding Magic 2013 cards to Magic Duels

This command-line tool imports extracted Magic 2013 cards into the final Magic
Duels release for both deck building and actual duels.

It performs all required layers of the import:

1. Copies the original `CARD_V2` XML files and TDX illustrations.
2. Converts every complete `CARD_V2` definition, including abilities, targets,
   filters, costs, triggers, and executable actions, into a standalone compiled
   Lua 5.1 `.lol` module used by the duel engine.
3. Adds full card objects to the compiled card-definition bundle used by the
   collection and deck builder.
4. Creates `CardPools/M13_IMPORTED_CARD_POOL.lol` with explicit IDs starting at
   `1328`. This registration file is required for the cards to appear in the
   collection.
5. Rebuilds and installs `DATA_008.ZED` after verifying both the registration
   and all standalone duel modules.
6. Patches the local Steam profile unlock table and recalculates `hash.file`.
7. Creates timestamped backups of both the archive and profile before replacing
   anything.

No Magic 2013 cards, artwork, profiles, or game archives are stored in this
repository.

## Requirements

- The final Windows release of Magic Duels.
- .NET 10 SDK.
- An extracted Magic 2013 source folder in either layout:

```text
Source/
  Cards/*.xml
  Illustrations/*.tdx
```

or:

```text
Source/Data_All_Platforms/
  Cards/*.xml
  Art_Assets/Illustrations/*.tdx
```

Close Magic Duels and `MTG-Editor-Utills` before starting an import.

To validate the source and compile all standalone duel modules plus the card
registration without changing game or profile files, add `--validate-only`.

## Run

From this directory:

```powershell
dotnet run -c Release -- --source "D:\Extracted Magic 2013" --game "I:\SteamLibrary\steamapps\common\Magic Duels"
```

The importer attempts to locate the most recently modified Steam profile under
`userdata/*/316010/remote`. To select it explicitly:

```powershell
dotnet run -c Release -- --source "D:\Extracted Magic 2013" --game "I:\SteamLibrary\steamapps\common\Magic Duels" --profile "C:\Program Files (x86)\Steam\userdata\123456\316010\remote\123456.profile"
```

`Run-Importer.cmd` accepts the source directory, game directory, and optional
profile path as its three positional arguments.

## ID limit

The final official card-pool registration ends at ID `1327`, so the importer
starts at `1328`. The profile stores only 2048 IDs. Consequently, IDs
`1328..2047` can appear as owned cards in the normal deck builder. Later cards
remain registered in the archive but require card-set rotation or direct deck
injection.

Tokens (`RARITY=T`) are copied as definitions so other cards may create them,
but they are not registered as collectible deck-building cards.

## Backups and working files

The importer writes only under the selected game directory:

```text
UrukCardEditorData/
  Backups/
  Backups/Profiles/
  Sessions/Adding2013VersionCards-YYYYMMDD-HHMMSS/
```

Every session contains the generated registration source, rebuilt archive, and
an `import-manifest.txt` file describing the operation.

Use this only with a local/offline profile. Steam Cloud may ask which profile
copy to keep after a local modification.
