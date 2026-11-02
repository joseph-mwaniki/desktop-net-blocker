namespace DesktopNetBlocker.Core.Models;

public enum MatchingMode
{
    Exact = 0,
    Subdomain = 1,
    Wildcard = 2
}

public enum DomainStatus
{
    Active = 0,
    SuspendedTemporaryUnlock = 1,
    Removed = 2
}

public enum RequestType
{
    Removal = 0,
    TemporaryUnlock = 1,
    PolicyModification = 2
}

public enum RequestStatus
{
    Locked = 0,
    Pending = 1,
    Confirmation = 2,
    FinalDelay = 3,
    Authorized = 4,
    Cancelled = 5,
    Expired = 6
}

public enum TemporaryAccessStatus
{
    Active = 0,
    Expired = 1,
    Revoked = 2
}

