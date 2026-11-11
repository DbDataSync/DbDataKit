# Locale data: what is checked, and what a person must still judge

The suite holds locale data by hand in one place, the view formatter (`DbDataKit.Views` `ViewFormat` in .NET, `format.ts` in `@dbdatakit/visuals`; plan 32 DBR40, DBR46; `docs/views.md`, "Locale").
Everything else that looks like locale work (`toLocaleString()` in the screens, the `en-GB` clock in `dashboard/refresh.ts`, `.ToString(CultureInfo.InvariantCulture)` in the engines) asks the platform and holds no table of its own.

## The tables

| Table | Where | Locales | Fields |
| --- | --- | --- | --- |
| `locales.json`, read by both languages (the .NET project embeds the same file) | `Shared/web/visuals/src/views/locales.json` | `en de fr es it pt nl` | `decimal`, `group` (one character; the French group is U+202F), `months` (12 names), `short` (12 abbreviations) |
| Currency symbols | `CurrencySymbol` in `ViewFormat.cs`, the twin in `format.ts` | English only (a symbol is not localised) | `USD EUR GBP JPY CNY INR KRW` |
| Compact suffixes | `Compact` in `ViewFormat.cs`, the twin in `format.ts` | English only | thousand `K`, million `M`, billion `B` |

The date presets and tokens (`yyyy-MM-dd`, `MMM yyyy`) are the formatter's own closed vocabulary, not locale data.

## What the tests check

`Shared/conformance/locale/cldr-snapshot.json` is a small snapshot of the Unicode CLDR (JSON, release in `cldr-snapshot.pin`, licence Unicode-3.0) holding exactly those fields for exactly those locales:
the separators of the Latin numbering system, the **stand-alone** month names (wide and abbreviated), the English currency symbols and the English compact suffixes.
`scripts/fetch_cldr_snapshot.py --write` regenerates it from the two npm packages `cldr-numbers-full` and `cldr-dates-full` (standard library only; the download is checked against the integrity in the pin, the file against its SHA-256;
`--verify` redoes that without writing; `scripts/tests/test_cldr_snapshot.py` checks the pin offline).

`LocaleDataTests` (`Shared/tests/DbDataKit.Views.Tests`) and `localeData.test.ts` (`Shared/web/visuals/src/views`) run the same checks, each failure naming the locale and the field:
the snapshot is the pinned one; `locales.json` has every locale and field of the snapshot and no other; every field is well formed (not empty, normal form C, no control character, no space at an end, a separator is one character and never a
breaking space, twelve distinct names); every field equals the snapshot (separators, months, abbreviations, the currency symbol and the compact suffix the formatter really writes); and, where ICU is present, the separators and
the stand-alone month names equal what ICU says (.NET `CultureInfo`, Node `Intl`).

A **deliberate** difference is a row in `Shared/conformance/locale/allowed-deviations.json`: locale, field, our value, the reference value and a reason. A row that matches nothing, lacks a reason or holds an out-of-date value fails.
One row today: `CNY` is written `¥` (the formatter and `docs/semantic/presentation.md`), CLDR's English writes `CN¥`.

**What the tests cannot cover:** the ICU comparison skips on a machine with `InvariantGlobalization` or without ICU data, and compares only separators and wide month names because abbreviations, the contexts
and the symbols differ between ICU versions (so it is a cross-check of the snapshot's transcription, not a second authority); the snapshot is one CLDR release and CLDR itself changes (es `sept` and the pt full stops are changes of recent releases);
whether the formatter *uses* the data correctly (that is the goldens of `Shared/conformance/views/format/cases.json`); locales beyond the seven; the chart axis, which draws d3 strings with English separators (not localised, see `docs/views.md`).

## Fixes the check made (2026-11-11, against CLDR 49)

`es` September abbreviation `sep` became `sept`; the `pt` abbreviations `jan fev mar abr mai jun jul ago set out nov dez` gained their full stop (`jan.`, `fev.`, ...). Nothing else differed. No golden held these names.

## The judgement list: what a person must still review

CLDR is a reference, not a verdict. Machines cannot say whether these read well to the people who will see them.

1. **Abbreviations in a chart or a column** (`short`). `pt` now reads `jan.` and `mai.` and `es` `sept`, as CLDR 49 has them (the table had `jan`, `mai` and `sep`, which other style guides may prefer); `fr` `mars`/`mai`/`juin`/`août` are whole words by design. A native reader should say whether the stops belong in an axis label.
2. **Stand-alone or format context.** The table has one name per month. It is compared with CLDR's *stand-alone* context, right for a month shown alone (an axis, a column). In a pattern with a day, German CLDR uses another abbreviation in the *format* context (`Jan.`, `März`, `Sept.` against the stand-alone `Jan`, `Mär`, `Sep`); `dd MMM yyyy` in `de` therefore reads `15 Mär 2026` where a German writer may expect `15. März 2026`. The other six locales have the same text in both contexts. Decide whether `de` needs a second list.
3. **Currency symbol of `CNY`** (the allow-listed row): `¥` as in `presentation.md`, or `CN¥` as CLDR writes it. Also whether the symbol should ever move after the number (CLDR 49 puts it after the number, `1.234,50 €`, in `de fr es it` and before it, `€ 1.234,50`, in `pt` and `nl`; today it stays directly in front everywhere, documented as not localised).
4. **Compact numbers** in `de`, `fr`, `es`, `it`, `pt`, `nl`: the suffixes are the English `K M B` with a localised decimal. CLDR 49 writes, for thousand, million, billion: `de` (no thousand suffix) `Mio.` `Mrd.`; `fr` `k` `M` `Md`; `es` `mil` `M` (and `0000 M` for a billion); `it` `K` `Mln` `Mld`; `pt` `mil` `mi` `bi`; `nl` `K` `mln.` `mld.`, each after a no-break space. These are not in the snapshot or the check; a person must decide whether English suffixes are acceptable in localised output.
5. **The French group separator** U+202F (narrow no-break space) is what CLDR and ICU write today and what French typography prescribes, but some fonts and older PDF or email clients draw it as a missing glyph; U+00A0 is the fallback (CLDR used it before release 34). Check an export and a mail in the real clients.
6. **Grouping of four-digit numbers** (`es`, `it`): CLDR's `minimumGroupingDigits` is 2 for `es` and `it` (`1234`, not `1.234`; groups start at five digits); the formatter groups from four digits in every locale. Decide whether `1.234` is acceptable in `es` and `it`.
7. **Where sources disagree.** CLDR, ICU and local style guides differ (Spanish `sept`/`sep`, Portuguese stops), and `pt` means Brazilian Portuguese in CLDR (`pt-PT` is a locale of its own there, not compared). The table says `pt` once; if European Portuguese is wanted it is a new locale.
