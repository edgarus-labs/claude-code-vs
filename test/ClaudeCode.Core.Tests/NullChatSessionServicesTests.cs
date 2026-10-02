using ClaudeCode.Core.ViewModels.Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Covers <c>NullChatSessionServices</c>, the <c>IChatSessionServices</c> fallback used by
/// <c>ChatPanelView</c>, against the interface contract.
/// </summary>
public sealed class NullChatSessionServicesTests
{
    [Fact]
    public async Task OpenDocumentAsync_Faults_BecauseThereIsNoHostEditorToOpenIn()
    {
        var services = new NullChatSessionServices();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.OpenDocumentAsync("C:/some/file.cs", null, CancellationToken.None));
    }
}
