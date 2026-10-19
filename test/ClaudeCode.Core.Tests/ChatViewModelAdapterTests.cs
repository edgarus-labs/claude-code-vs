using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatViewModelAdapterTests
{
    [Fact]
    public async Task InitializeAsync_WhenAdapterMissing_SetsIsAdapterMissingTrueAndDoesNotConnect()
    {
        var connection = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(connection);
        int connectCalls = 0;
        factory.ConnectHandler = ct => { connectCalls++; return Task.FromResult<IAcpAgentConnection>(connection); };

        var installer = new FakeAcpAdapterInstaller { CurrentReadiness = AdapterReadiness.MissingAdapter };
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService())
        {
            AdapterInstaller = installer
        };

        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();

        Assert.True(vm.IsAdapterMissing);
        Assert.False(vm.IsNodeMissing);
        Assert.False(vm.IsConnecting);
        Assert.Equal(0, connectCalls);
    }

    [Fact]
    public async Task InitializeAsync_WhenNodeMissing_SetsIsNodeMissingTrueAndDoesNotConnect()
    {
        var connection = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(connection);
        int connectCalls = 0;
        factory.ConnectHandler = ct => { connectCalls++; return Task.FromResult<IAcpAgentConnection>(connection); };

        var installer = new FakeAcpAdapterInstaller { CurrentReadiness = AdapterReadiness.MissingNodeOrNpm };
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService())
        {
            AdapterInstaller = installer
        };

        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();

        Assert.True(vm.IsNodeMissing);
        Assert.False(vm.IsAdapterMissing);
        Assert.False(vm.IsConnecting);
        Assert.Equal(0, connectCalls);
    }

    [Fact]
    public async Task InstallAdapterCommand_WhenSuccessful_InstallsAndInitializesConnection()
    {
        var connection = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(connection);
        FakeAcpAdapterInstaller installer = null!;
        installer = new FakeAcpAdapterInstaller
        {
            CurrentReadiness = AdapterReadiness.MissingAdapter,
            InstallHandler = (progress, ct) =>
            {
                installer.CurrentReadiness = AdapterReadiness.Ready;

                return Task.FromResult(true);
            }
        };

        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService())
        {
            AdapterInstaller = installer
        };

        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();
        Assert.True(vm.IsAdapterMissing);

        await vm.InstallAdapterCommand.ExecuteAsync(null);

        Assert.Equal(1, installer.InstallCount);
        Assert.False(vm.IsAdapterMissing);
        Assert.False(vm.IsInstallingAdapter);
        Assert.True(connection.IsInitialized);
    }

    [Fact]
    public async Task InstallAdapterCommand_WhenFails_SetsStatusMessageAndRemainsMissing()
    {
        var connection = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(connection);
        var installer = new FakeAcpAdapterInstaller
        {
            CurrentReadiness = AdapterReadiness.MissingAdapter,
            InstallHandler = (progress, ct) => Task.FromResult(false)
        };

        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService())
        {
            AdapterInstaller = installer
        };

        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();

        await vm.InstallAdapterCommand.ExecuteAsync(null);

        Assert.Equal(1, installer.InstallCount);
        Assert.True(vm.IsAdapterMissing);
        Assert.False(vm.IsInstallingAdapter);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyNpmInstallCommand_CopiesCommandToClipboard()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        using var vm = new ChatViewModel(services);

        vm.CopyNpmInstallCommand.Execute(null);

        Assert.Equal("npm install -g @agentclientprotocol/claude-agent-acp", services.LastCopiedText);
        Assert.Contains("npm install", vm.StatusMessage);
    }

    [Fact]
    public void CopyWingetCommand_CopiesCommandToClipboard()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        using var vm = new ChatViewModel(services);

        vm.CopyWingetCommand.Execute(null);

        Assert.Equal("winget install OpenJS.NodeJS", services.LastCopiedText);
        Assert.Contains("winget install", vm.StatusMessage);
    }

    [Fact]
    public async Task CheckAgainCommand_RechecksReadinessAndInitializes()
    {
        var connection = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(connection);
        var installer = new FakeAcpAdapterInstaller { CurrentReadiness = AdapterReadiness.MissingAdapter };
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService())
        {
            AdapterInstaller = installer
        };

        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();
        Assert.True(vm.IsAdapterMissing);

        installer.CurrentReadiness = AdapterReadiness.Ready;
        await vm.CheckAgainCommand.ExecuteAsync(null);

        Assert.False(vm.IsAdapterMissing);
        Assert.True(connection.IsInitialized);
    }
}
