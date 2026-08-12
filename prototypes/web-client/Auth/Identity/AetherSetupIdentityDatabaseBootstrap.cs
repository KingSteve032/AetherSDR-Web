using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherSetupIdentityDatabaseBootstrap
{
    internal static async Task<AetherIdentityDatabaseReport>
        EnsureInitializedAsync(
            InstallationPaths paths,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);

        AetherIdentityDatabaseReport plan =
            await AetherIdentityDatabaseMigration.PlanAsync(
                paths,
                cancellationToken);
        if (IsConverged(plan))
        {
            return plan;
        }
        if (!string.Equals(plan.Outcome, "planned", StringComparison.Ordinal) ||
            !string.Equals(
                plan.Code,
                "identity-schema-initialization-required",
                StringComparison.Ordinal) ||
            !plan.MutationRequired ||
            plan.MutationAttempted ||
            string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new InvalidOperationException(
                "Setup-only identity schema planning did not produce one " +
                "allowed exact state.");
        }

        AetherIdentityDatabaseReport applied =
            await AetherIdentityDatabaseMigration.ApplyAsync(
                paths,
                plan.PlanId,
                cancellationToken);
        if (!string.Equals(applied.Outcome, "applied", StringComparison.Ordinal) ||
            !string.Equals(
                applied.Code,
                "identity-schema-initialized",
                StringComparison.Ordinal) ||
            applied.MutationRequired ||
            !applied.MutationAttempted ||
            !applied.DatabaseCreated)
        {
            throw new InvalidOperationException(
                "Setup-only identity schema initialization did not converge.");
        }
        return applied;
    }

    private static bool IsConverged(AetherIdentityDatabaseReport report) =>
        string.Equals(report.Outcome, "converged", StringComparison.Ordinal) &&
        string.Equals(
            report.Code,
            "identity-schema-converged",
            StringComparison.Ordinal) &&
        !report.MutationRequired &&
        !report.MutationAttempted &&
        !report.DatabaseCreated;
}
