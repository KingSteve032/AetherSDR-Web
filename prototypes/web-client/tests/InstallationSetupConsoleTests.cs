using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupConsoleTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ParserRemovesOnlyTheRequestedSetupCommand()
    {
        InstallationSetupConsoleCommandLine parsed =
            InstallationSetupConsoleCommandParser.Parse(
                [
                    "--urls",
                    "http://127.0.0.1:5080",
                    InstallationSetupConsoleCommandParser.StatusSwitch,
                    "--environment",
                    "Development"
                ]);

        Assert.Equal(
            InstallationSetupConsoleCommandKind.Status,
            parsed.Command);
        Assert.Equal(
            [
                "--urls",
                "http://127.0.0.1:5080",
                "--environment",
                "Development"
            ],
            parsed.ApplicationArguments);
    }

    [Fact]
    public void ParserRejectsDuplicateOrConflictingSetupCommands()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.StatusSwitch,
                    InstallationSetupConsoleCommandParser.StatusSwitch
                ]));
        Assert.Throws<InvalidOperationException>(
            () => InstallationSetupConsoleCommandParser.Parse(
                [
                    InstallationSetupConsoleCommandParser.StatusSwitch,
                    InstallationSetupConsoleCommandParser
                        .IssueBootstrapTokenSwitch
                ]));
    }

    [Fact]
    public async Task StatusCommandReportsProgressWithoutTokenMaterial()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokenService.IssueAsync(initial.Revision);
        InstallationSetupState configured = await store.UpdateAsync(
            issue.State.Revision,
            state => state with
            {
                Topology = InstallationTopologyKind.PersonalSingleStation,
                CanonicalPublicUrl = "https://radio.example.org",
                LastCompletedStep = InstallationSetupStep.PublicUrl
            });
        InstallationSetupConsole console = new(store, tokenService);
        using StringWriter output = new();

        await console.ExecuteAsync(
            InstallationSetupConsoleCommandKind.Status,
            output);

        string status = output.ToString();
        Assert.Contains("\"lockMode\": \"bootstrapRequired\"", status);
        Assert.Contains("\"bootstrapTokenPresent\": true", status);
        Assert.Contains("\"canonicalPublicUrlConfigured\": true", status);
        Assert.Contains($"\"revision\": {configured.Revision}", status);
        Assert.DoesNotContain(issue.Token, status);
        Assert.DoesNotContain(configured.Lock.BootstrapTokenHash, status);
        Assert.DoesNotContain("bootstrapTokenHash", status);
        Assert.DoesNotContain(configured.CanonicalPublicUrl, status);
    }

    [Fact]
    public async Task IssueCommandShowsTokenOnceAndPersistsOnlyItsHash()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationSetupConsole console = new(store, tokenService);
        using StringWriter output = new();

        await console.ExecuteAsync(
            InstallationSetupConsoleCommandKind.IssueBootstrapToken,
            output,
            interactiveTokenOutput: true);

        InstallationSetupState state = await store.LoadOrCreateAsync();
        string persisted = await File.ReadAllTextAsync(statePath);
        string text = output.ToString();
        string token = text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Token: ", StringComparison.Ordinal))
            ["Token: ".Length..];

        Assert.True(token.Length >= 32);
        Assert.Contains(
            $"Expires at: {Start.Add(InstallationBootstrapTokenService.DefaultLifetime):O}",
            text);
        Assert.Contains("shown once", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, persisted);
        Assert.NotEmpty(state.Lock.BootstrapTokenHash);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            state.Lock.Mode);
    }

    [Fact]
    public async Task IssueCommandRejectsNonInteractiveOutputWithoutChangingState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationSetupConsole console = new(store, tokenService);
        using StringWriter output = new();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => console.ExecuteAsync(
                    InstallationSetupConsoleCommandKind.IssueBootstrapToken,
                    output));

        Assert.Contains("interactive local terminal", exception.Message);
        Assert.False(File.Exists(statePath));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task ConsoleRejectsMissingCommandWithoutChangingState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationBootstrapTokenService tokenService = new(store, time);
        InstallationSetupConsole console = new(store, tokenService);
        using StringWriter output = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => console.ExecuteAsync(
                InstallationSetupConsoleCommandKind.None,
                output));

        Assert.False(File.Exists(statePath));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-setup-console-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
