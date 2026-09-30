using Lager.Domain.Articles;
using Lager.Domain.Packing;

namespace Lager.Application.Packing;

/// <summary>
/// Packplan mit echter Geometrie statt Volumensumme: Artikel werden absteigend nach Einzelvolumen in Kartons
/// gelegt (First-Fit-Decreasing), und zwar als Blöcke gleicher Artikel in freien Quadern (Guillotine-Zerlegung
/// des Kartoninnenraums). Ein Block ist ein Raster aus Einheiten; jede Einheit passt in einer der erlaubten
/// Drehungen in den freien Quader, Blöcke überlappen nie und liegen immer im Karton.
///
/// Regeln:
/// - Drehung: alle 6 Orientierungen. Ist ein Artikel stapelbar (<see cref="StackingInfo.IsStackable"/>) und
///   <see cref="StackingInfo.MaxStackCount"/> &gt; 1, bleibt seine Stapelachse senkrecht (nur die Drehung um sie).
/// - Stapeln (Ineinanderstellen): jede weitere Einheit einer Säule braucht nur <see cref="StackingInfo.StackingIncrementMm"/>
///   auf der Stapelachse, höchstens <see cref="StackingInfo.MaxStackCount"/> Einheiten je Säule. Inkrement 0
///   heißt "kein Platzgewinn": die Einheit braucht ihre volle Kantenlänge.
/// - Gewicht: <see cref="CartonType.MaxWeightGrams"/> ist das Bruttogewicht, die Tara zählt mit.
/// - Karton: neue Kartons nehmen den kleinsten Typ, der den Rest der Gruppe in einem Block fasst, sonst den
///   Typ mit dem größten Block; passt eine Einheit in keinen Typ, landet sie in <see cref="PackingPlan.Unpacked"/>.
/// - Einheiten werden je Artikel aggregiert, nicht einzeln expandiert (Laufzeit unabhängig von der Menge).
/// Das Verfahren ist eine Heuristik: Es garantiert Machbarkeit (jede Platzierung ist geometrisch gültig),
/// aber nicht die minimale Kartonanzahl.
/// </summary>
public class FirstFitDecreasingPackingOptimizer : IPackingOptimizer
{
    /// <summary>Sicherheitsgrenze gegen unsinnig große Mengen: mehr Kartons werden nicht geplant.</summary>
    public const int MaxCartons = 10_000;

    private static readonly int[][] Permutations =
    {
        new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
        new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
    };

    public PackingPlan Plan(Guid orderId, string orderNumber, IEnumerable<PackInput> items, IReadOnlyList<CartonType> cartonTypes)
    {
        if (cartonTypes.Count == 0)
            throw new ArgumentException("Es wurden keine Kartontypen übergeben.", nameof(cartonTypes));

        var warnings = new List<string>();
        var types = cartonTypes
            .Where(t => t.InnerLengthMm > 0 && t.InnerWidthMm > 0 && t.InnerHeightMm > 0)
            .OrderBy(t => t.InnerVolumeMm3)
            .ThenBy(t => t.MaxWeightGrams)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var groups = BuildGroups(items);
        var open = new List<OpenCarton>();
        var unpacked = new List<PackedItem>();

        foreach (var group in groups)
        {
            var remaining = group.Quantity;

            if (!group.HasValidDimensions || types.Count == 0)
            {
                warnings.Add($"Artikel {group.Article.Sku}: Abmessungen fehlen oder sind ungültig, die Einheiten können nicht verpackt werden.");
                unpacked.Add(ToPackedItem(group, remaining));
                continue;
            }

            // 1. In offene Kartons legen (First-Fit). Ein Karton, der für diesen Artikel einmal nichts mehr
            //    aufnehmen konnte, kann es nie wieder (freier Platz und Restgewicht schrumpfen nur).
            var firstOpen = 0;
            while (remaining > 0)
            {
                var placed = 0L;
                for (var i = firstOpen; i < open.Count; i++)
                {
                    placed = open[i].TryPlace(group, remaining);
                    if (placed > 0) { firstOpen = i; break; }
                }
                if (placed > 0) { remaining -= placed; continue; }

                // 2. Neuer Karton.
                if (open.Count >= MaxCartons)
                {
                    warnings.Add($"Es wären mehr als {MaxCartons} Kartons nötig; die restlichen Einheiten von {group.Article.Sku} sind nicht verpackt.");
                    break;
                }

                var type = ChooseCartonType(group, remaining, types);
                if (type is null) break; // passt in keinen Kartontyp

                var carton = new OpenCarton(type);
                placed = carton.TryPlace(group, remaining);
                if (placed <= 0) break; // dürfte nach ChooseCartonType nicht vorkommen; schützt vor Endlosschleife
                open.Add(carton);
                firstOpen = open.Count - 1;
                remaining -= placed;
            }

            if (remaining > 0) unpacked.Add(ToPackedItem(group, remaining));
        }

        var cartons = open
            .Select((c, idx) => new PackedCarton(idx, c.Type, (int)Math.Min(c.WeightGrams, int.MaxValue), c.UsedVolumeMm3, c.ToPackedItems()))
            .ToList();

        return new PackingPlan(orderId, orderNumber, cartons, unpacked) { Warnings = warnings };
    }

    // ---------------------------------------------------------------- Gruppen (je Artikel aggregiert)

    private sealed class Group
    {
        public required Article Article { get; init; }
        public long Quantity { get; set; }
        public bool HasValidDimensions { get; init; }
        public int WeightGrams { get; init; }
        public int MaxStack { get; init; }
        public int Increment { get; init; }
        public required Orientation[] Orientations { get; init; }
        public long UnitVolumeMm3 { get; init; }
    }

    /// <summary>Kantenlängen einer Einheit im Karton (x, y, z); bei stapelbaren Artikeln ist z die Stapelachse.</summary>
    private readonly record struct Orientation(int X, int Y, int Z);

    private static List<Group> BuildGroups(IEnumerable<PackInput> items)
    {
        var byArticle = new Dictionary<Guid, Group>();
        foreach (var item in items)
        {
            if (item.Quantity < 0)
                throw new ArgumentOutOfRangeException(nameof(items), item.Quantity, "Die Menge eines Artikels darf nicht negativ sein.");
            if (item.Quantity == 0) continue;

            if (byArticle.TryGetValue(item.Article.Id, out var existing))
            {
                existing.Quantity += item.Quantity;
                continue;
            }

            var a = item.Article;
            var dims = new[] { a.Dimensions.LengthMm, a.Dimensions.WidthMm, a.Dimensions.HeightMm };
            var stackDim = (int)a.Stacking.StackingAxis switch { 0 => 0, 1 => 1, _ => 2 };
            var maxStack = a.Stacking.IsStackable ? Math.Max(1, a.Stacking.MaxStackCount ?? int.MaxValue) : 1;
            var valid = dims.All(d => d > 0);

            byArticle[a.Id] = new Group
            {
                Article = a,
                Quantity = item.Quantity,
                HasValidDimensions = valid,
                WeightGrams = Math.Max(0, a.WeightGrams),
                MaxStack = maxStack,
                // Inkrement 0 (oder negativ) = kein Platzgewinn beim Stapeln: volle Kantenlänge.
                Increment = a.Stacking.StackingIncrementMm > 0 ? a.Stacking.StackingIncrementMm : dims[stackDim],
                Orientations = valid ? BuildOrientations(dims, stackDim, maxStack > 1) : Array.Empty<Orientation>(),
                UnitVolumeMm3 = a.Dimensions.VolumeMm3,
            };
        }

        return byArticle.Values
            .OrderByDescending(g => g.UnitVolumeMm3)
            .ThenBy(g => g.Article.Sku, StringComparer.Ordinal)
            .ThenBy(g => g.Article.Id)
            .ToList();
    }

    private static Orientation[] BuildOrientations(int[] dims, int stackDim, bool keepStackAxisUpright)
    {
        var list = new List<Orientation>();
        foreach (var p in Permutations)
        {
            if (keepStackAxisUpright && p[2] != stackDim) continue;
            var o = new Orientation(dims[p[0]], dims[p[1]], dims[p[2]]);
            if (!list.Contains(o)) list.Add(o);
        }
        return list.ToArray();
    }

    private static PackedItem ToPackedItem(Group g, long quantity) =>
        new(g.Article.Id, g.Article.Sku, (int)Math.Min(quantity, int.MaxValue));

    // ---------------------------------------------------------------- Kartonwahl

    /// <summary>
    /// Kleinster Typ, der den Rest der Gruppe in einem Block aufnimmt; sonst der mit dem größten Block
    /// (bei Gleichstand der kleinere); null, wenn keine einzige Einheit in irgendeinen Typ passt.
    /// </summary>
    private static CartonType? ChooseCartonType(Group group, long remaining, IReadOnlyList<CartonType> types)
    {
        CartonType? best = null;
        long bestUnits = 0;
        foreach (var type in types)
        {
            var carton = new OpenCarton(type);
            var units = carton.BestBlock(group, remaining)?.Units ?? 0;
            if (units >= remaining) return type;
            if (units > bestUnits) { best = type; bestUnits = units; }
        }
        return best;
    }

    // ---------------------------------------------------------------- Geometrie

    /// <summary>Freier Quader im Karton (nur Kantenlängen; die Lage ergibt sich aus der Zerlegung).</summary>
    private readonly record struct Cuboid(int X, int Y, int Z)
    {
        public long Volume => (long)X * Y * Z;
    }

    /// <summary>Ein Raster gleicher Einheiten, das im freien Quader an dessen Ursprung liegt.</summary>
    private sealed record Block(long Units, int X, int Y, int Z, int FreeIndex)
    {
        public long Volume => (long)X * Y * Z;
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;

    /// <summary>
    /// Bestes Raster für bis zu <paramref name="limit"/> Einheiten in Orientierung <paramref name="o"/> im
    /// Quader <paramref name="f"/>; null, wenn nicht einmal eine Einheit passt. Entlang z stehen Säulen aus
    /// bis zu MaxStack ineinandergestellten Einheiten (Inkrement je weiterer Einheit), mehrere Säulen übereinander.
    /// Bei weniger Einheiten als das volle Raster wird die kleinste umschließende Teilbox gewählt.
    /// </summary>
    private static Block? Arrange(Cuboid f, Orientation o, Group g, long limit, int freeIndex)
    {
        if (o.X > f.X || o.Y > f.Y || o.Z > f.Z) return null;

        long columnUnits = 1;
        long columnLength = o.Z;
        if (g.MaxStack > 1)
        {
            columnUnits = Math.Min(g.MaxStack, 1 + (f.Z - o.Z) / (long)g.Increment);
            columnLength = o.Z + (columnUnits - 1) * g.Increment;
        }

        long columns = f.Z / columnLength;          // Säulen übereinander, >= 1
        long gx = f.X / o.X;
        long gy = f.Y / o.Y;
        long perCell = columns * columnUnits;       // Einheiten je (x,y)-Zelle
        long capacity = gx * gy * perCell;
        var units = Math.Min(limit, capacity);
        if (units <= 0) return null;

        long LengthZ(long need) =>
            need / columnUnits * columnLength + (need % columnUnits > 0 ? o.Z + (need % columnUnits - 1) * g.Increment : 0);

        long bx, by, bz;
        if (units == capacity)
        {
            bx = gx * o.X; by = gy * o.Y; bz = columns * columnLength;
        }
        else if (units <= perCell)
        {
            bx = o.X; by = o.Y; bz = LengthZ(units);
        }
        else
        {
            long bestVolume = long.MaxValue;
            bx = by = bz = 0;
            for (long ax = 1; ax <= gx; ax++)
            {
                for (var ay = CeilDiv(units, ax * perCell); ay <= gy; ay++)
                {
                    var lz = LengthZ(CeilDiv(units, ax * ay));
                    var volume = ax * o.X * (ay * o.Y) * lz;
                    if (volume >= bestVolume) continue;
                    bestVolume = volume;
                    bx = ax * o.X; by = ay * o.Y; bz = lz;
                }
            }
        }

        return new Block(units, (int)bx, (int)by, (int)bz, freeIndex);
    }

    /// <summary>
    /// Guillotine-Zerlegung des Rests nach dem Platzieren eines Blocks im Quader: die Achse mit dem größten
    /// Rest bekommt die Platte über den vollen Querschnitt, die beiden anderen Stücke sind lückenlos angelegt.
    /// Die Stücke überlappen nie und decken genau (Quader minus Block) ab.
    /// </summary>
    private static IEnumerable<Cuboid> Split(Cuboid f, Block b)
    {
        int[] full = { f.X, f.Y, f.Z };
        int[] used = { b.X, b.Y, b.Z };
        int[] rest = { f.X - b.X, f.Y - b.Y, f.Z - b.Z };
        var axes = new[] { 0, 1, 2 }.OrderByDescending(a => rest[a]).ThenBy(a => a).ToArray();
        int a1 = axes[0], a2 = axes[1], a3 = axes[2];

        var pieces = new[]
        {
            Make(a1, rest[a1], a2, full[a2], a3, full[a3]),
            Make(a1, used[a1], a2, rest[a2], a3, full[a3]),
            Make(a1, used[a1], a2, used[a2], a3, rest[a3]),
        };
        return pieces.Where(p => p.X > 0 && p.Y > 0 && p.Z > 0);

        static Cuboid Make(int axisA, int lenA, int axisB, int lenB, int axisC, int lenC)
        {
            var v = new int[3];
            v[axisA] = lenA; v[axisB] = lenB; v[axisC] = lenC;
            return new Cuboid(v[0], v[1], v[2]);
        }
    }

    // ---------------------------------------------------------------- Karton

    private sealed class OpenCarton
    {
        private readonly List<Cuboid> _free = new();
        private readonly Dictionary<Guid, (string Sku, long Count)> _items = new();
        private readonly List<Guid> _itemOrder = new();

        public OpenCarton(CartonType type)
        {
            Type = type;
            WeightGrams = type.TareWeightGrams;
            _free.Add(new Cuboid(type.InnerLengthMm, type.InnerWidthMm, type.InnerHeightMm));
        }

        public CartonType Type { get; }

        /// <summary>Bruttogewicht inklusive Tara.</summary>
        public long WeightGrams { get; private set; }

        public long UsedVolumeMm3 { get; private set; }

        /// <summary>Größtes platzierbares Raster für bis zu <paramref name="remaining"/> Einheiten (ohne zu platzieren).</summary>
        public Block? BestBlock(Group g, long remaining)
        {
            var room = Type.MaxWeightGrams - WeightGrams;
            if (room < 0) return null;
            var byWeight = g.WeightGrams == 0 ? long.MaxValue : room / g.WeightGrams;
            var limit = Math.Min(remaining, byWeight);
            if (limit <= 0) return null;

            Block? best = null;
            var bestFreeVolume = 0L;
            for (var fi = 0; fi < _free.Count; fi++)
            {
                var f = _free[fi];
                foreach (var o in g.Orientations)
                {
                    var block = Arrange(f, o, g, limit, fi);
                    if (block is null) continue;

                    // Mehr Einheiten zuerst, dann der kleinste freie Quader (Best-Fit), dann das kleinere Raster.
                    if (best is null
                        || block.Units > best.Units
                        || (block.Units == best.Units && (f.Volume < bestFreeVolume
                            || (f.Volume == bestFreeVolume && block.Volume < best.Volume))))
                    {
                        best = block;
                        bestFreeVolume = f.Volume;
                    }
                }
            }
            return best;
        }

        /// <summary>Legt so viele Einheiten wie möglich als einen Block in den Karton; liefert deren Anzahl (0 = passt nicht mehr).</summary>
        public long TryPlace(Group g, long remaining)
        {
            var block = BestBlock(g, remaining);
            if (block is null) return 0;

            var f = _free[block.FreeIndex];
            _free.RemoveAt(block.FreeIndex);
            _free.AddRange(Split(f, block));

            WeightGrams += block.Units * g.WeightGrams;
            UsedVolumeMm3 += block.Volume;
            if (_items.TryGetValue(g.Article.Id, out var existing))
                _items[g.Article.Id] = (existing.Sku, existing.Count + block.Units);
            else
            {
                _items[g.Article.Id] = (g.Article.Sku, block.Units);
                _itemOrder.Add(g.Article.Id);
            }
            return block.Units;
        }

        public IReadOnlyList<PackedItem> ToPackedItems() =>
            _itemOrder
                .Select(id => new PackedItem(id, _items[id].Sku, (int)Math.Min(_items[id].Count, int.MaxValue)))
                .ToList();
    }
}
