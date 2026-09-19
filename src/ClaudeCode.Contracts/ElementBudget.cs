namespace ClaudeCode.Contracts;

/// <summary>
/// The node budget for one UI Automation tree walk, and the record of whether that walk actually
/// dropped anything. It lives in this shared assembly rather than next to its single caller in
/// <c>ClaudeCode.Vsix</c> because that assembly has no test project and the contract below is subtle
/// enough to be re-broken by an innocent-looking edit.
/// <para>
/// The contract: the caller pre-charges the root (<c>new ElementBudget(maxNodes - 1)</c>, because the
/// root is always emitted) and the *parent* calls <see cref="TryTake"/> before recursing into a child.
/// <see cref="Truncated"/> therefore means "a node was actually left out", which an exhausted budget
/// alone does not prove: a tree that fits in exactly <c>maxNodes</c> nodes ends at zero with nothing
/// dropped. A walk that stops for any other reason - the depth clamp - must say so with
/// <see cref="MarkTruncated"/>, otherwise the caller reports a complete tree that is missing controls.
/// </para>
/// </summary>
public sealed class ElementBudget
{
    private int _remaining;

    public ElementBudget(int nodes) => _remaining = nodes;

    /// <summary>True once at least one node has been left out of the walk.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Charges one node to the budget. Returns <c>false</c> - and records the truncation - when the
    /// budget is spent, which is the caller's signal to stop emitting children.
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
