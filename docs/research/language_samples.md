# Label / receipt printer language samples

Provenance: the samples below were written to be **consistent with** the official programming manuals (command names, parameter order), not copied verbatim, and none was run on hardware in this session. Items I could not re-check against a manual in this session are marked **[unverified]**. `<ESC>`=0x1B, `<STX>`=0x02, `<ETX>`=0x03, `<GS>`=0x1D, `<RS>`=0x1E, `<LF>`=0x0A, `<CR>`=0x0D, `<NUL>`=0x00.

The bridge always sends UTF-8. Summary of what that means per language:

| Language | UTF-8 accepted? | What to send first |
|---|---|---|
| ZPL | Yes, after `^CI28` | `^CI28` inside every format |
| EPL2 | No (single-byte code pages) | `I8,A,001` and transcode to CP1252 |
| TSPL | Firmware dependent (`CODEPAGE UTF-8`) | `CODEPAGE UTF-8`, else transcode |
| CPCL | Firmware dependent (`ENCODING UTF-8`) | `ENCODING UTF-8` or `COUNTRY` + transcode |
| DPL / PPLA | No by default (single-byte symbol sets) | transcode |
| ESC/POS | No on classic models | `ESC t n` + transcode |
| SBPL | Model dependent | transcode unless verified |
| EZPL | Model dependent | transcode unless verified |
| PPLB | No (same as EPL2) | `I` command + transcode |
| TPCL | Printer setting (not per job) | transcode unless printer set to UTF-8 |
| Fingerprint/DP | Yes, `NASC 8` | `NASC 8` |
| IPL | No by default | transcode |
| ESC/Label | Yes, `^CI28` | `^CI28` |
| StarPRNT | Yes on StarPRNT models, by command | transcode unless verified |

---

## 1. ZPL II (Zebra)

Source: https://docs.zebra.com/us/en/printers/software/zpl-pg/ (ZPL Programming Guide)

```
^XA
^CI28
^PW812
^LL406
^FO50,50^A0N,40,40^FDHello World^FS
^FO50,120^BY2^BCN,100,Y,N,N^FD12345678^FS
^PQ2
^XZ
```

- Copies: `^PQq,p,r,o` (q = total quantity). `^PQ2` prints 2.
- Signature: `^XA` ... `^XZ`; fields `^FO`/`^FD`/`^FS`; control commands start with `~`. Prefix characters can be remapped with `^CC`/`~CC`, but `^XA` is reliable in practice.
- Encoding: default is code page 850 (`^CI0`). `^CI28` = UTF-8, `^CI27` = Windows-1252. `^CI` persists until power-off or changed, so emit it in every job. Non-Latin glyphs still need a font that contains them (e.g. Swiss 721 on Link-OS; `^A@` with a downloaded TTF).

## 2. EPL2 (Eltron / Zebra page mode)

Source: https://www.zebra.com/content/dam/zebra_new_ia/en-us/manuals/printers/common/programming/epl2-pm-en.pdf (EPL2 Programming Guide; URL follows the same pattern as the verified CPCL URL but was not opened this session **[unverified URL]**)

```
<LF>
N
q812
Q406,24
I8,A,001
A50,50,0,3,1,1,N,"Hello World"
B50,120,0,1,2,6,100,B,"12345678"
P2
```

- Each line ends with LF (CR LF is accepted). Start with an empty line so `N` is seen as a fresh command.
- Copies: `P2` = 2 label sets; `P1,2` = 1 set, 2 copies of each.
- Signature: a line containing only `N`, followed by `A<x>,<y>,<rot>,<font>,...,"data"` / `B...` lines and a final `P<n>`. Quoted data strings.
- Encoding: single-byte only. `I8,A,001` = 8-bit data, Windows-1252, country 001; `I8,0,001` = DOS 437. No UTF-8 in classic EPL2: UTF-8 multi-byte sequences print as two or three wrong characters.

## 3. TSPL / TSPL2 (TSC; also most Chinese 4x6 printers, Brother FBPL)

Source: https://fs.tscprinters.com/system/files/31-0000001-00_tspl_tspl2_programming_3_0.pdf (TSC TSPL/TSPL2 Programming manual)

```
SIZE 100 mm, 50 mm
GAP 3 mm, 0 mm
DIRECTION 1
CODEPAGE UTF-8
CLS
TEXT 50,50,"3",0,1,1,"Hello World"
BARCODE 50,120,"128",100,1,0,2,2,"12345678"
PRINT 1,2
```

- Lines end with CR LF (LF alone is normally accepted).
- Copies: `PRINT m,n` = m label sets, n copies of each. `PRINT 1,2` prints 2 identical labels.
- Signature: `SIZE ` as the first command, then `GAP`/`BLINE`, `CLS`, and a terminating `PRINT `. Upper-case English keywords with comma-separated arguments.
- Encoding: `CODEPAGE n` selects the 8-bit table (437, 850, 852, 860, 863, 865, 1250, 1252, 1253, 1254, ...; default 850) or `CODEPAGE UTF-8` on TSPL2 firmware. UTF-8 is honoured by TrueType/scalable fonts; the internal bitmap fonts `"1"`..`"8"` are single-byte ASCII/Latin. Many low-cost clones ignore `CODEPAGE UTF-8` **[unverified per clone]** - transcode for those.

## 4. CPCL (Zebra/Comtec mobile; also HPRT, Bixolon, TSC Alpha emulations)

Source: https://www.zebra.com/content/dam/zebra_new_ia/en-us/manuals/printers/common/programming/cpcl-pm-en.pdf (CPCL Programming Manual)

```
! 0 200 200 406 2
ENCODING UTF-8
TEXT 4 0 50 50 Hello World
BARCODE 128 1 1 100 50 120 12345678
FORM
PRINT
```

- Lines end with CR LF.
- Copies: the 5th number of the header line `! <offset> <h-res> <v-res> <height> <qty>` (here 2).
- Signature: first line starts with `! ` followed by five integers; ends with `PRINT` (or `POST-FEED`/`FORM` + `PRINT`). Utility sessions start `! U1` or `! UTILITIES`.
- Encoding: `ENCODING ASCII|UTF-8|SJIS|BIG5|GBK...` and `COUNTRY <name|code page>` (e.g. `COUNTRY CP850`, `COUNTRY LATIN9`). `ENCODING UTF-8` needs a font that supports it (fonts on newer Link-OS mobiles); with resident font 4/7 only Latin renders **[details unverified]**.

## 5. DPL (Datamax-O'Neil / Honeywell)

Source: Honeywell "DPL Command Reference" (document sps-ppr-dpl-en-cr.pdf; official URL not located this session). A copy of the older programmer's manual: https://www.scribd.com/document/459163541/Manual-DPL-Programmer-Manual-pdf

```
<STX>L
D11
H10
121100000500050Hello World
1A3106001200050 12345678
Q0002
E
```

- Each record ends with CR.
- Record layout: rotation(1) font/barcode-id(1) width-mult(1) height-mult(1) height-or-subfont(3) row(4) column(4) data. `1A31060...` = Code 39 (`A`, human readable), wide 3, narrow 1, height 060.
- Copies: `Qnnnn` inside the label format (`Q0002`). `E` ends the format and prints.
- Signature: `<STX>L` (0x02 0x4C) to enter label formatting; system commands are `<STX>` + letter (e.g. `<STX>n`, `<STX>c`). The control prefix can be switched to alternate codes (`^` / 0x5E) by configuration.
- Encoding: single-byte; symbol set chosen with `<STX>y S<set>` / in-format `ySxx` (e.g. PC-850, PM). Unicode is only available with scalable/ILPC fonts **[unverified]**. Raw UTF-8 will misprint non-ASCII - transcode.

## 6. ESC/POS (Epson TM and compatibles)

Source: https://download4.epson.biz/sec_pubs/pos/reference_en/escpos/index.html (ESC/POS Command Reference); code pages: https://download4.epson.biz/sec_pubs/pos/reference_en/escpos/esc_lt.html and https://download4.epson.biz/sec_pubs/pos/reference_en/charcode/index.html

```
1B 40                      ESC @        initialise
1B 74 10                   ESC t 16     code table 16 = WPC1252
48 65 6C 6C 6F 20 57 6F 72 6C 64 0A     "Hello World" LF
1D 68 64                   GS h 100     barcode height
1D 77 02                   GS w 2       module width
1D 48 02                   GS H 2       HRI below
1D 6B 49 0A 7B 42 31 32 33 34 35 36 37 38   GS k 73 10 "{B12345678"  (CODE128, code set B)
0A
1D 56 42 00                GS V 66 0    feed and partial cut
```

- Copies: there is no quantity command. Send the whole job n times.
- Signature: starts with `1B 40` (ESC @); barcodes use `1D 6B` (GS k); cut is `1D 56`. Binary, no line-oriented keywords.
- Encoding: single-byte code tables selected with `ESC t n` (0 = PC437, 2 = PC850, 16 = WPC1252, 19 = PC858; valid n differs per model) plus `ESC R n` international set. Classic models (TM-T20/T82/T88V) have no UTF-8. Some recent models add a UTF-8 mode via `FS ( C` **[unverified which models]**. Multi-byte CJK needs `FS &` (Kanji mode) on CJK firmware.

## 7. SBPL (SATO)

Source: SATO "SBPL Programming Reference" / "Programming Reference for CLNX / CT4-LX" (official URL not located this session **[unverified]**)

```
<ESC>A
<ESC>V0050<ESC>H0050<ESC>L0202<ESC>XMHello World
<ESC>V0120<ESC>H0050<ESC>B103100*12345678*
<ESC>Q2
<ESC>Z
```

- `<ESC>A` starts and `<ESC>Z` ends a job. `V`/`H` = vertical/horizontal position in dots, `L` = expansion, `XM` = a resident font, `B103100` = barcode type 1 (Code 39), narrow bar 03, height 100. Code 128 uses `<ESC>BG`.
- Copies: `<ESC>Qn` (up to 6 digits).
- Signature: `1B 41` (ESC A) at the start and `1B 5A` (ESC Z) at the end; every command is ESC + upper-case letters. Some hosts wrap the job in `<STX>`...`<ETX>`. SATO's "non-standard code" mode replaces ESC with `^` and STX/ETX with `{`/`}`.
- Encoding: single-byte code page by default; Kanji via Shift-JIS/JIS commands. UTF-8 handling on NX/LX models is model/firmware specific **[unverified]** - transcode unless tested.

## 8. EZPL (Godex) - not ZPL

Source: https://www.manualsdir.com/manuals/736893/godex-ezpl.html (GoDEX EZPL Programmer's Manual, 89 pages)

```
^Q50,3
^W100
^H10
^P1
^C2
^L
AC,50,50,1,1,0,0,Hello World
BQ,50,120,2,5,100,0,1,12345678
E
```

- Lines end with CR (CR LF accepted).
- `^Q` label length,gap (mm); `^W` width (mm); `^H` darkness; `^L` begins the label format; `E` ends it and prints. `Ax` = text with font A..H (`AC` here), `Bx` = barcode (`BQ` = Code 128 with auto subset, `BA` = Code 39).
- Copies: `^P<n>` = number of labels (sets), `^C<n>` = copies of each label.
- Signature: setup lines starting `^Q`, `^W`, `^H`, then `^L` on its own line and a final line containing only `E`. Immediate commands start with `~` (e.g. `~S,ES` switches emulation). Shares the `^` prefix with ZPL but never contains `^XA`.
- Encoding: single-byte code page via `^XSET,CODEPAGE,n`; a Unicode/UTF-8 mode exists on newer firmware for TrueType text **[unverified command]**. Firmware default language is auto-switch EZPL/GEPL/GZPL; `~S,ES,G` forces EZPL, `E` GEPL, `Z` GZPL.

## 9. PPLA (Argox, Datamax-DPL compatible)

Source: Argox PPLA command sections: https://www.manualsdir.com/manuals/754556/argox-cp-series.html?page=65 ; PPLA Programmer's Manual copy: https://www.scribd.com/document/477162653/argox-ArgoxPPLA-pdf

```
<STX>L
D11
H10
121100000500050Hello World
1A3106001200050 12345678
Q0002
E
```

- Identical framing to DPL: `<STX>L` ... `E`, records ending in CR, `Qnnnn` for quantity.
- Signature: `<STX>L` (0x02 0x4C). PPLA cannot be distinguished from DPL by content.
- Encoding: single-byte symbol sets only. Transcode.

## 10. PPLB (Argox, Eltron-EPL2 compatible)

Source: https://www.manualsdir.com/manuals/754556/argox-cp-series.html?page=66 and https://manualslib.com/manual/1147063/Argox-Os-214-Plus.html?page=64

```
<LF>
N
q812
Q406,24
I8,A,001
A50,50,0,3,1,1,N,"Hello World"
B50,120,0,1,2,6,100,B,"12345678"
P2
```

- Same syntax as EPL2: `N` clears, `A` text, `B` barcode, `P<n>` prints n labels.
- Signature: same as EPL2 (line with only `N`, `A`/`B` records with quoted data, trailing `P<n>`). PPLB cannot be distinguished from EPL2 by content.
- Encoding: `I` command code pages, single-byte. Transcode.
- (PPLZ, Argox's third emulation, is ZPL-compatible: use the ZPL sample.)

## 11. TPCL (Toshiba TEC)

Source: Toshiba TEC "External Equipment Interface Specification" for B-EV4 / B-EX4 / BV400 series (official URL not located this session **[unverified]**)

```
{D0500,1000,0470|}
{C|}
{PC000;0100,0100,10,10,A,00,B=Hello World|}
{XB00;0100,0300,9,3,02,0,0100=12345678|}
{XS;I,0002,0002C4101|}
```

- Units are 0.1 mm. `D` = label pitch, effective width, effective length; `C` = clear image buffer; `PC000` = text field 000 (bitmap font A, 1.0x magnification); `XB00` = barcode field 00 (type 9 = CODE128 auto, 2-dot module, height 10.0 mm); `XS;I,<copies>,...` = issue.
- Each command is framed by control codes. Factory default is `<ESC>` ... `<LF><NUL>`; the alternative printable set `{` ... `|}` is a printer setting ("Control code: Auto/ESC LF NUL/{|}").
- Copies: first parameter of `XS;I` (`0002`).
- Signature: `{D` or `<ESC>D` followed by 4-digit groups, `{C|}`, and a closing `XS;I,`. Either `{...|}` pairs or `1B ... 0A 00` framing.
- Encoding: single-byte code page chosen in printer parameters (PC-850, PC-8, PC-1252, ... and UTF-8 on newer models) - a printer setting, not a per-job command **[unverified]**.

## 12. Fingerprint / Direct Protocol (Intermec / Honeywell "- DP" drivers)

Source: Honeywell "Fingerprint Command Reference" and "Direct Protocol" programmer's reference (official URL not located this session **[unverified]**)

```
NASC 8
BF ON
PP 50,50
FT "Univers",10
PT "Hello World"
PP 50,120
BT "CODE128"
BH 100
BM 2
PB "12345678"
PF 2
```

- Lines end with CR (or CR LF). Abbreviated and long forms are equivalent (`PP`=`PRPOS`, `PT`=`PRTXT`, `PB`=`PRBAR`, `PF`=`PRINTFEED`, `FT`=`FONT`, `BT`=`BARTYPE`).
- Copies: `PF n` (`PRINTFEED n`).
- Signature: BASIC-like lines with two-letter mnemonics; `PP x,y` followed by `PT "..."` and a closing `PF`. Direct Protocol sessions may begin with `INPUT ON` and end with `INPUT OFF`; layouts use `LAYOUT INPUT`.
- Encoding: `NASC n` selects the character set - `NASC 8` = UTF-8, `NASC 1252` = Windows Latin-1, default `NASC 1` (Roman 8). Legacy Fingerprint 8.x printers (PD41/PM4i) need `NASC -2` or similar for UTF-8 **[unverified]**.

## 13. IPL (Intermec Printer Language, "- IPL" drivers)

Source: https://www.ers-online.co.uk/files/products/files/IPL-EN-CR.pdf (IPL Command Reference; Honeywell document sps-ppr-ipl-en-cr.pdf, mirror: https://www.quinta.co.in/wp-content/uploads/2024/03/sps-ppr-ipl-en-cr.pdf)

```
<STX><ESC>C<ETX>
<STX><ESC>P<ETX>
<STX>E4;F4;<ETX>
<STX>H0;o50,50;f0;c25;h20;w20;d3,Hello World;<ETX>
<STX>B1;o50,120;f0;c6,0;h100;w2;i1;d3,12345678;<ETX>
<STX>R;<ETX>
<STX><ESC>E4<ETX>
<STX><CAN><ETX>
<STX><RS>2<ETX>
<STX><ETB><ETX>
```

- `<ESC>C` = advanced mode, `<ESC>P` = program mode, `E4;F4;` = erase/create format 4, `H0` = human-readable field, `B1` = barcode field (`c6` = Code 128), `R;` = leave program mode, `<ESC>E4` = select format, `<CAN>` = clear data, `<ETB>` = print.
- Copies: `<RS>n` sets the quantity (`<US>n` sets the batch count).
- Signature: every message framed `02 ... 03` (STX/ETX) with `;`-separated sub-commands; `<STX><ESC>C<ETX>` or `<STX><ESC>P<ETX>` near the start.
- Encoding: single-byte with selectable international character set / code page; UTF-8 support depends on firmware and font **[unverified]**. Transcode.

## 14. ESC/Label (Epson ColorWorks)

Source: https://download4.epson.biz/sec_pubs/bs/pdf/Esclabel_crg_en_11.pdf (ESC/Label Command Reference Guide)

```
^XA
^CI28
^PW2400
^LL1200
^FO100,100^A0N,80,80^FDHello World^FS
^FO100,300^BY4^BCN,200,Y,N,N^FD12345678^FS
^PQ2
^XZ
```

- ESC/Label is a ZPL-II-compatible subset plus Epson-specific `^S(...` commands (media, colour, cutter settings). ColorWorks heads are 600 dpi, so coordinates are roughly 3x the 203-dpi values.
- Copies: `^PQ2`.
- Signature: `^XA` ... `^XZ` - indistinguishable from ZPL by content; only Epson `^S(` commands or the queue name (`CW-C6000`, `CW-C4000`, `Colorworks C7500`) identify it.
- Encoding: `^CI28` = UTF-8, as in ZPL.

## 15. StarPRNT (Star mC-Print, TSP100IV, TSP650II in StarPRNT mode)

Source: StarPRNT Command Specifications (Star Micronics; PDF URL not located this session **[unverified]**); SDK builder reference: https://star-m.jp/products/s_print/sdk/starprnt_sdk/manual/wind_csharp/en/api_starioext_i_command_builder.html

```
1B 40                         ESC @            initialise
1B 1D 74 20                   ESC GS t 32      code page 32 = Windows-1252
48 65 6C 6C 6F 20 57 6F 72 6C 64 0A            "Hello World" LF
1B 62 06 02 02 64 31 32 33 34 35 36 37 38 1E   ESC b 6 2 2 100 "12345678" RS  (Code128, HRI below, mode 2, height 100)
1B 64 02                      ESC d 2          feed and partial cut
```

- Copies: no quantity command; send the job n times.
- Signature: `1B 40` then Star-specific `1B 1D ..` sequences; barcode is `1B 62 ... 1E` (ESC b ... RS) and cut is `1B 64 n`. ESC/POS uses `1D 6B` and `1D 56` instead - that pair is the practical discriminator, because both languages start with ESC @.
- Encoding: code page with `ESC GS t n`; StarPRNT models can be switched to UTF-8 with `ESC GS ) U` (`1B 1D 29 55 02 00 30 01`) **[byte sequence unverified]**. Star Line Mode models (older TSP650/TSP700) are single-byte only. TSP100 (non-IV) is raster-only and accepts none of this.

---

## Content signatures at a glance

| First bytes / pattern | Language(s) |
|---|---|
| `^XA` | ZPL, ESC/Label, Argox PPLZ, Godex GZPL, SATO SZPL, Honeywell ZSim, SNBC BPLZ |
| `N<LF>` ... `A..,"..."` ... `P1` | EPL2, Argox PPLB, Godex GEPL, SATO SEPL, Honeywell ESim, SNBC BPLE |
| `SIZE ` ... `CLS` ... `PRINT ` | TSPL, Brother FBPL |
| `! 0 200 200 ` | CPCL |
| `02 4C` (`<STX>L`) ... `E` | DPL, Argox PPLA, SATO SDPL |
| `1B 40` + `1D 6B` / `1D 56` | ESC/POS |
| `1B 40` + `1B 62` / `1B 1D` | StarPRNT / Star Line |
| `1B 41` ... `1B 5A` | SBPL |
| `^Q`, `^W`, `^L` ... line `E` | EZPL |
| `{D....|}` or `1B 44 ... 0A 00` | TPCL |
| `PP x,y` / `PT "..."` / `PF` | Fingerprint / Direct Protocol |
| `02 1B 43 03`, `02 ... 03` framing | IPL |
