using System.Buffers;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Vectorized;

/// <summary>
/// A row-level expression lowered for the column-at-a-time interpreter (design 7.7, backend A): request-level
/// subtrees become a <see cref="ScalarProgram"/> evaluated once per call, every row-varying node becomes one
/// <see cref="Instr"/>, and registers are assigned at compile time by liveness so that temporaries are reused.
/// </summary>
/// <remarks>
/// <para>Row domain: when the requested rows are at least half of the physical rows, every instruction runs over
/// the full physical range <c>[0, PhysicalRowCount)</c> and column arrays are read in place; otherwise the inputs are
/// gathered densely for the requested rows. Either way only the requested rows are written to the destination.</para>
/// <para>Per call the program rents one array per register file (plus value slots and node locations) from
/// <see cref="ArrayPool{T}.Shared"/> and returns them before returning: no steady-state allocation.</para>
/// </remarks>
internal sealed class VectorProgram
{
    private readonly Instr[] _instrs;
    private readonly ScalarProgram _hoisted;
    private readonly int _fRegs;
    private readonly int _iRegs;
    private readonly int _bRegs;
    private readonly int _sRegs;
    private readonly ExprType _type;

    private VectorProgram(Instr[] instrs, ScalarProgram hoisted, int[] registerCounts, ExprType type)
    {
        _instrs = instrs;
        _hoisted = hoisted;
        _fRegs = registerCounts[(int)RegFile.F];
        _iRegs = registerCounts[(int)RegFile.I];
        _bRegs = registerCounts[(int)RegFile.B];
        _sRegs = registerCounts[(int)RegFile.S];
        _type = type;
    }

    public IReadOnlyList<Instr> Instructions => _instrs;

    public int RegisterCount => _fRegs + _iRegs + _bRegs + _sRegs;

    public int HoistedCount => _hoisted.SlotCount;

    private int Root => _instrs.Length - 1;

    public static VectorProgram Build(IrNode root, ExprType resultType) => new Lowering().Build(root, resultType);

    public void Evaluate(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination, in ScalarValue outOfRange)
    {
        var table = context.Table ?? throw new InvalidOperationException("A row-level expression needs EvalContext.Table.");
        var physical = table.PhysicalRowCount;
        if (!LoadKernels.AllInRange(rows, physical))
        {
            foreach (var r in rows)
            {
                if ((uint)r >= (uint)physical)
                {
                    throw new ArgumentOutOfRangeException(nameof(rows), $"Row {r} is outside the table's physical rows [0, {physical}).");
                }
            }
        }

        var identity = (long)rows.Length * 2 >= physical;
        var n = identity ? physical : rows.Length;
        var words = Bits.WordCount(n);

        var scalars = Rent<ScalarValue>(_hoisted.SlotCount);
        var locs = Rent<Loc>(_instrs.Length);
        var f64 = Rent<double>(_fRegs * n);
        var i64 = Rent<long>(_iRegs * n);
        var bits = Rent<ulong>(_bRegs * words);
        var strings = Rent<string?>(_sRegs * n);
        try
        {
            _hoisted.Run(context, scalars);

            var frame = new Frame
            {
                N = n,
                Physical = physical,
                Words = words,
                Identity = identity,
                Map = identity ? default : rows,
                Context = context,
                Scalars = scalars,
                Locs = locs,
                F = f64,
                I = i64,
                B = bits,
                S = strings,
            };

            foreach (var instr in _instrs)
            {
                instr.Execute(ref frame);
            }

            DestinationWriter.Write(ref frame, Root, _type, rows, destination, outOfRange);
        }
        finally
        {
            Return(scalars, clear: true);
            Return(locs, clear: true);
            Return(f64, clear: false);
            Return(i64, clear: false);
            Return(bits, clear: false);
            Return(strings, clear: true);
        }
    }

    private static T[] Rent<T>(int length) => length == 0 ? Array.Empty<T>() : ArrayPool<T>.Shared.Rent(length);

    private static void Return<T>(T[] array, bool clear)
    {
        if (array.Length > 0)
        {
            ArrayPool<T>.Shared.Return(array, clear);
        }
    }

    public override string ToString() => string.Join("\n", (IEnumerable<Instr>)_instrs);

    /// <summary>IR → instructions, with pattern fusion and register allocation.</summary>
    private sealed class Lowering
    {
        private readonly Dictionary<IrNode, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly List<Instr> _instrs = new();
        private readonly ScalarProgram.Builder _scalars = new();

        public VectorProgram Build(IrNode root, ExprType resultType)
        {
            var rootOperand = Visit(root);
            if (rootOperand.IsScalar || rootOperand.Index != _instrs.Count - 1)
            {
                throw new InvalidOperationException("The root of a row-level program must be its last instruction.");
            }

            var instrs = _instrs.ToArray();
            var counts = AllocateRegisters(instrs);
            return new VectorProgram(instrs, _scalars.Build(), counts, resultType);
        }

        private Opnd Visit(IrNode node)
        {
            if (node.IsInvariant)
            {
                return Opnd.Scalar(_scalars.Add(node));
            }

            if (_index.TryGetValue(node, out var existing))
            {
                return Opnd.Node(existing);
            }

            var instr = Lower(node);
            instr.Out = _instrs.Count;
            _instrs.Add(instr);
            _index.Add(node, instr.Out);
            return Opnd.Node(instr.Out);
        }

        private Opnd[] VisitAll(IrNode[] nodes)
        {
            var result = new Opnd[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                result[i] = Visit(nodes[i]);
            }

            return result;
        }

        /// <summary>A column stored dictionary-encoded (a string column; a text column reads as a string but is stored plainly).</summary>
        private static bool IsDictColumn(IrNode node) => node.Op == IrOp.Column && node.Symbol.Type.Kind == StorageKind.String;

        private Instr Lower(IrNode node)
        {
            var t = node.Type;
            var a = node.Args;
            switch (node.Op)
            {
                case IrOp.Column:
                    return new ColumnInstr(node.Symbol, t);
                case IrOp.Neg or IrOp.Abs when t.Kind == TypeKind.Int:
                    return new IntUnaryInstr(node.Op, t, Visit(a[0]));
                case IrOp.Neg or IrOp.Abs or IrOp.Sqrt or IrOp.Exp or IrOp.Log or IrOp.Log1p or IrOp.Sigmoid:
                    return new FloatUnaryInstr(node.Op, t, Visit(a[0]));
                case IrOp.ToFloat:
                    return new ToFloatInstr(t, Visit(a[0]));
                case IrOp.ToInt:
                    return new ToIntInstr(t, Visit(a[0]));
                case IrOp.Not:
                    return new NotInstr(t, Visit(a[0]));
                case IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Min or IrOp.Max or IrOp.Mod when t.Kind == TypeKind.Int:
                    return new IntBinaryInstr(node.Op, t, Visit(a[0]), Visit(a[1]));
                case IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Div or IrOp.Pow or IrOp.Min or IrOp.Max:
                    return new FloatBinaryInstr(node.Op, t, Visit(a[0]), Visit(a[1]));
                case IrOp.Eq or IrOp.Ne when IsDictColumn(a[0]) && a[1].IsInvariant:
                    return new DictEqualsInstr(node.Op, a[0].Symbol, a[0].Type, t, Visit(a[1]));
                case IrOp.Eq or IrOp.Ne when IsDictColumn(a[1]) && a[0].IsInvariant:
                    return new DictEqualsInstr(node.Op, a[1].Symbol, a[1].Type, t, Visit(a[0]));
                case IrOp.Eq or IrOp.Ne or IrOp.Lt or IrOp.Le or IrOp.Gt or IrOp.Ge:
                    return new CompareInstr(node.Op, a[0].Type.Kind, t, Visit(a[0]), Visit(a[1]));
                case IrOp.And or IrOp.Or:
                    return new LogicInstr(node.Op, t, Visit(a[0]), Visit(a[1]));
                case IrOp.Conditional when IsSource(t):
                    return SourceSelectInstr.Conditional(t, Visit(a[0]), Visit(a[1]), Visit(a[2]));
                case IrOp.Conditional:
                    return new SelectInstr(t, Visit(a[0]), Visit(a[1]), Visit(a[2]));
                case IrOp.Coalesce when IsSource(t):
                    return SourceSelectInstr.Coalesce(t, Visit(a[0]), Visit(a[1]));
                case IrOp.Coalesce:
                    return new CoalesceInstr(t, Visit(a[0]), Visit(a[1]));
                case IrOp.Has:
                    return new HasInstr(t, Visit(a[0]));
                case IrOp.Size:
                    return new SizeInstr(a[0].Type.Kind, t, Visit(a[0]));
                case IrOp.Contains when a[0].Type.Kind == TypeKind.ListString && a[0].IsInvariant && IsDictColumn(a[1]):
                    return new DictPredicateInstr(IrOp.Contains, a[1].Symbol, a[1].Type, t, null, new[] { Visit(a[0]) });
                case IrOp.Contains:
                    return new ContainsInstr(a[0].Type.Kind, t, Visit(a[0]), Visit(a[1]));
                case IrOp.InSet when IsDictColumn(a[0]):
                    return new DictPredicateInstr(IrOp.InSet, a[0].Symbol, a[0].Type, t, (ConstSet)node.Data!, Array.Empty<Opnd>());
                case IrOp.InSet:
                    return new InSetInstr((ConstSet)node.Data!, t, Visit(a[0]));
                case IrOp.StartsWith or IrOp.EndsWith when IsDictColumn(a[0]) && a[1].IsInvariant:
                    return new DictPredicateInstr(node.Op, a[0].Symbol, a[0].Type, t, null, new[] { Visit(a[1]) });
                case IrOp.StartsWith or IrOp.EndsWith:
                    return new AffixInstr(node.Op, t, Visit(a[0]), Visit(a[1]));
                case IrOp.Overlap:
                    return new OverlapInstr(a[0].Type.Kind, t, Visit(a[0]), Visit(a[1]));
                case IrOp.Dot or IrOp.Cosine or IrOp.L2Norm:
                    return new VectorFunctionInstr(node.Op, node.Data is Float32Lists, t, VisitAll(a));
                case IrOp.PiecewiseLinear:
                    return new PiecewiseInstr((Knots)node.Data!, t, Visit(a[0]));
                case IrOp.Hash01:
                {
                    var kinds = new TypeKind[a.Length];
                    for (var i = 0; i < a.Length; i++)
                    {
                        kinds[i] = a[i].Type.Kind;
                    }

                    return new Hash01Instr(kinds, t, VisitAll(a));
                }

                case IrOp.Lookup:
                    return new LookupInstr((ILookupTable)node.Data!, a[0].Type.Kind, t, Visit(a[0]), Visit(a[1]));
                default:
                    throw new InvalidOperationException($"No vectorized lowering for {node.Op}.");
            }
        }

        /// <summary>Kinds whose results are lazy per-lane sources (<see cref="SourceKind"/>) instead of registers: lists.
        /// An int or float list source may point at narrow or wide storage, lane by lane (see <see cref="Lanes.IntList"/>).</summary>
        private static bool IsSource(ExprType t) => t.IsList;

        /// <summary>
        /// Linear-scan allocation in program order. An instruction's output registers are taken before its
        /// operands' registers are released, so outputs never alias inputs. Retaining instructions (lazy selects)
        /// keep their operands live until they themselves die.
        /// </summary>
        private static int[] AllocateRegisters(Instr[] instrs)
        {
            var uses = new int[instrs.Length];
            foreach (var instr in instrs)
            {
                foreach (var o in instr.Operands)
                {
                    if (!o.IsScalar)
                    {
                        uses[o.Index]++;
                    }
                }
            }

            uses[^1]++; // the root stays live for the destination write

            var counts = new int[5];
            var free = new Stack<int>[5];
            for (var i = 0; i < free.Length; i++)
            {
                free[i] = new Stack<int>();
            }

            foreach (var instr in instrs)
            {
                if (instr.ValueFile != RegFile.None)
                {
                    instr.ValueReg = Allocate(instr.ValueFile);
                }

                if (instr.Type.Nullable)
                {
                    instr.ValidReg = Allocate(RegFile.B);
                }

                if (!instr.Retains)
                {
                    ReleaseOperands(instr);
                }
            }

            return counts;

            int Allocate(RegFile file) => free[(int)file].Count > 0 ? free[(int)file].Pop() : counts[(int)file]++;

            void ReleaseOperands(Instr instr)
            {
                foreach (var o in instr.Operands)
                {
                    if (!o.IsScalar && --uses[o.Index] == 0)
                    {
                        var dead = instrs[o.Index];
                        if (dead.ValueReg >= 0)
                        {
                            free[(int)dead.ValueFile].Push(dead.ValueReg);
                        }

                        if (dead.ValidReg >= 0)
                        {
                            free[(int)RegFile.B].Push(dead.ValidReg);
                        }

                        if (dead.Retains)
                        {
                            ReleaseOperands(dead);
                        }
                    }
                }
            }
        }
    }
}

/// <summary>
/// Writes evaluation results into a destination column (design 4.3): narrowing, validity bits, and the out-of-range
/// rule for int32 storage and fixed-length lists.
/// </summary>
/// <remarks>
/// <para>Narrowing an int64 into <c>int32</c> or <c>list&lt;int32&gt;</c> storage never wraps. A value outside the int32
/// range gets the caller's replacement (the column's default) or becomes missing, and is counted in
/// <see cref="ExpressionCounters.OutOfRange"/>. A list is replaced as a whole when any element is outside the range:
/// dropping or clamping elements would write a different list. A <c>list&lt;E,N&gt;</c> destination takes a list of
/// another length the same way: as a whole, counted, never cut or padded.</para>
/// <para>The in-range path stays a single pass: scalar narrowing writes every value and ORs a branch-free range flag,
/// and only when the flag is set does a second pass find and replace the offending (present) rows.</para>
/// </remarks>
internal static class DestinationWriter
{
    /// <summary>Throws when <paramref name="destination"/> cannot hold results of <paramref name="type"/>, or when the
    /// out-of-range replacement is present but would not fit the destination itself.</summary>
    public static void Validate(ExprType type, ColumnBuffer destination, in ScalarValue outOfRange)
    {
        var ok = (type.Kind, destination) switch
        {
            (TypeKind.Float, ScalarColumn<double> or ScalarColumn<float>) => true,
            (TypeKind.Int, ScalarColumn<long> or ScalarColumn<int> or ScalarColumn<double> or ScalarColumn<float>) => true,
            (TypeKind.Bool, ScalarColumn<bool>) => true,
            (TypeKind.String, DictStringColumn or TextColumn) => true,
            (TypeKind.ListBool, ListColumn<bool>) => true,
            (TypeKind.ListInt, ListColumn<long> or ListColumn<int>) => true,
            (TypeKind.ListFloat, ListColumn<double> or ListColumn<float>) => true,
            (TypeKind.ListString, ListColumn<string>) => true,
            _ => false,
        };
        if (!ok || (type.Length > 0 && !ColumnValues.FitsLength(type.Length, destination.Type)))
        {
            throw new ArgumentException($"A {type} expression result cannot be written to a {destination.Type} column.", nameof(destination));
        }

        if (!outOfRange.IsMissing && destination is ScalarColumn<int> or ListColumn && !Fits(outOfRange, destination))
        {
            throw new ArgumentException(
                $"The out-of-range replacement {outOfRange} cannot be stored in the {destination.Type} column itself; " +
                "pass the column's (validated) default or ScalarValue.Missing.", nameof(outOfRange));
        }
    }

    /// <remarks>
    /// The destination may be a column the expression itself reads (an in-place update such as <c>col.a ?? [7]</c>
    /// written into <c>col.a</c>). Every instruction has run before this method, and the lazy list results it
    /// resolves per lane only look at the same row of their sources, so writing a row never changes another row's
    /// inputs. The exception is a row's own validity bit, which a lazy <c>??</c> uses as its selection mask (zero-copy
    /// from the column in full-range mode): list and string destinations are therefore written row by row,
    /// reading the lane before updating that row's validity. Scalar results are already materialized in registers
    /// (or are the column's own array, row for row), so their values are copied first and validity follows.
    /// </remarks>
    public static void Write(ref Frame f, int root, ExprType type, ReadOnlySpan<int> rows, ColumnBuffer destination, in ScalarValue outOfRange)
    {
        var valid = f.Validity(Opnd.Node(root));
        var allValid = valid.IsConstant && valid.Fill == ulong.MaxValue;
        var identity = f.Identity;
        var narrowingFailed = false;
        switch (destination)
        {
            case ScalarColumn<double> d when type.Kind == TypeKind.Float:
                Scatter(f.GetF(root), d.Values, rows, identity);
                break;

            case ScalarColumn<float> d when type.Kind == TypeKind.Float:
            {
                var src = f.GetF(root);
                var dst = d.Values;
                for (var k = 0; k < rows.Length; k++)
                {
                    var row = rows[k];
                    dst[row] = (float)src[identity ? row : k];
                }

                break;
            }

            case ScalarColumn<double> d:
            {
                var src = f.GetI(root);
                var dst = d.Values;
                for (var k = 0; k < rows.Length; k++)
                {
                    var row = rows[k];
                    dst[row] = src[identity ? row : k];
                }

                break;
            }

            case ScalarColumn<float> d:
            {
                var src = f.GetI(root);
                var dst = d.Values;
                for (var k = 0; k < rows.Length; k++)
                {
                    var row = rows[k];
                    dst[row] = src[identity ? row : k];
                }

                break;
            }

            case ScalarColumn<long> d:
                Scatter(f.GetI(root), d.Values, rows, identity);
                break;

            case ScalarColumn<int> d:
                // Missing lanes hold arbitrary values, so the flag may be set by a lane that is not even written as
                // present; the second pass below only replaces present rows.
                narrowingFailed = !NarrowScatter(f.GetI(root), d.Values, rows, identity);
                break;

            case ScalarColumn<bool> d:
            {
                var src = f.GetB(root);
                var dst = d.Values;
                for (var k = 0; k < rows.Length; k++)
                {
                    var row = rows[k];
                    dst[row] = Bits.Get(src, identity ? row : k);
                }

                break;
            }

            default:
                WriteReferenceKinds(ref f, root, type.Kind, valid, allValid, rows, destination, outOfRange);
                return;
        }

        if (allValid)
        {
            // A table slot starts with every row missing (CandidateTable): the written rows become present. A scratch
            // buffer without a bitmap is already all valid.
            destination.SetValid(rows);
        }
        else
        {
            for (var k = 0; k < rows.Length; k++)
            {
                destination.SetValidity(rows[k], valid.Get(identity ? rows[k] : k));
            }
        }

        if (narrowingFailed)
        {
            ReplaceOutOfRange(ref f, root, valid, allValid, rows, (ScalarColumn<int>)destination, outOfRange);
        }
    }

    /// <summary>Copies lane values to the listed rows: lane = row in the identity domain, lane = k otherwise.</summary>
    private static void Scatter<T>(ReadOnlySpan<T> source, T[] destination, ReadOnlySpan<int> rows, bool identity)
    {
        if (identity)
        {
            foreach (var row in rows)
            {
                destination[row] = source[row];
            }
        }
        else
        {
            for (var k = 0; k < rows.Length; k++)
            {
                destination[rows[k]] = source[k];
            }
        }
    }

    /// <summary>Like <see cref="Scatter{T}"/>, narrowing int64 lanes to int32. Returns false when any lane (present or
    /// not) is outside the int32 range; those rows then hold truncated values until they are replaced.</summary>
    private static bool NarrowScatter(ReadOnlySpan<long> source, int[] destination, ReadOnlySpan<int> rows, bool identity)
    {
        var fits = true;
        if (identity)
        {
            foreach (var row in rows)
            {
                var v = source[row];
                destination[row] = unchecked((int)v);
                fits &= Int32Range.Fits(v);
            }
        }
        else
        {
            for (var k = 0; k < rows.Length; k++)
            {
                var v = source[k];
                destination[rows[k]] = unchecked((int)v);
                fits &= Int32Range.Fits(v);
            }
        }

        return fits;
    }

    /// <summary>Second pass of an int32 write whose range flag was set: every present row whose value does not fit takes
    /// the replacement (or becomes missing) and is counted.</summary>
    private static void ReplaceOutOfRange(ref Frame f, int root, WordView valid, bool allValid, ReadOnlySpan<int> rows, ScalarColumn<int> destination, in ScalarValue outOfRange)
    {
        var src = f.GetI(root);
        long replaced = 0;
        for (var k = 0; k < rows.Length; k++)
        {
            var row = rows[k];
            var lane = f.Identity ? row : k;
            if ((allValid || valid.Get(lane)) && !Int32Range.Fits(src[lane]))
            {
                Replace(destination, row, outOfRange);
                replaced++;
            }
        }

        f.Context.Counters?.AddOutOfRange(replaced);
    }

    /// <summary>Writes value and validity of each row in one step: the lane is read first, then <c>Set</c> marks the
    /// row present (or <c>SetMissing</c> marks it missing), so an aliased mask still holds this row's old bit when read
    /// (see <see cref="Write"/>). A list of another length than a <c>list&lt;E,N&gt;</c> destination's takes the
    /// out-of-range replacement, as a list with an element outside int32 storage does.</summary>
    private static void WriteReferenceKinds(
        ref Frame f, int root, TypeKind kind, WordView valid, bool allValid, ReadOnlySpan<int> rows, ColumnBuffer destination, in ScalarValue outOfRange)
    {
        var node = Opnd.Node(root);
        var fixedLength = destination.Type.FixedLength;
        long replaced = 0;
        for (var k = 0; k < rows.Length; k++)
        {
            var row = rows[k];
            var lane = f.Identity ? row : k;
            if (!allValid && !valid.Get(lane))
            {
                destination.SetMissing(row);
                continue;
            }

            switch (destination)
            {
                case DictStringColumn d:
                    d.Set(row, Lanes.String(ref f, node, lane));
                    break;
                case TextColumn d:
                    d.Set(row, Lanes.String(ref f, node, lane));
                    break;
                case ListColumn when fixedLength > 0 && Lanes.ListLength(ref f, node, lane, kind) != fixedLength:
                    Replace(destination, row, outOfRange);
                    replaced++;
                    break;
                case ListColumn<long> d:
                    WriteList(d, row, Lanes.IntList(ref f, node, lane));
                    break;
                case ListColumn<int> d:
                    if (!TryWriteList(d, row, Lanes.IntList(ref f, node, lane)))
                    {
                        Replace(d, row, outOfRange);
                        replaced++;
                    }

                    break;
                case ListColumn<double> d:
                    WriteList(d, row, Lanes.FloatList(ref f, node, lane));
                    break;
                case ListColumn<float> d:
                    WriteList(d, row, Lanes.FloatList(ref f, node, lane));
                    break;
                case ListColumn<bool> d:
                    d.Set(row, Lanes.BoolList(ref f, node, lane));
                    break;
                case ListColumn<string> d:
                    d.Set(row, Lanes.StringList(ref f, node, lane));
                    break;
            }
        }

        if (replaced > 0)
        {
            f.Context.Counters?.AddOutOfRange(replaced);
        }
    }

    /// <summary>Writes an int list into int64 storage, widening an int32 source while copying.</summary>
    private static void WriteList(ListColumn<long> destination, int row, IntListLane list)
    {
        if (list.IsInt32)
        {
            Int32Range.Widen(list.Int32, destination.SetUninitialized(row, list.Length));
        }
        else
        {
            destination.Set(row, list.Int64);
        }
    }

    /// <summary>Writes a float list into float64 storage, widening a float32 source while copying.</summary>
    private static void WriteList(ListColumn<double> destination, int row, FloatListLane list)
    {
        if (!list.IsFloat32)
        {
            destination.Set(row, list.Float64);
            return;
        }

        var extent = destination.SetUninitialized(row, list.Length);
        for (var i = 0; i < extent.Length; i++)
        {
            extent[i] = list.Float32[i];
        }
    }

    /// <summary>Writes a float list into float32 storage, narrowing a float64 source while copying (a value beyond
    /// float32 becomes an infinity, which the writer's non-finite rule handles, as for a float32 column).</summary>
    private static void WriteList(ListColumn<float> destination, int row, FloatListLane list)
    {
        if (list.IsFloat32)
        {
            destination.Set(row, list.Float32);
        }
        else
        {
            ColumnValues.NarrowFloats(list.Float64, destination.SetUninitialized(row, list.Length));
        }
    }

    /// <summary>Writes an int list into int32 storage. False, leaving the row untouched, when an element does not fit.</summary>
    private static bool TryWriteList(ListColumn<int> destination, int row, IntListLane list)
    {
        if (list.IsInt32)
        {
            destination.Set(row, list.Int32);
            return true;
        }

        if (!Int32Range.AllFit(list.Int64))
        {
            return false;
        }

        Int32Range.Narrow(list.Int64, destination.SetUninitialized(row, list.Length));
        return true;
    }

    /// <summary>The out-of-range rule for one row: the replacement (validated to fit) or missing.</summary>
    private static void Replace(ColumnBuffer destination, int row, in ScalarValue outOfRange)
    {
        if (outOfRange.IsMissing)
        {
            destination.SetMissing(row);
        }
        else
        {
            ColumnValues.Set(destination, row, outOfRange);
        }
    }

    /// <summary>False when a present value cannot be stored in int32 destination storage, or is a list of another length
    /// than a <c>list&lt;E,N&gt;</c> destination's; true for other destinations.</summary>
    private static bool Fits(in ScalarValue value, ColumnBuffer destination) => destination switch
    {
        ScalarColumn<int> => value.Kind == StorageKind.Int64 && Int32Range.Fits(value.AsInt64()),
        ListColumn<int> => value.ListElement == StorageKind.Int64 && ColumnValues.FitsLength(value.ListLength, destination.Type)
                                                                  && Int32Range.AllFit(value.AsInt64List()),
        ListColumn => value.Kind == StorageKind.List && ColumnValues.FitsLength(value.ListLength, destination.Type),
        _ => true,
    };

    /// <summary>Writes one request-level value to every listed row. A value that does not fit int32 storage, or a list of
    /// another length than a fixed-length destination's, is out of range for every row: each row takes
    /// <paramref name="outOfRange"/> and is counted.</summary>
    public static void Broadcast(in ScalarValue value, ReadOnlySpan<int> rows, ColumnBuffer destination, in ScalarValue outOfRange, ExpressionCounters? counters)
    {
        var write = value;
        if (!value.IsMissing && !Fits(value, destination))
        {
            counters?.AddOutOfRange(rows.Length);
            write = outOfRange;
        }

        if (write.IsMissing)
        {
            foreach (var row in rows)
            {
                destination.SetMissing(row);
            }

            return;
        }

        foreach (var row in rows)
        {
            switch (destination)
            {
                case ScalarColumn<double> d: d.Set(row, write.AsFloat64()); break;
                case ScalarColumn<float> d: d.Set(row, (float)write.AsFloat64()); break;
                case ScalarColumn<long> d: d.Set(row, write.AsInt64()); break;
                case ScalarColumn<int> d: d.Set(row, (int)write.AsInt64()); break; // checked by Fits above
                case ScalarColumn<bool> d: d.Set(row, write.AsBool()); break;
                case DictStringColumn d: d.Set(row, write.AsString()); break;
                case TextColumn d: d.Set(row, write.AsString()); break;
                case ListColumn<long> d: d.Set(row, write.AsInt64List()); break;
                case ListColumn<int> d:
                {
                    var longs = write.AsInt64List();
                    Int32Range.Narrow(longs, d.SetUninitialized(row, longs.Length)); // checked by Fits above
                    break;
                }

                case ListColumn<double> d: d.Set(row, write.AsFloat64List()); break;
                case ListColumn<float> d:
                {
                    var doubles = write.AsFloat64List();
                    ColumnValues.NarrowFloats(doubles, d.SetUninitialized(row, doubles.Length));
                    break;
                }

                case ListColumn<bool> d: d.Set(row, write.AsBoolList()); break;
                case ListColumn<string> d: d.Set(row, write.AsStringList()); break;
            }
        }
    }
}
