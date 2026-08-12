namespace AetherSDR.Web.Setup;

public sealed record InstallationSetupPreflightReport(
    int SchemaVersion,
    long StateRevision,
    DateTimeOffset GeneratedAt,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl,
    InstallationUpdateChannel UpdateChannel,
    string PinnedRelease,
    bool InstallTransmitSupport,
    bool ReadyForInstallerReview,
    IReadOnlyList<string> PlannedUsers,
    IReadOnlyList<string> PlannedPackages,
    IReadOnlyList<string> PlannedPorts,
    IReadOnlyList<string> PlannedFiles,
    IReadOnlyList<string> PlannedServices,
    IReadOnlyList<string> PlannedProxyChanges,
    IReadOnlyList<string> FirewallExpectations,
    IReadOnlyList<string> PlannedMigrations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? PostInstallOperationalChecks = null);

public sealed class InstallationSetupPreflight
{
    private readonly InstallationSetupStore m_store;
    private readonly TimeProvider m_timeProvider;

    public InstallationSetupPreflight(
        InstallationSetupStore store,
        TimeProvider? timeProvider = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InstallationSetupPreflightReport> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        InstallationSetupState state =
            await m_store.LoadAsync(cancellationToken);
        if (state.Lock.Mode != InstallationSetupLockMode.Claimed)
        {
            throw new InvalidOperationException(
                "Installation preflight requires a claimed first-run lock.");
        }
        if (state.LastCompletedStep < InstallationSetupStep.TransmitSupport)
        {
            throw new InvalidOperationException(
                "Installation preflight requires topology, public URL, paths, " +
                "update channel, backup, and transmit-support choices.");
        }

        InstallationTopologyKind topology = state.Topology ??
            throw new InvalidOperationException(
                "Installation preflight requires a topology.");
        InstallationTopologyProfile profile =
            InstallationTopologyProfile.For(topology);
        InstallationPaths paths = state.Paths ??
            throw new InvalidOperationException(
                "Installation preflight requires installation paths.");
        CanonicalPublicUrl publicUrl =
            CanonicalPublicUrl.Parse(state.CanonicalPublicUrl);

        List<string> users = [];
        List<string> packages = [];
        List<string> ports = [];
        List<string> services = [];
        List<string> proxyChanges = [];
        List<string> firewall = [];
        List<string> warnings = [];

        if (profile.GatewayRunsHere)
        {
            users.Add("aethersdr (dedicated gateway service user)");
            packages.Add("AetherSDR.Web gateway package");
            ports.Add("TCP 5080 on loopback for the web gateway");
            services.Add("aethersdr-web.service");
            proxyChanges.Add(
                $"Terminate HTTPS for {publicUrl.Value} and forward browser HTTP " +
                "and WebSocket traffic to loopback TCP 5080.");
            firewall.Add(
                "Allow public inbound TCP 443 only after TLS and proxy validation; " +
                "do not expose TCP 5080.");
        }
        else
        {
            proxyChanges.Add(
                "No reverse-proxy change is planned on this remote station node.");
        }

        if (profile.BrokerRunsHere)
        {
            AddAetherRemoteUser(users);
            packages.Add("AetherRemote.Broker package");
            ports.Add("TCP 5090 on loopback for the station broker");
            services.Add("aetherremote-broker.service");
            proxyChanges.Add(
                "Forward the station broker WebSocket path to loopback TCP 5090.");
            firewall.Add("Do not expose TCP 5090 directly.");
        }

        if (profile.StationEngineRunsHere && profile.AgentRunsHere)
        {
            AddAetherRemoteUser(users);
            packages.Add("AetherSDR.Web station-engine package");
            ports.Add("TCP 5081 on loopback for the station engine");
            services.Add("aetherremote-station-engine.service");
        }
        if (profile.StationEngineRunsHere)
        {
            firewall.Add(
                "Permit only the station-local FLEX discovery and radio traffic " +
                "required on the operator-approved radio LAN.");
        }

        if (profile.AgentRunsHere)
        {
            AddAetherRemoteUser(users);
            packages.Add("AetherRemote.Agent package");
            ports.Add(
                $"Outbound TCP 443 from the station agent to {publicUrl.Value}");
            services.Add("aetherremote-agent.service");
            firewall.Add(
                "Permit outbound HTTPS to the canonical gateway; no inbound agent " +
                "listener is planned.");
        }

        if (state.InstallTransmitSupport)
        {
            packages.Add(
                "AetherSDR.TxWatchdog package installed disabled and unarmed");
            warnings.Add(
                "Installing transmit support does not enable TX, grant a radio " +
                "policy, arm a watchdog, or create browser authority.");
        }

        warnings.Add(
            "Reverse-proxy ownership and Caddy versus existing-proxy selection " +
            "remain an M8C installer decision.");

        string[] files =
        [
            paths.ConfigurationFilePath,
            paths.SetupStatePath,
            paths.DataProtectionKeyDirectory,
            paths.RadioAccessPolicyPath,
            paths.AdministrativeAuditPath,
            paths.ReleaseDirectory,
            paths.BackupDirectory,
            paths.LogDirectory
        ];
        string[] migrations =
        [
            $"Validate setup schema version {state.SchemaVersion}.",
            "Stage configuration migration against a copy before activation.",
            "Apply no migration, package installation, service change, or firewall " +
            "change during this preflight."
        ];
        List<string> postInstallChecks =
        [
            "Validate the canonical public URL, TLS trust chain, and certificate expiry.",
            "Validate reverse-proxy forwarding and required browser security headers.",
            "Reach the protected browser WebSocket authentication boundary at /ws/radio.",
            "Validate the configured authentication callback or local-authentication readiness.",
            "Confirm FLEX discovery and radio health without sending a radio command.",
            "Confirm per-radio TX prerequisites without acquiring a TX lease or keying the radio.",
            "Confirm encrypted-backup readiness and backup-age objective.",
            "Confirm the active immutable release is ready for signed update.",
            "Confirm at least one retained immutable release is available for rollback."
        ];
        if (profile.AcceptsRemoteStations)
        {
            postInstallChecks.Add(
                "Reach the protected station WebSocket broker boundary through /aetherremote/broker and validate signed AetherRemote compatibility.");
        }

        return new InstallationSetupPreflightReport(
            state.SchemaVersion,
            state.Revision,
            m_timeProvider.GetUtcNow(),
            topology,
            publicUrl.Value,
            state.UpdateChannel,
            state.PinnedRelease,
            state.InstallTransmitSupport,
            ReadyForInstallerReview: true,
            users.AsReadOnly(),
            packages.AsReadOnly(),
            ports.AsReadOnly(),
            files,
            services.AsReadOnly(),
            proxyChanges.AsReadOnly(),
            firewall.AsReadOnly(),
            migrations,
            warnings.AsReadOnly(),
            postInstallChecks.AsReadOnly());
    }

    private static void AddAetherRemoteUser(List<string> users)
    {
        const string value =
            "aetherremote (dedicated broker, agent, and station-engine service user)";
        if (!users.Contains(value, StringComparer.Ordinal))
        {
            users.Add(value);
        }
    }
}
