using Elwood.Core.Abstractions;

namespace Elwood.Core.Evaluation;

/// <summary>
/// The contents of an array arranged for membership tests, so that <c>x.in(list)</c> asked
/// once per row of another collection costs a lookup rather than a scan of the list.
/// Answers exactly what comparing against each element in turn would answer.
/// </summary>
internal sealed class MembershipIndex
{
    // Numbers are equal within this distance, as in Evaluator.ValuesEqual.
    private const double NumberTolerance = 1e-10;

    private readonly HashSet<string> _strings = new(StringComparer.Ordinal);
    private readonly double[] _numbers;
    private readonly HashSet<string>? _composites;
    private readonly bool _hasNull;
    private readonly bool _hasTrue;
    private readonly bool _hasFalse;

    public MembershipIndex(IEnumerable<IElwoodValue> items)
    {
        var numbers = new List<double>();
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case ElwoodValueKind.String:
                    _strings.Add(item.GetStringValue() ?? "");
                    break;
                case ElwoodValueKind.Number:
                    numbers.Add(item.GetNumberValue());
                    break;
                case ElwoodValueKind.Boolean:
                    if (item.GetBooleanValue()) _hasTrue = true; else _hasFalse = true;
                    break;
                case ElwoodValueKind.Null:
                    _hasNull = true;
                    break;
                default:
                    // Arrays and objects compare by serialized form; the two never collide.
                    (_composites ??= new(StringComparer.Ordinal)).Add(Evaluator.Serialize(item));
                    break;
            }
        }
        numbers.Sort();
        _numbers = numbers.ToArray();
    }

    public bool Contains(IElwoodValue target) => target.Kind switch
    {
        ElwoodValueKind.String => _strings.Contains(target.GetStringValue() ?? ""),
        ElwoodValueKind.Number => ContainsNumber(target.GetNumberValue()),
        ElwoodValueKind.Boolean => target.GetBooleanValue() ? _hasTrue : _hasFalse,
        ElwoodValueKind.Null => _hasNull,
        _ => _composites is not null && _composites.Contains(Evaluator.Serialize(target))
    };

    private bool ContainsNumber(double target)
    {
        // Narrow to the neighbourhood by binary search, then apply the same test a scan would.
        // The window is wider than the tolerance so rounding at its edges cannot exclude a match.
        var from = target - 2 * NumberTolerance;
        var to = target + 2 * NumberTolerance;

        int lo = 0, hi = _numbers.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_numbers[mid] < from) lo = mid + 1; else hi = mid;
        }

        for (var i = lo; i < _numbers.Length && _numbers[i] <= to; i++)
            if (Math.Abs(_numbers[i] - target) < NumberTolerance)
                return true;
        return false;
    }
}
