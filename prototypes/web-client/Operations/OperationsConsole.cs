using System.Diagnostics;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Operations;

public enum OperationsConsoleCommandKind
{
    None = 0,
    CreateBackup = 1,
    InspectBackup = 2,
    RestoreBackup = 3
}

public sealed record OperationsConsoleCommandLine(
    OperationsConsoleCommandKind Command,
    string BackupPath,
    IReadOnlyList<string> ApplicationArguments)
{
    public static OperationsConsoleCommandLine None(
        IReadOnlyList<string> arguments) =>
        new(OperationsConsoleCommandKind.None, string.Empty, arguments);
}

public static class OperationsConsoleCommandParser
{
    public const string CreateBackupSwitch = "--create-encrypted-backup";
    public const string InspectBackupSwitch = "--inspect-encrypted-backup";
    public const string RestoreBackupSwitch = "--restore-encrypted-backup";
    public const string BackupPathSwitch = "--backup-file";

    public static OperationsConsoleCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        OperationsConsoleCommandKind command = OperationsConsoleCommandKind.None;
        string backupPath = string.Empty;
        List<string> applicationArguments = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, CreateBackupSwitch, StringComparison.Ordinal))
            {
                SetCommand(ref command, OperationsConsoleCommandKind.CreateBackup);
                continue;
            }
            if (string.Equals(argument, InspectBackupSwitch, StringComparison.Ordinal))
            {
                SetCommand(ref command, OperationsConsoleCommandKind.InspectBackup);
                continue;
            }
            if (string.Equals(argument, RestoreBackupSwitch, StringComparison.Ordinal))
            {
                SetCommand(ref command, OperationsConsoleCommandKind.RestoreBackup);
                continue;
            }
            if (string.Equals(argument, BackupPathSwitch, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(backupPath) || index + 1 >= arguments.Count)
                {
                    throw new InvalidOperationException(
                        "The backup file option requires one exact absolute path.");
                }
                backupPath = CanonicalBackupPath(arguments[++index]);
                continue;
            }
            applicationArguments.Add(argument);
        }

        if (command == OperationsConsoleCommandKind.None)
        {
            if (!string.IsNullOrEmpty(backupPath))
            {
                throw new InvalidOperationException(
                    "The backup file option requires an encrypted backup command.");
            }
            return OperationsConsoleCommandLine.None(applicationArguments);
        }
        if (command == OperationsConsoleCommandKind.CreateBackup)
        {
            if (!string.IsNullOrEmpty(backupPath))
            {
                throw new InvalidOperationException(
                    "Backup creation uses the configured backup directory and does not accept an output path.");
            }
        }
        else if (string.IsNullOrEmpty(backupPath))
        {
            throw new InvalidOperationException(
                "Inspect and restore commands require --backup-file with one absolute .aebak path.");
        }
        return new OperationsConsoleCommandLine(
            command,
            backupPath,
            applicationArguments);
    }

    private static void SetCommand(
        ref OperationsConsoleCommandKind current,
        OperationsConsoleCommandKind candidate)
    {
        if (current != OperationsConsoleCommandKind.None)
        {
            throw new InvalidOperationException(
                "Only one encrypted backup command may run at a time.");
        }
        current = candidate;
    }

    private static string CanonicalBackupPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                "The backup file path must be absolute.");
        }
        string canonical = Path.GetFullPath(value);
        if (!canonical.EndsWith(
                InstallationBackupService.BackupExtension,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The backup file must use the {InstallationBackupService.BackupExtension} extension.");
        }
        return canonical;
    }
}

public static class OperationsConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> ExecuteAsync(
        OperationsConsoleCommandLine commandLine,
        InstallationPaths paths,
        TimeProvider? timeProvider = null,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(paths);
        output ??= Console.Out;
        if (commandLine.Command == OperationsConsoleCommandKind.None)
        {
            return 0;
        }
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Encrypted backup passphrases must be entered at an interactive local terminal.");
        }
        InstallationSetupStore setupStore = new(paths.SetupStatePath, timeProvider);
        ReleaseInstallationStatusReader statusReader = new(setupStore, paths);
        InstallationBackupService backup = new(
            paths,
            setupStore,
            statusReader,
            timeProvider);

        if ((commandLine.Command is OperationsConsoleCommandKind.CreateBackup or
                OperationsConsoleCommandKind.RestoreBackup) &&
            IsSystemInstallation(paths))
        {
            await EnsureOfflineMaintenanceWindowAsync(cancellationToken);
        }

        string passphrase;
        switch (commandLine.Command)
        {
            case OperationsConsoleCommandKind.CreateBackup:
                passphrase = await ReadPassphraseAsync(
                    "Backup passphrase: ",
                    cancellationToken);
                string confirmation = await ReadPassphraseAsync(
                    "Confirm backup passphrase: ",
                    cancellationToken);
                if (!string.Equals(passphrase, confirmation, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Backup passphrase confirmation did not match.");
                }
                (string backupPath, InstallationBackupSummary summary) =
                    await backup.CreateAsync(passphrase, cancellationToken);
                await WriteJsonAsync(
                    output,
                    new
                    {
                        command = "create-encrypted-backup",
                        succeeded = true,
                        backupPath,
                        summary
                    });
                return 0;
            case OperationsConsoleCommandKind.InspectBackup:
                passphrase = await ReadPassphraseAsync(
                    "Backup passphrase: ",
                    cancellationToken);
                InstallationBackupSummary inspected = await backup.InspectAsync(
                    commandLine.BackupPath,
                    passphrase,
                    cancellationToken);
                await WriteJsonAsync(
                    output,
                    new
                    {
                        command = "inspect-encrypted-backup",
                        succeeded = true,
                        summary = inspected
                    });
                return 0;
            case OperationsConsoleCommandKind.RestoreBackup:
                passphrase = await ReadPassphraseAsync(
                    "Backup passphrase: ",
                    cancellationToken);
                InstallationRestoreSummary restored = await backup.RestoreAsync(
                    commandLine.BackupPath,
                    passphrase,
                    cancellationToken);
                await WriteJsonAsync(
                    output,
                    new
                    {
                        command = "restore-encrypted-backup",
                        succeeded = true,
                        summary = restored
                    });
                return 0;
            default:
                throw new InvalidOperationException(
                    "The encrypted backup command is unsupported.");
        }
    }

    private static bool IsSystemInstallation(InstallationPaths paths) =>
        OperatingSystem.IsLinux() &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(paths.StateDirectory),
            "/var/lib/aethersdr",
            StringComparison.Ordinal);

    private static async Task EnsureOfflineMaintenanceWindowAsync(
        CancellationToken cancellationToken)
    {
        const string systemctl = "/usr/bin/systemctl";
        string[] units =
        [
            "aethersdr-web.service",
            "aethersdr-release-updater.service",
            "aetherremote-broker.service",
            "aetherremote-station-engine.service",
            "aetherremote-agent.service",
            "aetherremote-release-updater.service"
        ];
        if (!File.Exists(systemctl))
        {
            throw new InvalidOperationException(
                "Production backup/restore requires systemctl to prove an offline maintenance window.");
        }
        foreach (string unit in units)
        {
            ProcessStartInfo start = new()
            {
                FileName = systemctl,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Environment.Clear();
            start.ArgumentList.Add("is-active");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(unit);
            using Process process = new() { StartInfo = start };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "The fixed service-state inspection could not start.");
            }
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string state = stdout.Trim();
            if (state is "active" or "activating" or "reloading" or "deactivating")
            {
                throw new InvalidOperationException(
                    $"Encrypted backup/restore requires an offline maintenance window; {unit} is {state}.");
            }
            if (state is not ("inactive" or "failed" or "unknown"))
            {
                throw new InvalidOperationException(
                    $"Encrypted backup/restore could not prove that {unit} is inactive.");
            }
        }
    }

    private static async ValueTask<string> ReadPassphraseAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        string value = await InstallationSetupConsoleSecretReader.ReadAsync(
            cancellationToken);
        Console.WriteLine();
        if (value.Length < InstallationBackupService.MinimumPassphraseLength ||
            value.Length > InstallationBackupService.MaximumPassphraseLength)
        {
            throw new InvalidOperationException(
                $"Backup passphrase must contain {InstallationBackupService.MinimumPassphraseLength}-{InstallationBackupService.MaximumPassphraseLength} characters.");
        }
        return value;
    }

    private static Task WriteJsonAsync(TextWriter output, object value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
}
