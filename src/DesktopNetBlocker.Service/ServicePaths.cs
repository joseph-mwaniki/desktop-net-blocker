using System;
using System.IO;

namespace DesktopNetBlocker.Service;

public static class ServicePaths
{
    public static string GetDatabasePath()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            baseDir = Path.Combine(programData, "DesktopNetBlocker");
        }
        else
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-net-blocker");
        }

        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        return Path.Combine(baseDir, "blocker.db");
    }
}

