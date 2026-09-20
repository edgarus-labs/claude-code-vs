using ClaudeCode.Core.ViewModels.Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// <c>NullChatSessionServices</c> is not a mock - <c>ChatPanelView.xaml.cs</c> falls back to it in
/// production when no host-provided <c>IChatSessionServices</c> is available, so it has to honour the
/// interface contract rather than merely satisfy the compiler.
/// </summary>
public sealed class NullChatSessionServicesTests
{
    [Fact]
    public async Task OpenDocumentAsync_Faults_BecauseThereIsNoHostEditorToOpenIn()
    {
        // IChatSessionServices documents that the returned task carries the failure when the host
        // cannot open the path. This implementation has no host at all, so reporting success tells the
        // caller a file was shown to the user that never was.
        var services = new NullChatSessionServices();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.OpenDocumentAsync("C:/some/file.cs", null, CancellationToken.None));
    }
}
