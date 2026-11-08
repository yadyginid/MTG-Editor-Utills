# MTG-Editor-Utills

Card editor for a local Magic Duels installation.

## Run

Run `Start-MTG-Editor-Utills.cmd` or `dist-fixed/MTG-Editor-Utills.exe`.

On first launch, click **Browse…** next to **Game directory** and select the Magic Duels folder containing `DATA_000.ZED` and `DATA_008.ZED`. The most recently selected path is stored in `%LOCALAPPDATA%\MTG-Editor-Utills\settings.json`.

## Workflow

1. Search for a card in the list and select it.
2. Click **Edit selected card**.
3. Change the title, mana cost, power, toughness, artist, or image.
4. Open **Abilities and triggers** to edit the Lua/XML ability block.
5. Use the template buttons to add either of the supported death triggers.
6. Click **Save to working copy**. More cards can be edited in the same session.
7. Close Magic Duels and click **Install to game**.

The installer rebuilds `DATA_008.ZED`, verifies the file count, and creates a timestamped backup in `UrukCardEditorData/Backups` before replacement.

## Current scope

- Indexes standalone `.xml` and `.lol` card records from `DATA_000.ZED` through `DATA_008.ZED`.
- Shows a lazily loaded original-art thumbnail next to every visible card name in the list.
- Decompiles official Lua 5.1 card bytecode when a card is opened.
- Compiles every modified `.lol` back to 32-bit Lua 5.1 bytecode with the Magic Duels `0x5F` format marker.
- Reverses `unluac.net`'s spurious `\\00` prefixes and preserves the original non-ASCII string bytes, so Russian and other localized card text remains registered by `T(...)`.
- Updates every containing aggregate card record, including booster sets and `MO_PLAYER_POOL.lol`.
- Also writes a compiled standalone card override; deck building and active matches therefore use the same title, cost, stats, artwork, and abilities.
- Converts PNG/JPEG/BMP/TIFF artwork to the game's 512×376 DXT1 `.tdx` format.
- Validates edited Lua or XML before staging.
- Provides ready-made templates for death damage and death −1/−1-counter triggers.
- Other abilities can be edited directly in the raw Lua/XML ability editor.

The editor does not modify Steam API files.

## Magic 2013 card importer

The reproducible importer used to add the Magic 2013 card set is located in
[`Adding2013Version Cards`](./Adding2013Version%20Cards). It creates a dedicated
card-pool registration file, preserves card XML and artwork, patches the local
2048-slot profile unlock table, and makes timestamped backups before installing
the rebuilt `DATA_008.ZED`.
