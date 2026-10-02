using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>The user's answer to an <see cref="ElicitationRequestEventArgs"/>. A field key absent
/// from <see cref="Content"/> means the user left that field blank.</summary>
public sealed class ElicitationAnswer
{
    /// <summary>
    /// Initializes a new instance of the ElicitationAnswer class with the specified elicitation action and associated content dictionary.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="content">The content.</param>
    public ElicitationAnswer(ElicitationAction action, IReadOnlyDictionary<string, IReadOnlyList<string>> content)
    {
        Action = action;
        Content = content;
    }

    /// <summary>
    /// Gets the action.
    /// </summary>
    public ElicitationAction Action { get; }

    /// <summary>
    /// Gets the content.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Content { get; }
}
