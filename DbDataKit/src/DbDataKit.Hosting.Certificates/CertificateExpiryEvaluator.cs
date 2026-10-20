namespace DbDataKit.Hosting.Certificates;

/// <summary>What today's expiry check should raise, if anything.</summary>
public enum CertificateExpiryDecision
{
    None,
    Expiring,
    Expired,
}

/// <summary>
/// Pure decision logic for a daily certificate expiry check. Deliberately has no dependency on
/// <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>, a notification sink, or
/// the clock beyond what is passed in, so each scenario (31 days out raises nothing, 29 days raises once,
/// a second check the same day raises nothing further, past NotAfter raises Expired) is one call.
/// <para>
/// **The caller owns "once a day."** This method is given the date it last raised each kind (null if
/// never) and decides fresh each call — it does not remember anything itself. A caller that keeps that
/// state in memory can produce one extra notification after a mid-day restart; that is a courtesy-cap
/// tradeoff, not a guarantee worth a persisted cursor.
/// </para>
/// </summary>
public static class CertificateExpiryEvaluator
{
    public static CertificateExpiryDecision Evaluate(
        DateTimeOffset notAfter, int warningDays, DateTimeOffset now,
        DateOnly? lastRaisedExpiringUtc, DateOnly? lastRaisedExpiredUtc)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (now >= notAfter)
            return lastRaisedExpiredUtc == today ? CertificateExpiryDecision.None : CertificateExpiryDecision.Expired;

        var daysRemaining = (notAfter - now).TotalDays;
        if (daysRemaining <= warningDays)
        {
            return lastRaisedExpiringUtc == today
                ? CertificateExpiryDecision.None
                : CertificateExpiryDecision.Expiring;
        }

        return CertificateExpiryDecision.None;
    }
}
