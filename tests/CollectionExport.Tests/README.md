# Collection export — test bench

Runs the collection export code (`Plugin/Features/CollectionExport`) outside the
game. Those files contain no Unity and no Arena types on purpose, so the same
source is compiled here. No NuGet packages: Newtonsoft.Json is taken from the
Arena install, like the plugin does.

From the source root (PowerShell):

```
dotnet run --project tests\CollectionExport.Tests
```

Exit code 0 = every check passed. Every check is printed, passed or not.

## What is covered

- CSV escaping (commas, quotes, line breaks, leading spaces, non-ASCII), no BOM
- Arena name cleanup: `///` split separator, TextMeshPro tags, Alchemy "A-"
- Scryfall page parsing, error pages rejected, index file round trip,
  truncated index rejected
- empty collection, single card, several copies, same card in different sets,
  same set with different collector numbers, double-faced / split / adventure
  cards, names with commas and quotes, digital-only and Alchemy cards, cards
  missing from Scryfall, a wrong Scryfall `arena_id`, an `arena_id` shared by two
  printings, tokens, unknown cards, zero and negative counts, duplicate entries
- for every format: exact header, positive integer quantities, same number of
  cells on every line, no duplicate lines, and **no lost copies**
  (copies read = copies in the file + copies the format can't hold + skipped)
- output does not depend on input order
- 21 000 printings / 51 000 copies

`Fixtures/` holds a few real Scryfall records (trimmed to the fields we read)
from the search of 2026-10-14.

## Optional: real data

1. All of Arena's card database against all of Scryfall's Arena printings.
   Needs the Scryfall search pages saved as `page000.json`… plus `sets.json`,
   and Arena's card table dumped to a TSV
   (`GrpId, title, set, number, IsToken, IsDigitalOnly, IsRebalanced, IsPrimaryCard`):

   ```
   dotnet run --project tests\CollectionExport.Tests -- -real <pages folder> <arena-cards.tsv>
   ```

2. The real downloader against the real Scryfall (about two minutes):

   ```
   dotnet run --project tests\CollectionExport.Tests -- -download <output .tsv>
   ```

Neither file is committed: one is Arena's data, the other is 110 MB of JSON.
