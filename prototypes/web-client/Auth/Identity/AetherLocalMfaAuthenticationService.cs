using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherLocalMfaAuthenticationResult(
    bool Succeeded,
    string Code,
    ClaimsPrincipal? Principal,
    Guid? SessionId,
    DateTimeOffset? AbsoluteExpiresAtUtc);

internal sealed record AetherLocalRecoveryCredential(
    string Code,
    IdentityUserToken<Guid> Token);

internal sealed class AetherLocalMfaCredentialProtector(
    IDataProtectionProvider dataProtectionProvider)
{
    internal const string LoginProvider = "Aether.LocalMfa";
    internal const string TotpSecretName = "TotpSecret.v1";
    internal const string TotpLastAcceptedStepName =
        "TotpLastAcceptedStep.v1";
    internal const string RecoveryCodeNamePrefix = "RecoveryCode.v1.";

    private readonly IDataProtector protector =
        dataProtectionProvider.CreateProtector(
            "AetherSDR.Web.Auth.LocalTotpSecret.v1");

    internal IdentityUserToken<Guid> CreateTotpSecretToken(
        Guid userId,
        ReadOnlySpan<byte> secret)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "A TOTP credential requires a canonical user identifier.",
                nameof(userId));
        }
        if (secret.Length != 20)
        {
            throw new ArgumentException(
                "A TOTP credential must contain exactly 160 random bits.",
                nameof(secret));
        }

        byte[] plaintextSecret = secret.ToArray();
        byte[] protectedSecret;
        try
        {
            protectedSecret = protector.Protect(plaintextSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextSecret);
        }

        try
        {
            return new()
            {
                UserId = userId,
                LoginProvider = LoginProvider,
                Name = TotpSecretName,
                Value = Convert.ToBase64String(protectedSecret)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedSecret);
        }
    }

    internal bool TryUnprotectTotpSecret(
        IdentityUserToken<Guid>? token,
        out byte[] secret)
    {
        secret = [];
        if (token is null ||
            !string.Equals(
                token.LoginProvider,
                LoginProvider,
                StringComparison.Ordinal) ||
            !string.Equals(
                token.Name,
                TotpSecretName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(token.Value) ||
            token.Value.Length > 4096)
        {
            return false;
        }

        byte[] protectedSecret;
        try
        {
            protectedSecret = Convert.FromBase64String(token.Value);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            byte[] candidate = protector.Unprotect(protectedSecret);
            if (candidate.Length != 20)
            {
                CryptographicOperations.ZeroMemory(candidate);
                return false;
            }
            secret = candidate;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedSecret);
        }
    }

    internal static AetherLocalRecoveryCredential
        GenerateRecoveryCredential(Guid userId)
    {
        byte[] randomCode = RandomNumberGenerator.GetBytes(16);
        try
        {
            string hexadecimal = Convert.ToHexString(randomCode);
            string code = string.Join(
                '-',
                Enumerable.Range(0, 8).Select(
                    index => hexadecimal.Substring(index * 4, 4)));
            return new(code, CreateRecoveryCodeToken(userId, code));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomCode);
        }
    }

    private static IdentityUserToken<Guid> CreateRecoveryCodeToken(
        Guid userId,
        string recoveryCode)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "A recovery credential requires a canonical user identifier.",
                nameof(userId));
        }
        if (!TryNormalizeRecoveryCode(recoveryCode, out string normalized))
        {
            throw new ArgumentException(
                "A recovery code must contain eight groups of four " +
                "hexadecimal characters.",
                nameof(recoveryCode));
        }

        return new()
        {
            UserId = userId,
            LoginProvider = LoginProvider,
            Name = RecoveryCodeNamePrefix +
                ComputeRecoveryCodeBinding(userId, normalized),
            Value = "active"
        };
    }

    internal static bool TryNormalizeRecoveryCode(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: 39 })
        {
            return false;
        }

        Span<char> buffer = stackalloc char[32];
        int target = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if ((index + 1) % 5 == 0)
            {
                if (value[index] != '-')
                {
                    return false;
                }
                continue;
            }

            char character = value[index];
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
            buffer[target++] = char.ToUpperInvariant(character);
        }
        if (target != buffer.Length)
        {
            return false;
        }

        normalized = new string(buffer);
        return true;
    }

    internal static string ComputeRecoveryCodeBinding(
        Guid userId,
        string normalizedRecoveryCode)
    {
        string payload = string.Join(
            '\n',
            "aethersdr-recovery-code-v1",
            userId.ToString("D", CultureInfo.InvariantCulture),
            normalizedRecoveryCode);
        byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);
        try
        {
            return Convert.ToHexStringLower(
                SHA256.HashData(payloadBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }
}

internal sealed class AetherLocalMfaAuthenticationService(
    AetherIdentityDbContext database,
    AetherLocalMfaChallengeStore challenges,
    AetherLocalMfaCredentialProtector credentialProtector,
    AetherLocalAuthenticationPolicy policy,
    TimeProvider timeProvider)
{
    private const string AuditAction = "authentication.local.mfa";
    private const int TotpDigits = 6;
    private const int TotpPeriodSeconds = 30;
    private const int TotpAllowedDriftSteps = 1;

    internal async Task<AetherLocalMfaAuthenticationResult> AuthenticateAsync(
        string? challengeToken,
        string? verificationCode,
        string correlationId,
        TimeSpan absoluteSessionLifetime,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationId(correlationId);
        ValidateSessionLifetime(absoluteSessionLifetime);

        if (!challenges.TryConsume(challengeToken, out AetherLocalMfaChallenge? challenge) ||
            challenge is null)
        {
            return await RejectAsync(
                subjectUserId: null,
                correlationId,
                "local-mfa-challenge-rejected",
                cancellationToken);
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await AuthenticateChallengeAsync(
                    challenge,
                    verificationCode,
                    correlationId,
                    absoluteSessionLifetime,
                    cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                database.ChangeTracker.Clear();
                return await RejectAsync(
                    subjectUserId: null,
                    correlationId,
                    "local-mfa-concurrency-rejected",
                    cancellationToken);
            }
        }
    }

    private async Task<AetherLocalMfaAuthenticationResult>
        AuthenticateChallengeAsync(
            AetherLocalMfaChallenge challenge,
            string? verificationCode,
            string correlationId,
            TimeSpan absoluteSessionLifetime,
            CancellationToken cancellationToken)
    {
        AetherIdentityUser? user = await database.Users
            .SingleOrDefaultAsync(
                candidate => candidate.Id == challenge.UserId,
                cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (user is null ||
            !user.Enabled ||
            !user.TwoFactorEnabled ||
            !user.LockoutEnabled ||
            user.AuthorityVersion != challenge.AuthorityVersion ||
            !string.Equals(
                user.SecurityStamp,
                challenge.SecurityStamp,
                StringComparison.Ordinal) ||
            user.LockoutEnd is DateTimeOffset lockoutEnd &&
            lockoutEnd > now)
        {
            return await RejectAsync(
                user?.Id,
                correlationId,
                "local-mfa-authority-rejected",
                cancellationToken);
        }

        bool isTotp = TryReadTotpCode(
            verificationCode,
            out string totpCode);
        bool isRecovery =
            AetherLocalMfaCredentialProtector.TryNormalizeRecoveryCode(
                verificationCode,
                out string normalizedRecoveryCode);
        if (!isTotp && !isRecovery)
        {
            return await RegisterFailureAsync(
                user,
                correlationId,
                "local-mfa-code-rejected",
                now,
                cancellationToken);
        }

        IdentityUserToken<Guid>? acceptedToken = null;
        long? acceptedTotpStep = null;
        AetherAuthenticationMethod authenticationMethod;
        if (isTotp)
        {
            IdentityUserToken<Guid>? secretToken =
                await FindTokenAsync(
                    user.Id,
                    AetherLocalMfaCredentialProtector.TotpSecretName,
                    cancellationToken);
            if (!credentialProtector.TryUnprotectTotpSecret(
                    secretToken,
                    out byte[] secret))
            {
                return await RegisterFailureAsync(
                    user,
                    correlationId,
                    "local-mfa-credential-rejected",
                    now,
                    cancellationToken);
            }

            try
            {
                acceptedTotpStep = FindMatchingTotpStep(
                    secret,
                    totpCode,
                    now);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            IdentityUserToken<Guid>? replayState =
                await FindTokenAsync(
                    user.Id,
                    AetherLocalMfaCredentialProtector
                        .TotpLastAcceptedStepName,
                    cancellationToken);
            if (acceptedTotpStep is null ||
                !IsNewTotpStep(replayState, acceptedTotpStep.Value))
            {
                return await RegisterFailureAsync(
                    user,
                    correlationId,
                    "local-mfa-code-rejected",
                    now,
                    cancellationToken);
            }
            acceptedToken = replayState;
            authenticationMethod =
                AetherAuthenticationMethod.LocalPasswordWithTotp;
        }
        else
        {
            string recoveryName =
                AetherLocalMfaCredentialProtector.RecoveryCodeNamePrefix +
                AetherLocalMfaCredentialProtector
                    .ComputeRecoveryCodeBinding(
                        user.Id,
                        normalizedRecoveryCode);
            acceptedToken = await FindTokenAsync(
                user.Id,
                recoveryName,
                cancellationToken);
            if (acceptedToken is null)
            {
                return await RegisterFailureAsync(
                    user,
                    correlationId,
                    "local-mfa-code-rejected",
                    now,
                    cancellationToken);
            }
            authenticationMethod =
                AetherAuthenticationMethod.LocalPasswordWithRecoveryCode;
        }

        string[] roles = await (
            from userRole in database.Set<IdentityUserRole<Guid>>()
            join role in database.Roles
                on userRole.RoleId equals role.Id
            where userRole.UserId == user.Id && role.Name != null
            orderby role.Name
            select role.Name!)
            .ToArrayAsync(cancellationToken);

        if (acceptedTotpStep is long step)
        {
            if (acceptedToken is null)
            {
                database.Set<IdentityUserToken<Guid>>().Add(
                    new()
                    {
                        UserId = user.Id,
                        LoginProvider =
                            AetherLocalMfaCredentialProtector.LoginProvider,
                        Name = AetherLocalMfaCredentialProtector
                            .TotpLastAcceptedStepName,
                        Value = step.ToString(CultureInfo.InvariantCulture)
                    });
            }
            else
            {
                acceptedToken.Value =
                    step.ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            database.Set<IdentityUserToken<Guid>>().Remove(
                acceptedToken!);
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        AetherAuthenticationSession session = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            User = user,
            AuthenticationMethod = authenticationMethod,
            ProviderId = null,
            AuthorityVersion = user.AuthorityVersion,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            AbsoluteExpiresAtUtc = now.Add(absoluteSessionLifetime),
            ReauthenticatedAtUtc = now
        };
        ClaimsPrincipal principal = AetherCanonicalPrincipalFactory.Create(
            user,
            session,
            roles,
            now);

        database.AuthenticationSessions.Add(session);
        AddAudit(
            actorUserId: user.Id,
            subjectUserId: user.Id,
            correlationId,
            AetherIdentityAuditOutcome.Succeeded,
            now,
            "local-mfa-authenticated",
            authenticationMethod,
            session.Id,
            failedAttempts: 0,
            lockoutEndUtc: null);
        await database.SaveChangesAsync(cancellationToken);

        return new(
            Succeeded: true,
            Code: "local-mfa-authenticated",
            principal,
            session.Id,
            session.AbsoluteExpiresAtUtc);
    }

    private async Task<AetherLocalMfaAuthenticationResult>
        RegisterFailureAsync(
            AetherIdentityUser user,
            string correlationId,
            string internalCode,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        if (user.AccessFailedCount < 0 ||
            user.AccessFailedCount >= policy.MaximumFailedAttempts)
        {
            AddAudit(
                actorUserId: null,
                subjectUserId: user.Id,
                correlationId,
                AetherIdentityAuditOutcome.Failed,
                now,
                "local-mfa-state-invalid",
                authenticationMethod: null,
                sessionId: null,
                user.AccessFailedCount,
                user.LockoutEnd);
            await database.SaveChangesAsync(cancellationToken);
            return Rejected();
        }

        int failedAttempts = user.AccessFailedCount + 1;
        string auditCode = internalCode;
        if (failedAttempts >= policy.MaximumFailedAttempts)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = now.Add(policy.LockoutDuration);
            auditCode = "local-mfa-lockout-started";
        }
        else
        {
            user.AccessFailedCount = failedAttempts;
            user.LockoutEnd = null;
        }
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        AddAudit(
            actorUserId: null,
            subjectUserId: user.Id,
            correlationId,
            AetherIdentityAuditOutcome.Rejected,
            now,
            auditCode,
            authenticationMethod: null,
            sessionId: null,
            failedAttempts,
            user.LockoutEnd);
        await database.SaveChangesAsync(cancellationToken);
        return Rejected();
    }

    private async Task<AetherLocalMfaAuthenticationResult> RejectAsync(
        Guid? subjectUserId,
        string correlationId,
        string internalCode,
        CancellationToken cancellationToken)
    {
        AddAudit(
            actorUserId: null,
            subjectUserId,
            correlationId,
            AetherIdentityAuditOutcome.Rejected,
            timeProvider.GetUtcNow(),
            internalCode,
            authenticationMethod: null,
            sessionId: null,
            failedAttempts: null,
            lockoutEndUtc: null);
        await database.SaveChangesAsync(cancellationToken);
        return Rejected();
    }

    private async Task<IdentityUserToken<Guid>?> FindTokenAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken) =>
        await database.Set<IdentityUserToken<Guid>>()
            .SingleOrDefaultAsync(
                token =>
                    token.UserId == userId &&
                    token.LoginProvider ==
                        AetherLocalMfaCredentialProtector.LoginProvider &&
                    token.Name == name,
                cancellationToken);

    private void AddAudit(
        Guid? actorUserId,
        Guid? subjectUserId,
        string correlationId,
        AetherIdentityAuditOutcome outcome,
        DateTimeOffset occurredAt,
        string code,
        AetherAuthenticationMethod? authenticationMethod,
        Guid? sessionId,
        int? failedAttempts,
        DateTimeOffset? lockoutEndUtc)
    {
        database.IdentityAuditRecords.Add(new AetherIdentityAuditRecord
        {
            OccurredAtUtc = occurredAt,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            Action = AuditAction,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailJson = JsonSerializer.Serialize(
                new
                {
                    code,
                    authenticationMethod,
                    sessionId,
                    failedAttempts,
                    lockoutEndUtc
                })
        });
    }

    private static long? FindMatchingTotpStep(
        ReadOnlySpan<byte> secret,
        string code,
        DateTimeOffset now)
    {
        long currentStep = now.ToUnixTimeSeconds() / TotpPeriodSeconds;
        ReadOnlySpan<int> driftSteps = [0, -1, TotpAllowedDriftSteps];
        foreach (int drift in driftSteps)
        {
            long candidate = currentStep + drift;
            if (candidate >= 0 &&
                FixedTimeCodeEquals(
                    ComputeTotp(secret, candidate),
                    code))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string ComputeTotp(
        ReadOnlySpan<byte> secret,
        long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(secret, counter);
        try
        {
            int offset = hash[^1] & 0x0f;
            int binaryCode =
                ((hash[offset] & 0x7f) << 24) |
                ((hash[offset + 1] & 0xff) << 16) |
                ((hash[offset + 2] & 0xff) << 8) |
                (hash[offset + 3] & 0xff);
            return (binaryCode % 1_000_000).ToString(
                $"D{TotpDigits}",
                CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static bool FixedTimeCodeEquals(
        string expected,
        string actual)
    {
        byte[] expectedBytes = Encoding.ASCII.GetBytes(expected);
        byte[] actualBytes = Encoding.ASCII.GetBytes(actual);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static bool IsNewTotpStep(
        IdentityUserToken<Guid>? replayState,
        long acceptedStep)
    {
        if (replayState is null)
        {
            return true;
        }
        return long.TryParse(
                replayState.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long previousStep) &&
            previousStep >= 0 &&
            acceptedStep > previousStep;
    }

    private static bool TryReadTotpCode(
        string? value,
        out string code)
    {
        code = string.Empty;
        if (value is not { Length: TotpDigits } ||
            value.Any(character => character is < '0' or > '9'))
        {
            return false;
        }
        code = value;
        return true;
    }

    private static AetherLocalMfaAuthenticationResult Rejected() =>
        new(
            Succeeded: false,
            Code: "local-mfa-rejected",
            Principal: null,
            SessionId: null,
            AbsoluteExpiresAtUtc: null);

    private static void ValidateSessionLifetime(TimeSpan value)
    {
        if (value < TimeSpan.FromMinutes(5) ||
            value > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The local session lifetime must be between 5 minutes and " +
                "24 hours.");
        }
    }

    private static void ValidateCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 100 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The local MFA correlation identifier must be an exact " +
                "value of at most 100 characters.",
                nameof(value));
        }
    }
}
