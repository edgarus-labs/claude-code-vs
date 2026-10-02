namespace ClaudeCode.Contracts;

/// <summary>
/// The node budget for one UI Automation tree walk, and the record of whether that walk left any
/// node out. The caller pre-charges the root and calls <see cref="TryTake"/> before recursing into
/// each child.
/// </summary>
public sealed class ElementBudget
{
    private int _remaining;

    public ElementBudget(int nodes) => _remaining = nodes;

    /// <summary>True once at least one node has been left out of the walk.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Charges one node to the budget. Returns <c>false</c> and records the truncation when the
    /// budget is spent.
    /// </summary>
    public bool TryTake()
    {
        if (_remaining <= 0)
        {
            Truncated = true;
            return false;
        }

        _remaining--;
        return true;
    }

    /// <summary>
    /// Records that the walk left a node out for a reason other than the node budget, without
    /// charging the budget for a node that was never emitted.
    /// </summary>
    public void MarkTruncated() => Truncated = true;
}
