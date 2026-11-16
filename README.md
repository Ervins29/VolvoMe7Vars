# me7vars — RAM variables for VolvoTools logging (Volvo Bosch ME7.0)

`me7vars` finds the RAM addresses of logging variables (`nmot_w`, `rl_w`, `ps_w`, `lamsoni_w`, …) in
a Volvo ME7.0 flash, and writes them as a **`.ecu` variables file for [VolvoTools](https://github.com/prometey1982/VolvoTools)
`VolvoLogger`**. It uses the same format as the `example.ecu` / `s60r.ecu` files that come with VolvoTools.

Those sample files only fit the software they were made for. Every software version puts its variables at
different addresses: `nmot_w` is at 0xF410 in `s60r.ecu`, 0xF852 in 359462 and 0xF854 in a 359462-based tune.
Logging a different software with the wrong file gives you garbage. `me7vars` builds the right file for
*your* flash.

## Quick start
```
dotnet build -c Release
me7vars my_car.bin                       -> my_car.ecu  (+ my_car_report.csv)
VolvoLogger.exe -f P2 --variables my_car.ecu -o log.csv
```
Read your flash with VolvoTools (VolvoFlasher) or any other reader, run `me7vars` on it, and pass the `.ecu`
to VolvoLogger with `--variables`. Pick `-f` to match your car (`P80`, `P2`, `P2_250`, … — `VolvoLogger --help`
lists them).

## Output: the .ecu file
```
Name            , Address  , Size , Bitmask , Unit       , Signed , I , Factor      , Offset  , Comment
nmot_w          ,   0xF854 ,    2 ,  0x0000 , U/min      ,      0 , 0 ,        0.25 ,       0 , Engine Speed
ps_w            ,   0xF82C ,    2 ,  0x0000 , hPa        ,      0 , 0 ,   0.0390625 ,       0 , Manifold Pressure
tans            , 0x300835 ,    1 ,  0x0000 , Grad C     ,      0 , 0 ,        0.75 ,     -48 , Intake Air Temperature
```
How VolvoLogger reads each column (`Logger/src/LogParameters.cpp`, `LogParameter.cpp`):

| Column | Meaning in VolvoLogger |
|---|---|
| `Name` | variable name; goes into the log column header |
| `Address` | hex, read as a 24-bit address (`0xF854` = internal RAM, `0x30xxxx` = external RAM) |
| `Size` | bytes to read: 1, 2 or 4 |
| `Bitmask` | if not 0, `value &= mask`. The value is **not shifted**, so a flag with mask `0x0080` logs as 0 or 128 × factor |
| `Unit` | text only; goes into the log column header |
| `Signed` | 1 = read as int8/int16/int32 |
| `I` | 1 = inverse conversion `factor / (raw + offset)`; me7vars always writes 0 |
| `Factor`, `Offset` | `physical = raw × factor + offset` (taken from the A2L conversion) |
| `Comment` | log column header text: `Comment(Unit) Name` |

Columns are matched by header name and spaces around values are trimmed, so the padding is only
there for readability. Values must not contain commas (the parser has no quoting); me7vars strips them.
The optional `DataType` column (`F` = float) isn't needed for ME7.

### Which variables
- **Default**: a logging set similar to `s60r.ecu` (39 variables: pedal, rpm, speed, boost actual/target,
  lambda, load, torque, ignition, MAF, injection time, fuel trim, throttle, temperatures, battery voltage, knock).
- `--names nmot_w,rl_w,ps_w,...` — just these A2L names (look them up in the report).
- `--all` — every variable found (~4,500). **Don't log this file directly.** VolvoLogger registers each
  line with the ECU, and the sample files use about 30. Use it as a catalogue: copy the lines you want
  into your own file, or rebuild with `--names`. With `--all`, comments are the long A2L descriptions.
- `--min-conf high|medium|inferred|low` — confidence a variable needs to be written (default `medium`).

`<out>_report.csv` lists every variable found, with its confidence, vote count, reference address and
shift. That's the place to check before trusting an unusual variable.

## How it finds the addresses
`me7vars` needs a **reference**: a flash plus its A2L (the Bosch description with the real RAM addresses).
It disassembles both flashes (C166), lines up the code that matches with addresses ignored, and reads off the
address each matching instruction uses in your flash. Every match votes; the result is the address most
votes agree on.

1. DPP setup read from the startup code (Volvo ME7.0: DPP0=4, DPP1=5, DPP2=0xC0 → RAM 0x30xxxx, DPP3=3).
2. Linear sweep disassembly over 0x0–0xC000 and 0x22000–0x90000.
3. Addresses and immediates masked; unique 8-instruction windows found in both flashes and extended both ways.
4. Every aligned pair that touches memory votes "reference address → your address" (direct memory operands,
   RAM pointers, EXTP/EXTS forms, bit addresses 0xFD00–0xFDFF).
5. Each A2L measurement gets the winning address and a confidence:
   - **high**: ≥3 agreeing votes (≥90 %), or fewer but the same shift as its RAM neighbours
   - **medium**: few votes, mostly agreeing
   - **inferred**: never accessed directly; address taken from neighbours that share the same shift
   - **low**: conflicting votes, a collision with another variable, a change of RAM region, or a shift over 0x800

The closer your software is to a reference, the more variables come out. A 359462-based tune lines up
about 83 % of the code and gives about 4,500 high-confidence variables.

## References
References are picked up automatically. Put each bin + A2L pair in its own folder under `references/`:
```
references/
  359462/            <- reference name = folder name
    1xEbvsde.bin
    1xEbvsde.a2l
  <another sw>/
    something.bin
    something.a2l
```
The bin is matched to the A2L by file name, or taken as the only .bin in that folder.
Everything under `references/` is copied next to the exe on build. At run time the program searches
`references/` next to the exe, then in the current folder. Every reference is tried and the one with the
most matching code wins (with several references, it prints a score for each). A new reference for another
Volvo software (S60R/V70R ME7.0.1, P80 T5 …) makes that family work.

| Option | Meaning |
|---|---|
| `--list-refs` | show the references found and exit |
| `--ref NAME` | use only this reference (folder or bin file name) |
| `--ref-dir DIR` | search DIR instead of the default folders |
| `--ref-bin F --ref-a2l F` | use this exact pair, no discovery |

## Checks
- Reference against itself: every variable maps back to its own address.
- 359462-based tune: the ignition-map inputs traced by hand (nmot 0xF852, rl 0xF829, tmot 0x300836) match,
  and ROM map pointers sent through the same alignment land exactly on independently defined maps
  (KFZW, KFMIRL, KFPED, MLHFM, KFMIOP, KFKHFM, …).
- Not yet checked against a live VolvoLogger log. A first log of `nmot_w`, `rl_w`, `ps_w` and `tmot` at idle and
  on a pull is the quickest sanity check: rpm matching the dash, `ps_w` close to ambient (~1000 hPa) with the engine off, sensible coolant temperature.

## Files
| File | What it does |
|---|---|
| `C166.cs` | C166 decoder (length, masked bytes, address operands), `Image`: DPP detection, linear sweep, EXTP/EXTS, physical address resolution |
| `A2l.cs` | ASAP2 reader: MEASUREMENT + linear COMPU_METHOD (factor/offset/unit), CHARACTERISTIC addresses |
| `Transfer.cs` | Alignment: unique 8-instruction masked seeds, extended both ways; votes reference → target addresses |
| `Finder.cs` | One result per measurement: bit flags, direct votes, region/shift sanity checks, neighbour inference, arrays, collisions |
| `References.cs` | finds bin + A2L pairs under `references/`, runs each against the target |
| `Program.cs` | CLI, `.ecu` writer (VolvoLogger format) and report |

.NET 10, no NuGet packages (`nuget.config` turns package sources off so it builds offline; delete it if you add packages).
