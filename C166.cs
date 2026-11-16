namespace Me7Vars;

public enum OpKind { Mem, Imm, Idx, CAddr, Bit, Ext }

/// <summary>Operand carrying an address or immediate. <c>Extra</c>: opcode / segment / bit number (-1 = none).</summary>
public readonly record struct Operand(OpKind Kind, int Value, int Extra);

public enum RefKind { Mem, Imm, Idx, Bit }

/// <summary>Data address an instruction touches, resolved to a physical (A2L style) address.</summary>
public readonly record struct DataRef(RefKind Kind, int Addr, int Q);

/// <summary>One decoded instruction. <c>Masked</c> packs length + bytes with addresses/immediates zeroed.</summary>
public sealed record Insn(int Pc, int Len, ulong Masked, Operand[] Ops, DataRef[] Refs);

/// <summary>
/// Minimal C166/C167 decoder: instruction length plus the operand fields that carry addresses.
/// No mnemonics - just enough to align two programs and read their data references.
/// </summary>
public static class C166
{
    static readonly HashSet<int> L4 = new();
    static readonly HashSet<int> MemOps = new();
    static readonly HashSet<int> Imm16Ops = new();
    static readonly HashSet<int> IdxOps = new() { 0xC4, 0xD4, 0xE4, 0xF4 };

    static C166()
    {
        for (var row = 0; row < 8; row++)               // ADD..OR families
        {
            var b = row << 4;
            foreach (var lo in new[] { 2, 3, 4, 5, 6, 7, 0xA }) L4.Add(b | lo);
            MemOps.Add(b | 2); MemOps.Add(b | 3);
            if (row != 4) { MemOps.Add(b | 4); MemOps.Add(b | 5); }
            Imm16Ops.Add(b | 6);
        }
        foreach (var op in new[] { 0x82, 0x84, 0x86, 0x87, 0x8A, 0x92, 0x94, 0x96, 0x97, 0x9A,
                                   0xA2, 0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xB2, 0xB4, 0xB5, 0xB6, 0xB7, 0xBA,
                                   0xC2, 0xC4, 0xC5, 0xC6, 0xCA, 0xD2, 0xD4, 0xD5, 0xD6, 0xD7, 0xDA,
                                   0xE2, 0xE4, 0xE6, 0xE7, 0xEA, 0xF2, 0xF3, 0xF4, 0xF6, 0xF7, 0xFA })
            L4.Add(op);
        foreach (var op in new[] { 0x82, 0x92, 0xA2, 0xB2, 0x84, 0x94, 0xA4, 0xB4,
                                   0xC2, 0xC5, 0xD2, 0xD5, 0xD6, 0xF2, 0xF3, 0xF6, 0xF7 })
            MemOps.Add(op);
        foreach (var op in new[] { 0x86, 0x96, 0xA6, 0xB6, 0xC6, 0xE6 }) Imm16Ops.Add(op);
    }

    public static int ILen(int op) => L4.Contains(op) ? 4 : 2;

    static ulong Pack(int n, ReadOnlySpan<byte> m)
    {
        var v = (ulong)n << 32;
        for (var i = 0; i < m.Length; i++) v |= (ulong)m[i] << (8 * i);
        return v;
    }

    /// <summary>Decode one instruction: length, masked bytes, address-carrying operands.</summary>
    public static (int Len, ulong Masked, Operand[] Ops) Decode(byte[] buf, int pc)
    {
        int op = buf[pc];
        var n = ILen(op);
        Span<byte> m = stackalloc byte[4];
        if (pc + n > buf.Length)
        {
            m[0] = buf[pc]; m[1] = pc + 1 < buf.Length ? buf[pc + 1] : (byte)0;
            return (2, Pack(2, m[..2]), Array.Empty<Operand>());
        }
        for (var i = 0; i < n; i++) m[i] = buf[pc + i];
        var raw = buf.AsSpan(pc, n);
        var ops = new List<Operand>(2);
        var lo = op & 0xF;
        if (n == 4)
        {
            var w = raw[2] | (raw[3] << 8);
            if (MemOps.Contains(op)) { ops.Add(new(OpKind.Mem, w, op)); m[2] = m[3] = 0; }
            else if (Imm16Ops.Contains(op)) { ops.Add(new(OpKind.Imm, w, op)); m[2] = m[3] = 0; }
            else if (IdxOps.Contains(op)) { ops.Add(new(OpKind.Idx, w, op)); m[2] = m[3] = 0; }
            else if (op is 0xCA or 0xEA) { ops.Add(new(OpKind.CAddr, w, -1)); m[2] = m[3] = 0; }      // CALLA / JMPA
            else if (op is 0xDA or 0xFA) { ops.Add(new(OpKind.CAddr, w, raw[1])); m[1] = m[2] = m[3] = 0; } // CALLS / JMPS
            else if (op == 0xE2) { ops.Add(new(OpKind.CAddr, w, -1)); m[2] = m[3] = 0; }               // PCALL
            else if (op == 0xE7 || (lo == 7 && op < 0x80)) { m[2] = m[3] = 0; }                          // #data8
            else if (op is 0x8A or 0x9A or 0xAA or 0xBA)                                                 // JB.. bitaddr.q, rel
            {
                ops.Add(new(OpKind.Bit, raw[1], raw[3] >> 4));
                m[1] = 0; m[2] = 0;
            }
            else if (lo == 0xA && op < 0x80)                                                             // BFLDL/BFLDH/BCMP/BMOV..
            {
                if (op is 0x0A or 0x1A)
                {
                    ops.Add(new(OpKind.Bit, raw[1], -1));
                    m[1] = m[2] = m[3] = 0;
                }
                else
                {
                    ops.Add(new(OpKind.Bit, raw[1], raw[3] & 0xF));
                    ops.Add(new(OpKind.Bit, raw[2], raw[3] >> 4));
                    m[1] = m[2] = 0;
                }
            }
            else if (op == 0xD7) { ops.Add(new(OpKind.Ext, w, raw[1])); m[2] = m[3] = 0; }              // EXTP/EXTS #pag/#seg
        }
        else
        {
            if (lo is 0xE or 0xF) { ops.Add(new(OpKind.Bit, raw[1], op >> 4)); m[1] = 0; }               // BCLR/BSET
            else if (lo == 0xD) m[1] = 0;                                                                // JMPR
            else if (op == 0xBB) m[1] = 0;                                                               // CALLR
        }
        return (n, Pack(n, m[..n]), ops.ToArray());
    }
}

/// <summary>A flash image disassembled by linear sweep over the code ranges.</summary>
public sealed class Image
{
    public byte[] Data { get; }
    public int[] Dpp { get; }
    public List<Insn> Insns { get; } = new();
    readonly (int Start, int End)[] _codeRanges;

    public Image(byte[] data, (int, int)[]? codeRanges = null)
    {
        Data = data;
        Dpp = FindDpp();
        _codeRanges = codeRanges ?? new[] { (0x0, 0xC000), (0x22000, 0x90000) };
        Disasm();
    }

    /// <summary>Looks for "mov DPP0,#; mov DPP1,#; mov DPP2,#; mov DPP3,#" in the startup code.</summary>
    int[] FindDpp()
    {
        var d = Data;
        for (var i = 0; i < 0x2000; i += 2)
        {
            if (d[i] == 0xE6 && d[i + 1] == 0x00 && d[i + 4] == 0xE6 && d[i + 5] == 0x01 &&
                d[i + 8] == 0xE6 && d[i + 9] == 0x02 && d[i + 12] == 0xE6 && d[i + 13] == 0x03)
                return new[] { d[i + 2] | d[i + 3] << 8, d[i + 6] | d[i + 7] << 8,
                               d[i + 10] | d[i + 11] << 8, d[i + 14] | d[i + 15] << 8 };
        }
        return new[] { 0, 1, 2, 3 };
    }

    /// <summary>16-bit DPP address -> physical address.</summary>
    public int Phys(int mem) => (Dpp[mem >> 14] & 0x3FF) * 0x4000 + (mem & 0x3FFF);

    void Disasm()
    {
        var d = Data;
        foreach (var (a, end) in _codeRanges)
        {
            int pc = a, b = Math.Min(end, d.Length - 4);
            (char Mode, int Value, int Remaining)? ext = null;
            while (pc < b)
            {
                var (n, masked, ops) = C166.Decode(d, pc);
                var refs = new List<DataRef>(2);
                foreach (var o in ops)
                {
                    switch (o.Kind)
                    {
                        case OpKind.Mem:
                        {
                            var w = o.Value;
                            int? pa;
                            if (ext is { Mode: 'p' } e1) pa = e1.Value * 0x4000 + (w & 0x3FFF);
                            else if (ext is { Mode: 's' } e2) pa = e2.Value * 0x10000 + w;
                            else if (w >= 0xFE00 || (w >= 0xF000 && w < 0xF200)) pa = null;   // SFR / ESFR
                            else pa = Phys(w);
                            if (pa is int p) refs.Add(new(RefKind.Mem, p, o.Extra));
                            break;
                        }
                        case OpKind.Imm or OpKind.Idx:
                            if ((o.Value >= 0x8000 && o.Value < 0xC000) || (o.Value >= 0xF600 && o.Value < 0xFE00))
                                refs.Add(new(o.Kind == OpKind.Imm ? RefKind.Imm : RefKind.Idx, Phys(o.Value), o.Extra));
                            break;
                        case OpKind.Bit:
                            if (o.Value < 0x80) refs.Add(new(RefKind.Bit, 0xFD00 + 2 * o.Value, o.Extra));
                            break;
                    }
                }
                if (ext is { } cur) ext = cur.Remaining > 1 ? (cur.Mode, cur.Value, cur.Remaining - 1) : null;
                foreach (var o in ops)
                {
                    if (o.Kind != OpKind.Ext) continue;
                    var mode = d[pc + 1] >> 6;
                    var cnt = ((d[pc + 1] >> 4) & 3) + 1;
                    if (mode is 0 or 1)                         // EXTS / EXTP (register forms ignored)
                        ext = (mode == 0 ? 's' : 'p', o.Value & (mode == 0 ? 0xFF : 0x3FF), cnt);
                }
                Insns.Add(new Insn(pc, n, masked, ops, refs.ToArray()));
                pc += n;
            }
        }
    }
}
