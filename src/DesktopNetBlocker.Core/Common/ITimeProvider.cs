using System;

namespace DesktopNetBlocker.Core.Common;

public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public class DefaultTimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

